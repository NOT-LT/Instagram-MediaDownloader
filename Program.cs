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
        public static string Username = "", Password = "", Authorization = "";
        public static int GoodReqs = 0, BadReqs = 0, ProcessedMsgs = 0;
        public const string IgUserAgent = "Instagram 361.0.0.46.88 Android (31/12; 640dpi; 1644x3840; 674675155)";
        public const string IgAppId = "567067343352427";
        public static int PollMentionDelayMS = 65_000;
        public static bool EnableActivityFeed = false;
        public static int PollMsgsDelayMS = 30_000;



        // App startup timestamp in microseconds (Instagram format)
        public static long AppStartupTimestamp { get; private set; }

        private static int _lastCleanupCount = 0;
        private static DateTime _lastCleanupTime = DateTime.MinValue;

        static void Main(string[] args)
        {
            // Capture app startup timestamp immediately (in microseconds)
            AppStartupTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;

            try
            {
                DotNetEnv.Env.Load();
                var filePath = Environment.GetEnvironmentVariable("AUTH_STORE_PATH") ?? "Auth.txt";

                if (File.Exists(filePath))
                {
                    Program.Authorization = File.ReadAllText(filePath); // Read saved Authorization token
                }
                EnableActivityFeed = byte.TryParse(
                    Environment.GetEnvironmentVariable("ENABLE_ACTIVITY_FEED"),
                    out var v
                )
                && v == 1;

                PollMentionDelayMS = int.TryParse(
                   Environment.GetEnvironmentVariable("POLL_MENTIONS_DELAY_MS"),
                   out var v2
                )
               ? v2
               : 65_000; // Fallback to 5 minutes

                PollMsgsDelayMS= int.TryParse(
                            Environment.GetEnvironmentVariable("POLL_MSGS_DELAY_MS"),
                            out var value)
                            ? value
                            : 30_000; // Fallback to 30 seconds

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
            if (Program.Authorization.Length < 10 || await LoginClass.IsValidAuthToken(Program.Authorization) == false)
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


            var DMReqsThrd = new Thread(DMReqs) { Priority = ThreadPriority.AboveNormal, Name = "DM Requests Thread" };
            DMReqsThrd.Start();


            if (EnableActivityFeed)
            {
                var ActivityFeedThrd = new Thread(ActivityFeedLoop) { Priority = ThreadPriority.AboveNormal, Name = "Activity Feed Thread" };
                ActivityFeedThrd.Start();
            }


            var CounterThrd = new Thread(Counter) { Priority = ThreadPriority.Normal, Name = "Counter Thread" };
            CounterThrd.Start();

            // Main thread handles direct messages
            var DMResponse = "";
            while (true)
            {
                try
                {
                    DMResponse = await InstagramApiClient.GetDirectMessagesAsync();
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
                        Thread.Sleep(FAILdelayMs);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error in main DM loop: {ex.Message}");
                }


                Thread.Sleep(PollMsgsDelayMS);
            }
        }

        public static async void DMReqs()
        {
            while (true)
            {
                try
                {
                    var DMReqsResponse = await InstagramApiClient.GetPendingDMInboxAsync();

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
                catch (Exception ex)
                {
                    Logger.Error($"Error in DM Requests loop: {ex.Message}");
                }

                int delayMs = int.TryParse(
                                          Environment.GetEnvironmentVariable("POLL_REQS_DELAY_MS"),
                                          out var value)
                                          ? value
                                          : 100_000; // default 100 seconds

                Thread.Sleep(delayMs);
            }
        }

        public static async void ActivityFeedLoop()
        {
            // Small delay to let DM processing establish thread mappings first
            Thread.Sleep(5000);
            Logger.Info("Activity Feed monitor started");

            while (true)
            {
                try
                {
                    var activityFeedResponse = await InstagramApiClient.GetActivityFeedAsync();

                    if (activityFeedResponse.Contains(@"""status"":""ok"""))
                    {
                        Logger.Info($"[{Program.GoodReqs}] Activity Feed (mentions) checked at {DateTime.Now.ToString("HH:mm:ss")}");
                        GoodReqs++;
                        await DMClass.ActivityFeedProcess(activityFeedResponse);
                    }
                    else
                    {
                        Logger.Warn($"Activity Feed check failed at {DateTime.Now.ToString("HH:mm:ss")}");
                        BadReqs++;
                        int FAILdelayMs = int.TryParse(
                                                        Environment.GetEnvironmentVariable("FAIL_POLL_MENTIONS_DELAY_MS"),
                                                        out var failValue)
                                                        ? failValue
                                                        : 180_000; // default 180 seconds
                        Thread.Sleep(FAILdelayMs);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error in Activity Feed loop: {ex.Message}");
                }

                int delayMs = int.TryParse(
                                            Environment.GetEnvironmentVariable("POLL_MENTIONS_DELAY_MS"),
                                            out var value)
                                            ? value
                                            : 60_000; // default 60 seconds (mentions don't need to be as frequent)

                Thread.Sleep(delayMs);
            }
        }

        public static void Counter()
        {
            DateTime lastCleanup = DateTime.UtcNow;

            while (true)
            {
                try
                {
                    Console.Title =
                        $"Good Requests: {GoodReqs}  || Bad Requests: {BadReqs} || Processed Msgs: {ProcessedMsgs}";

                    if ((DateTime.UtcNow - lastCleanup).TotalHours >= 3)
                    {
                        lastCleanup = DateTime.UtcNow;

                        int deletedRows = Store.DeleteOlderThanOneHour();

                        if (deletedRows > 0)
                        {
                            Logger.Info($"Cleanup: Deleted {deletedRows} message(s) older than 1 hour");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error in Counter: {ex.Message}");
                }

                Thread.Sleep(500);
            }
        }


    }
}
