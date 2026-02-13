using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.IO;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;
using System.Reflection;
using RestSharp;
using DotNetEnv;
namespace IGMediaDownloaderV2
{

    internal class Program
    {
        internal static MessageStore Store = new MessageStore("processed_messages.db");
        public static RestClient IGRestClient = new RestClient("https://i.instagram.com/");
        public static RestClient FBRestClient = new RestClient("https://rupload.facebook.com/");
        public static string Username = "", Password = "", Authorization = "", SessionId = "";
        public static int GoodReqs = 0, BadReqs = 0, ProcessedMsgs = 0;
        public static List<string> Timestamps = new List<string>();
        public const string IgUserAgent = "Instagram 361.0.0.46.88 Android (31/12; 640dpi; 1644x3840; 674675155)";
        public const string IgAppId = "567067343352427";
        public static readonly long PollMentionDelayMS =
               long.TryParse(
                   Environment.GetEnvironmentVariable("POLL_MENTIONS_DELAY_MS"),
                   out var v
               )
               ? v
               : 300000; // Fallback to 5 minutes
        
        // App startup timestamp in microseconds (Instagram format)
        public static long AppStartupTimestamp { get; private set; }

        static void Main(string[] args)
        {
            // Capture app startup timestamp immediately (in microseconds)
            AppStartupTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
            
            try
            {
                DotNetEnv.Env.Load();
                var filePath = Environment.GetEnvironmentVariable("AUTH_STORE_PATH") ?? "Auth.txt";
                var filePath2 = Environment.GetEnvironmentVariable("SESSION_STORE_PATH") ?? "Session.txt";

                if (File.Exists(filePath) && File.Exists(filePath2))
                {
                    Program.Authorization = File.ReadAllText(filePath); // Read saved Authorization token
                    Program.SessionId = File.ReadAllText(filePath2); // Read saved SessionId
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to load environment variables or authorization file.");
                Logger.Error($"Error: {ex.Message}");
                Environment.Exit(1);
            }

            MainAsync().GetAwaiter().GetResult();
            Console.ReadLine();
        }

        public static async Task MainAsync()
        {
            if (Program.Authorization.Length < 10 || Program.SessionId.Length < 10 || await LoginClass.IsValidTokens(Program.Authorization, Program.SessionId) == false)
            {
                Username = Environment.GetEnvironmentVariable("USERNAME") ?? "";
                Password = Environment.GetEnvironmentVariable("PASSWORD") ?? "";
                if (!await LoginClass.Login(Username, Password))
                {
                    Logger.Error("Login Failed");
                    Environment.Exit(1);
                }
            }
            Logger.Success("Logged In Successfully!");
            Logger.Info($"App started at {DateTimeOffset.FromUnixTimeMilliseconds(AppStartupTimestamp / 1000).ToString("yyyy-MM-dd HH:mm:ss")} UTC");
            Logger.Info($"Ignoring all messages before startup timestamp: {AppStartupTimestamp}");

            IGRestClient.AddDefaultHeader("Authorization", Authorization);
            FBRestClient.AddDefaultHeader("Authorization", Authorization);


            var DMReqsThrd = new Thread(DMReqs) { Priority = ThreadPriority.AboveNormal };
            DMReqsThrd.Start();
            var CounterThrd = new Thread(Counter) { Priority = ThreadPriority.AboveNormal };
            CounterThrd.Start();


            var DMResponse = "";
            while (true)
            {

                try
                {
                    DMResponse = await DMClass.CheckDMAPI(Authorization);
                    if (DMResponse.Contains(@"""status"":""ok"""))
                    {
                        Logger.Info($"[{Program.GoodReqs}] Direct messages checked at {DateTime.Now.ToString("HH:mm:ss")}");
                        GoodReqs++;
                        await DMClass.DMResponseProcess(DMResponse);
                    }
                    else
                    {
                        Logger.Warn($"Direct messages check failed at {DateTime.Now.ToString("HH:mm:ss")}");
                        BadReqs++;
                        int FAILdelayMs = int.TryParse(
                                                        Environment.GetEnvironmentVariable("FAIL_POLL_MSGS_DELAY_MS"),
                                                        out var failValue)
                                                        ? failValue
                                                        : 180_000; // default 180 seconds

                    }

                }
                catch { }

                try
                {
                    string JSONResponse = await DMClass.CheckActivityFeedAPI();
                    if (JSONResponse.Contains(@"""status"":""ok"""))
                    {
                        Logger.Info($"[{Program.GoodReqs}] Activity Feeds (mentions) checked at {DateTime.Now.ToString("HH:mm:ss")}");
                        GoodReqs++;
                        await DMClass.ActivityFeedProcess(JSONResponse);
                    }
                    else
                    {
                        Logger.Warn($" Activity Feeds (mentions) check failed at {DateTime.Now.ToString("HH:mm:ss")}");
                        BadReqs++;
                        int FAILdelayMs = int.TryParse(
                                                        Environment.GetEnvironmentVariable("FAIL_POLL_MSGS_DELAY_MS"),
                                                        out var failValue)
                                                        ? failValue
                                                        : 180_000; // default 180 seconds

                        Thread.Sleep(FAILdelayMs);
                    }

                }
                catch { }


                int delayMs = int.TryParse(
                                            Environment.GetEnvironmentVariable("POLL_MSGS_DELAY_MS"),
                                            out var value)
                                            ? value
                                            : 30_000; // default 30 seconds

                Thread.Sleep(delayMs);

            }


        }

        public static async void DMReqs()
        {
            var DMReqsResponse = "";
            while (true)
            {
                try
                {

                    DMReqsResponse = await DMClass.CheckDMReqAPI(Authorization);
                    
                    if (DMReqsResponse.Contains(@"""status"":""ok"""))
                    {
                        Logger.Info($"[{Program.GoodReqs}] Direct message requests checked at {DateTime.Now.ToString("HH:mm:ss")}");
                        GoodReqs++;
                        await DMClass.DMReqResponseProcess(DMReqsResponse);
                    }
                    else
                    {
                        Logger.Warn($"Direct message requests check failed at {DateTime.Now.ToString("HH:mm:ss")}");
                        BadReqs++;
                        int FAILdelayMs = int.TryParse(
                                                       Environment.GetEnvironmentVariable("FAIL_POLL_REQS_DELAY_MS"),
                                                       out var failValue)
                                                       ? failValue
                                                       : 180_000; // default 180 seconds

                        Thread.Sleep(FAILdelayMs);
                    }
                }
                catch { }
                int delayMs = int.TryParse(
                                          Environment.GetEnvironmentVariable("POLL_REQS_DELAY_MS"),
                                          out var value)
                                          ? value
                                          : 100_000; // default 100 seconds

                Thread.Sleep(delayMs);
            }
        }


        public static void Counter()
        {
            while (true)
            {
                try
                {
                    Console.Title = $"Good Requests: {GoodReqs}  || Bad Requests: {BadReqs} || Processed Msgs: {ProcessedMsgs}";
                    if (ProcessedMsgs % 100 == 0) // Every 100 processed messages
                    {
                        Store.DeleteUntilCutoff();
                    }
                }
                catch { }
                Thread.Sleep(500);
            }
        }

    }
}
