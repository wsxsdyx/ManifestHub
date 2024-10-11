using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using ManifestHub;
using CommandLine;
using Newtonsoft.Json;
using SteamKit2;
using SteamKit2.Authentication;
using System.Security.Cryptography;
using System.Text;

// 定义调试输出的帮助方法
void PrintDebugInfo(string message)
{
    Console.WriteLine($"[调试] {message}");
}

var result = Parser.Default.ParseArguments<Options>(args)
    .WithNotParsed(errors => {
        foreach (var error in errors)
        {
            Console.WriteLine($"[错误] 参数解析失败: {error}");
        }

        Environment.Exit(1);
    });

// 输出解析的参数
PrintDebugInfo($"成功解析参数。模式: {result.Value.Mode}");

// 初始化数据库对象
var gdb = new GitDatabase(".", result.Value.Token ?? throw new NullReferenceException(),
    result.Value.Key ?? throw new NullReferenceException());

var semaphore = new SemaphoreSlim(result.Value.ConcurrentAccount);
var tasks = new ConcurrentBag<Task>();
var writeTasks = new ConcurrentBag<Task>();

switch (result.Value.Mode)
{
    case "download":
        var index = 0;
        var total = gdb.GetAccounts().Count();

        foreach (var accountInfo in gdb.GetAccounts(true))
        {
            await semaphore.WaitAsync();

            PrintDebugInfo($"[{index++}/{total}] 分发账号: {accountInfo.AccountName}...");
            tasks.Add(Task.Run(async () =>
            {
                var downloader = new ManifestDownloader(accountInfo);
                try
                {
                    await downloader.Connect().ConfigureAwait(false);
                    var info = await downloader.GetAccountInfo();
                    await gdb.WriteAccount(info);
                    await downloader.DownloadAllManifestsAsync(result.Value.ConcurrentManifest, gdb, writeTasks)
                        .ConfigureAwait(false);
                }
                catch (AuthenticationException e) when (e.Result is
                                                            EResult.AccessDenied
                                                            or EResult.AccountLogonDeniedVerifiedEmailRequired
                                                            or EResult.AccountLoginDeniedNeedTwoFactor
                                                            or EResult.AccountDisabled
                                                            or EResult.InvalidPassword)
                {
                    await gdb.RemoveAccount(accountInfo);
                    Console.WriteLine($"[警告] {e.Result} 对于账号 {downloader.Username}。已删除。");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[错误] 处理账号 {downloader.Username} 时发生异常: {e.Message}");
                }
                finally
                {
                    _ = downloader.Disconnect();
                    semaphore.Release();
                }
            }));
        }

        await Task.WhenAll(tasks);
        PrintDebugInfo("所有下载任务已完成。正在等待写入任务...");
        await Task.WhenAll(writeTasks);
        PrintDebugInfo("开始清理旧的标签...");
        await gdb.PruneExpiredTags();

        PrintDebugInfo("写入汇总信息...");
        var summaryPath = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (summaryPath != null)
        {
            try
            {
                var summaryFile = File.OpenWrite(summaryPath);
                summaryFile.Write(Encoding.UTF8.GetBytes(gdb.ReportTrackingStatus()));
                summaryFile.Close();
                PrintDebugInfo("已将汇总信息写入 GITHUB_STEP_SUMMARY。");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[错误] 无法写入汇总信息至 GITHUB_STEP_SUMMARY: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine("[警告] 找不到 GITHUB_STEP_SUMMARY 环境变量。");
        }

        Console.WriteLine("[信息] 下载模式执行完毕。");
        break;

    case "account":
        // 检查文件是否存在
        if (!File.Exists(result.Value.Account))
        {
            Console.WriteLine($"[错误] 账号文件 '{result.Value.Account}' 不存在。");
            Environment.Exit(1);
        }
        else
        {
            PrintDebugInfo($"账号文件 '{result.Value.Account}' 存在。");
        }

        // 读取账号文件内容
        var raw = File.ReadAllText(result.Value.Account);
        PrintDebugInfo($"成功读取账号文件，文件长度: {raw.Length} 个字符。");
        PrintDebugInfo($"账号文件原始内容:\n{raw}");

        KeyValuePair<string, List<string?>>[] account;

        try
        {
            var accountJson = JsonConvert.DeserializeObject<Dictionary<string, List<string?>>>(raw);
            PrintDebugInfo($"解析后的账号 JSON 内容:\n{JsonConvert.SerializeObject(accountJson, Formatting.Indented)}");
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
                PrintDebugInfo($"加载已有账号信息: {infoPrev.AccountName}");
            }
            else
            {
                downloader = new ManifestDownloader(new AccountInfoCallback(
                    account[i].Key,
                    account[i].Value.FirstOrDefault()
                ));
                PrintDebugInfo($"创建新账号信息: {account[i].Key}");
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
        break;

    default:
        Console.WriteLine($"[错误] 无效的操作模式: {result.Value.Mode}。");
        Environment.Exit(1);
        break;
}

namespace ManifestHub
{
    [SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
    internal class Options
    {
        [Value(0, MetaName = "Mode", Default = "download", HelpText = "Mode of operation.")]
        public string? Mode { get; set; }

        [Option('a', "account", Required = false, HelpText = "Account file.")]
        public string? Account { get; set; }

        [Option('t', "token", Required = true, HelpText = "GitHub Access Token.")]
        public string? Token { get; set; }

        [Option('c', "concurrent-account", Required = false, HelpText = "Concurrent account.", Default = 4)]
        public int ConcurrentAccount { get; set; }

        [Option('p', "concurrent-manifest", Required = false, HelpText = "Concurrent manifest.", Default = 16)]
        public int ConcurrentManifest { get; set; }

        [Option('i', "index", Required = false, HelpText = "Index of instance.", Default = 0)]
        public int Index { get; set; }

        [Option('n', "number", Required = false, HelpText = "Number of instances.", Default = 1)]
        public int Number { get; set; }

        [Option('k', "key", Required = false, HelpText = "Encryption key.")]
        public string? Key { get; set; }
    }
}
