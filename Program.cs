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
    .WithNotParsed(errors =>
    {
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
        var rawDownload = File.ReadAllText(result.Value.Account);
        PrintDebugInfo($"成功读取账号文件，文件长度: {rawDownload.Length} 个字符。");
        PrintDebugInfo($"账号文件原始内容:\n{rawDownload}");

        Dictionary<string, List<string?>>? accountDownloadJson = null;

        try
        {
            accountDownloadJson = JsonConvert.DeserializeObject<Dictionary<string, List<string?>>>(rawDownload);
            PrintDebugInfo($"解析后的账号 JSON 内容:\n{JsonConvert.SerializeObject(accountDownloadJson, Formatting.Indented)}");

            if (accountDownloadJson == null || accountDownloadJson.Count == 0)
            {
                Console.WriteLine($"[错误] 解析账号文件失败或没有找到任何账号。");
                Environment.Exit(1);
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"[错误] 解析账号 JSON 文件失败: {e.Message}");
            Environment.Exit(1);
        }

        var totalDownload = accountDownloadJson.Count;
        var indexDownload = 0;

        foreach (var accountPair in accountDownloadJson)
        {
            await semaphore.WaitAsync();

            PrintDebugInfo($"[{indexDownload++}/{totalDownload}] 分发账号: {accountPair.Key}...");
            tasks.Add(Task.Run(async () =>
            {
                var downloader = new ManifestDownloader(new AccountInfoCallback(
                    accountPair.Key,
                    accountPair.Value.FirstOrDefault()
                ));

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
        var summaryPathDownload = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (summaryPathDownload != null)
        {
            try
            {
                var summaryFileDownload = File.OpenWrite(summaryPathDownload);
                summaryFileDownload.Write(Encoding.UTF8.GetBytes(gdb.ReportTrackingStatus()));
                summaryFileDownload.Close();
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
        // account 模式逻辑保持不变
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
