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
            Console.WriteLine($"[Error] Argument Parsing Error: {error}");
        }
        Environment.Exit(1);
    });

Console.WriteLine($"[Info] Successfully parsed arguments. Mode: {result.Value.Mode}");

var gdb = new GitDatabase(".", result.Value.Token ?? throw new NullReferenceException("[Error] Token not provided"),
    result.Value.Key ?? throw new NullReferenceException("[Error] AES Key not provided"));

var semaphore = new SemaphoreSlim(result.Value.ConcurrentAccount);
var tasks = new ConcurrentBag<Task>();
var writeTasks = new ConcurrentBag<Task>();

switch (result.Value.Mode)
{
    case "download":
        var index = 0;
        var total = gdb.GetAccounts().Count();

        Console.WriteLine($"[Info] Found {total} accounts to process.");

        foreach (var accountInfo in gdb.GetAccounts(true))
        {
            await semaphore.WaitAsync();

            Console.WriteLine($"[Info] [{index++}/{total}] Dispatching account: {accountInfo.AccountName}");

            tasks.Add(Task.Run(async () =>
            {
                var downloader = new ManifestDownloader(accountInfo);
                try
                {
                    await downloader.Connect().ConfigureAwait(false);
                    var info = await downloader.GetAccountInfo();
                    Console.WriteLine($"[Info] Account {accountInfo.AccountName} connected successfully.");

                    await gdb.WriteAccount(info);
                    Console.WriteLine($"[Info] Account {accountInfo.AccountName} written to database.");

                    await downloader.DownloadAllManifestsAsync(result.Value.ConcurrentManifest, gdb, writeTasks).ConfigureAwait(false);
                    Console.WriteLine($"[Info] Manifests downloaded for account {accountInfo.AccountName}.");
                }
                catch (AuthenticationException e) when (e.Result is EResult.AccessDenied or EResult.AccountLogonDeniedVerifiedEmailRequired or EResult.AccountLoginDeniedNeedTwoFactor or EResult.AccountDisabled or EResult.InvalidPassword)
                {
                    await gdb.RemoveAccount(accountInfo);
                    Console.WriteLine($"[Warning] {e.Result} for {downloader.Username}. Account removed.");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[Error] Exception for account {accountInfo.AccountName}: {e.Message}");
                }
                finally
                {
                    await downloader.Disconnect();
                    Console.WriteLine($"[Info] Account {accountInfo.AccountName} disconnected.");
                    semaphore.Release();
                }
            }));
        }

        await Task.WhenAll(tasks);
        Console.WriteLine("[Info] All download tasks completed. Waiting for write tasks...");
        await Task.WhenAll(writeTasks);

        Console.WriteLine("[Info] Start tag pruning...");
        await gdb.PruneExpiredTags();

        Console.WriteLine("[Info] Writing summary...");
        var summaryPath = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (summaryPath != null)
        {
            var summaryFile = File.OpenWrite(summaryPath);
            summaryFile.Write(Encoding.UTF8.GetBytes(gdb.ReportTrackingStatus()));
            summaryFile.Close();
            Console.WriteLine("[Info] Summary written to GITHUB_STEP_SUMMARY.");
        }
        else
        {
            Console.WriteLine("[Warning] GITHUB_STEP_SUMMARY environment variable not found.");
        }

        Console.WriteLine("[Info] Done.");
        break;

    case "account":
        Console.WriteLine($"[Info] Processing account mode with file: {result.Value.Account}");

        var raw = File.ReadAllText(result.Value.Account ?? throw new NullReferenceException("[Error] Account file path not provided."));

        Console.WriteLine($"[Info] Read account file contents: {raw.Length} characters.");

        // Detect if the account file is encrypted
        try
        {
            var dictionary = JsonConvert.DeserializeObject<Dictionary<string, string>>(raw);
            var encryptedAccount = dictionary?["payload"];
            var rsa = new RSACryptoServiceProvider();
            var rsaPrivateKey = Environment.GetEnvironmentVariable("RSA_PRIVATE_KEY");

            rsa.ImportFromPem(rsaPrivateKey);

            var decryptedBytes = rsa.Decrypt(Convert.FromBase64String(encryptedAccount!), true);
            raw = Encoding.UTF8.GetString(decryptedBytes);
            Console.WriteLine("[Info] Successfully decrypted account file.");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[Error] Failed to decrypt account file: {e.Message}");
        }

        KeyValuePair<string, List<string?>>[] account;

        try
        {
            var accountJson = JsonConvert.DeserializeObject<Dictionary<string, List<string?>>>(raw);
            account = accountJson!.ToArray();
            Console.WriteLine($"[Info] Successfully parsed {account.Length} accounts from the JSON file.");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[Error] Failed to parse account JSON file: {e.Message}");
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
            Console.WriteLine($"[Info] Dispatching account {account[i].Key}...");

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await downloader.Connect().ConfigureAwait(false);
                    var info = await downloader.GetAccountInfo();
                    Console.WriteLine($"[Info] Account {account[i].Key} connected successfully.");

                    if (infoPrev == null || info.RefreshToken != infoPrev.RefreshToken)
                    {
                        await gdb.WriteAccount(info);
                        Console.WriteLine($"[Info] Account {account[i].Key} written to database.");
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[Error] Exception for account {account[i].Key}: {e.Message}");
                }
                finally
                {
                    await downloader.Disconnect();
                    Console.WriteLine($"[Info] Account {account[i].Key} disconnected.");
                    semaphore.Release();
                }
            }));
        }

        await Task.WhenAll(tasks);
        Console.WriteLine("[Info] All account tasks completed.");

        break;

    default:
        Console.WriteLine("[Error] Invalid mode of operation.");
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
