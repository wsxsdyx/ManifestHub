using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using ManifestHub;
using CommandLine;
using Newtonsoft.Json;
using SteamKit2;
using SteamKit2.Authentication;
using System.Security.Cryptography;
using System.Text;

var result = Parser.Default.ParseArguments<Options>(args)
    .WithNotParsed(errors =>
    {
        foreach (var error in errors)
        {
            Console.WriteLine($"[错误] 参数解析失败: {error}");
        }
        Environment.Exit(1);
    });

Console.WriteLine($"[信息] 成功解析参数。模式: {result.Value.Mode}");

var gdb = new GitDatabase(".", result.Value.Token ?? throw new NullReferenceException("[错误] 没有提供 Token"),
    result.Value.Key ?? throw new NullReferenceException("[错误] 没有提供 AES Key"));

var semaphore = new SemaphoreSlim(result.Value.ConcurrentAccount);
var tasks = new ConcurrentBag<Task>();
var writeTasks = new ConcurrentBag<Task>();

switch (result.Value.Mode)
{
    case "download":
        var index = 0;
        var total = gdb.GetAccounts().Count();

        Console.WriteLine($"[信息] 找到 {total} 个待处理账号。");

        foreach (var accountInfo in gdb.GetAccounts(true))
        {
            await semaphore.WaitAsync();

            Console.WriteLine($"[信息] [{index++}/{total}] 正在处理账号: {accountInfo.AccountName}");

            tasks.Add(Task.Run(async () =>
            {
                var downloader = new ManifestDownloader(accountInfo);
                try
                {
                    await downloader.Connect().ConfigureAwait(false);
                    var info = await downloader.GetAccountInfo();
                    Console.WriteLine($"[信息] 账号 {accountInfo.AccountName} 连接成功。");

                    await gdb.WriteAccount(info);
                    Console.WriteLine($"[信息] 账号 {accountInfo.AccountName} 已写入数据库。");

                    await downloader.DownloadAllManifestsAsync(result.Value.ConcurrentManifest, gdb, writeTasks).ConfigureAwait(false);
                    Console.WriteLine($"[信息] 已下载账号 {accountInfo.AccountName} 的所有 Manifest。");
                }
                catch (AuthenticationException e) when (e.Result is EResult.AccessDenied or EResult.AccountLogonDeniedVerifiedEmailRequired or EResult.AccountLoginDeniedNeedTwoFactor or EResult.AccountDisabled or EResult.InvalidPassword)
                {
                    await gdb.RemoveAccount(accountInfo);
                    Console.WriteLine($"[警告] {e.Result} - 账号 {downloader.Username} 被移除。");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[错误] 处理账号 {accountInfo.AccountName} 时发生异常: {e.Message}");
                }
                finally
                {
                    await downloader.Disconnect();
                    Console.WriteLine($"[信息] 账号 {accountInfo.AccountName} 已断开连接。");
                    semaphore.Release();
                }
            }));
        }

        await Task.WhenAll(tasks);
        Console.WriteLine("[信息] 所有下载任务已完成。正在等待写入任务...");
        await Task.WhenAll(writeTasks);

        Console.WriteLine("[信息] 开始清理旧的标签...");
        await gdb.PruneExpiredTags();

        Console.WriteLine("[信息] 写入汇总信息...");
        var summaryPath = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (summaryPath != null)
        {
            var summaryFile = File.OpenWrite(summaryPath);
            summaryFile.Write(Encoding.UTF8.GetBytes(gdb.ReportTrackingStatus()));
            summaryFile.Close();
            Console.WriteLine("[信息] 已将汇总信息写入 GITHUB_STEP_SUMMARY。");
        }
        else
        {
            Console.WriteLine("[警告] 找不到 GITHUB_STEP_SUMMARY 环境变量。");
        }

        Console.WriteLine("[信息] 下载模式执行完毕。");
        break;

    case "account":
        Console.WriteLine($"[信息] 进入账号模式，正在处理账号文件: {result.Value.Account}");

        // 检查文件是否存在
        if (!File.Exists(result.Value.Account))
        {
            Console.WriteLine($"[错误] 账号文件 '{result.Value.Account}' 不存在。");
            Environment.Exit(1);
        }

        var raw = File.ReadAllText(result.Value.Account);
        Console.WriteLine($"[信息] 成功读取账号文件，文件长度: {raw.Length} 个字符。");

        // 检查文件是否为空
        if (string.IsNullOrWhiteSpace(raw))
        {
            Console.WriteLine("[错误] 账号文件内容为空。");
            Environment.Exit(1);
        }

        // 检查是否为加密文件
        try
        {
            var dictionary = JsonConvert.DeserializeObject<Dictionary<string, string>>(raw);
            var encryptedAccount = dictionary?["payload"];
            var rsa = new RSACryptoServiceProvider();
            var rsaPrivateKey = Environment.GetEnvironmentVariable("RSA_PRIVATE_KEY");

            rsa.ImportFromPem(rsaPrivateKey);

            var decryptedBytes = rsa.Decrypt(Convert.FromBase64String(encryptedAccount!), true);
            raw = Encoding.UTF8.GetString(decryptedBytes);
            Console.WriteLine("[信息] 成功解密账号文件。");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[警告] 解密账号文件失败: {e.Message}");
        }

        KeyValuePair<string, List<string?>>[] account;

        try
        {
            var accountJson = JsonConvert.DeserializeObject<Dictionary<string, List<string?>>>(raw);
            Console.WriteLine($"[调试] 解析后的账号 JSON 内容: {JsonConvert.SerializeObject(accountJson, Formatting.Indented)}");
            account = accountJson!.ToArray();
            Console.WriteLine($"[信息] 成功解析账号文件，共 {account.Length} 个账号。");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[错误] 解析账号 JSON 文件失败: {e.Message}");
            account = Array.Empty<KeyValuePair<string, List<string?>>>();
            Environment.Exit(1);
        }

        for (var i = result.Value.Index; i < account.Length; i += result.Value.Number)
        {
            var infoPrev = gdb.GetAccount(account[i].Key);

            ManifestDownloader downloader;
            if (infoPrev != null)
            {
                infoPrev.AccountPassword = account[i].Value.FirstOrDefault();
                downloader = new ManifestDownloader(infoPrev);
            }
            else
            {
                downloader = new ManifestDownloader(new AccountInfoCallback(account[i].Key, account[i].Value.FirstOrDefault()));
            }

            await semaphore.WaitAsync();
            Console.WriteLine($"[信息] 正在处理账号 {account[i].Key}...");

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await downloader.Connect().ConfigureAwait(false);
                    var info = await downloader.GetAccountInfo();
                    Console.WriteLine($"[信息] 账号 {account[i].Key} 连接成功。");

                    if (infoPrev == null || info.RefreshToken != infoPrev.RefreshToken)
                    {
                        await gdb.WriteAccount(info);
                        Console.WriteLine($"[信息] 账号 {account[i].Key} 已写入数据库。");
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[错误] 处理账号 {account[i].Key} 时发生异常: {e.Message}");
                }
                finally
                {
                    await downloader.Disconnect();
                    Console.WriteLine($"[信息] 账号 {account[i].Key} 已断开连接。");
                    semaphore.Release();
                }
            }));
        }

        await Task.WhenAll(tasks);
        Console.WriteLine("[信息] 所有账号任务已完成。");

        break;

    default:
        Console.WriteLine("[错误] 无效的操作模式。");
        Environment.Exit(1);
        break;
}

namespace ManifestHub
{
    [SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
    internal class Options
    {
        [Value(0, MetaName = "Mode", Default = "download", HelpText = "操作模式.")]
        public string? Mode { get; set; }

        [Option('a', "account", Required = false, HelpText = "账号文件.")]
        public string? Account { get; set; }

        [Option('t', "token", Required = true, HelpText = "GitHub 访问令牌.")]
        public string? Token { get; set; }

        [Option('c', "concurrent-account", Required = false, HelpText = "同时处理的账号数量.", Default = 4)]
        public int ConcurrentAccount { get; set; }

        [Option('p', "concurrent-manifest", Required = false, HelpText = "同时处理的 Manifest 数量.", Default = 16)]
        public int ConcurrentManifest { get; set; }

        [Option('i', "index", Required = false, HelpText = "实例索引.", Default = 0)]
        public int Index { get; set; }

        [Option('n', "number", Required = false, HelpText = "实例数量.", Default = 1)]
        public int Number { get; set; }

        [Option('k', "key", Required = false, HelpText = "加密密钥.")]
        public string? Key { get; set; }
    }
}
