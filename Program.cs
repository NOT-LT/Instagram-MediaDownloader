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

namespace IGMediaDownloader
{

    internal class Program
    {
        public static RestClient IGRestClient = new RestClient("https://i.instagram.com/");
        public static RestClient FBRestClient = new RestClient("https://rupload.facebook.com/");
        public static string Username = "", Password = "",Authorization = "";
        public static int GoodReqs = 0, BadReqs = 0;
        public static List<string> Timestamps = new List<string>();

        static void Main(string[] args)
        {
            try
            {
                Timestamps = File.ReadAllLines("Timestamps.txt").ToList(); //Do not delete the Timestamps text file
            }
            catch (Exception)
            { Environment.Exit(0); }
          
            MainAsync().GetAwaiter().GetResult();
            Console.ReadLine();
        }

        public static async Task MainAsync()
        {
        Starting:
            Console.Clear();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("[+] Enter username: ");
            Username = Console.ReadLine();
            Console.Write("[+] Enter password: ");
            Password = Console.ReadLine();
            if (!await LoginClass.Login(Username, Password))
                goto Starting;

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
                        Thread.Sleep(1000000);
                    }

                }
                catch { }

                Thread.Sleep(30000);

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
                        Thread.Sleep(1000000);
                    }
                }
                catch { }
                Thread.Sleep(60000);
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
                Thread.Sleep(250);
            }
        }

    }
}
