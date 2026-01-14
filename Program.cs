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

namespace IGMediaDownloaderV2
{

    internal class Program
    {
        internal static MessageStore Store = new MessageStore("processed_messages.db");
        public static RestClient IGRestClient = new RestClient("https://i.instagram.com/");
        public static RestClient FBRestClient = new RestClient("https://rupload.facebook.com/");
        public static string Username = "", Password = "", Authorization = "";
        public static int GoodReqs = 0, BadReqs = 0;
        public static List<string> Timestamps = new List<string>();

        static void Main(string[] args)
        {
            try
            {
                var filePath = Environment.GetEnvironmentVariable("AUTH_STORE_PATH") ?? "Auth.txt";
                if (File.Exists(filePath))
                {
                    Program.Authorization = File.ReadAllText(filePath); //Do not delete the Auth text file
                }
                //Timestamps = File.ReadAllLines("Timestamps.txt").ToList(); //Do not delete the Timestamps text file
            }
            catch (Exception)
            { Environment.Exit(0); }

            MainAsync().GetAwaiter().GetResult();
            Console.ReadLine();
        }

        public static async Task MainAsync()
        {
        Starting:
            //Console.Clear();
            Console.ForegroundColor = ConsoleColor.Green;
            if (Program.Authorization.Length < 10 || await LoginClass.IsValidAuthToken(Program.Authorization) == false)
            {
                //Console.Write("[+] Enter username: ");
                //Username = Console.ReadLine();
                //Console.Write("[+] Enter password: ");
                //Password = Console.ReadLine();
                Username = Environment.GetEnvironmentVariable("USERNAME") ?? "";
                Password = Environment.GetEnvironmentVariable("PASSWORD") ?? "";
                if (!await LoginClass.Login(Username, Password))
                    Environment.Exit(-1);

            }
            Console.WriteLine("[+] Logged In Successfully ! ");
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
                        Console.WriteLine($"[{Program.GoodReqs}] Direct messages was checked at {DateTime.Now.ToString("hh:mm:ss")}");
                        GoodReqs++;
                        await DMClass.DMResponseProcess(DMResponse);
                    }
                    else
                    {
                        Console.WriteLine($"[!] Direct messages cannot be checked at {DateTime.Now.ToString("hh:mm:ss")}");
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
                        Console.WriteLine($"[{Program.GoodReqs}] Direct messages reqs was checked at {DateTime.Now.ToString("hh:mm:ss")}");
                        GoodReqs++;
                        await DMClass.DMReqResponseProcess(DMReqsResponse);
                    }
                    else
                    {
                        Console.WriteLine($"[!] Direct messages reqs cannot be checked at {DateTime.Now.ToString("hh:mm:ss")}");
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
                    Console.Title = $"Good Requests: {GoodReqs}  || Bad Requests: {BadReqs}";

                }
                catch { }
                Thread.Sleep(500);
            }
        }

    }
}
