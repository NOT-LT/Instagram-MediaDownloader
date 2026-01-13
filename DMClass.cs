using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using RestSharp;
using System.Security.Policy;
using System.Threading;
using System.CodeDom;
using System.Net;
using System.IO;

namespace IGMediaDownloader
{
    internal class DMClass
    {
        public static string MediaName = "", FBPhotoID = "", FBVidID = "", MediaUrl = "";
        

        public static async Task<string> CheckDMReqAPI(string SessionID)
        {
            var request = new RestRequest("https://i.instagram.com/api/v1/direct_v2/pending_inbox/", Method.Get);
            request.AddHeader("User-Agent", "Instagram 265.0.0.19.301 Android");
            request.AddHeader("X-Ig-App-Id", "567067343352427");
            request.AddHeader("X-Mid", "Y-TgjgABAAGyBUo6fl_dzKH6iPdK");
            request.AddHeader("Ig-U-Ds-User-Id", new Random().Next(999999999));
            RestResponse HttpResponse = await Program.IGRestClient.ExecuteAsync(request);
            var JSONResponse = HttpResponse.Content;
            return JSONResponse.ToString();

        }

        public static async Task<string> CheckDMAPI(string SessionID)
        {

            var request = new RestRequest("https://i.instagram.com/api/v1/direct_v2/inbox/?visual_message_return_type=unseen&thread_message_limit=10&persistentBadging=true&limit=20&is_prefetching=true&fetch_reason=null", Method.Get);
            request.AddHeader("User-Agent", "Instagram 265.0.0.19.301 Android");
            request.AddHeader("X-Ig-App-Id", "567067343352427");
            request.AddHeader("X-Mid", "Y-TgjgABAAGyBUo6fl_dzKH6iPdK");
            request.AddHeader("Ig-U-Ds-User-Id", new Random().Next(999999999));
            RestResponse HttpResponse = await Program.IGRestClient.ExecuteAsync(request);
            var JSONResponse = HttpResponse.Content;
            return JSONResponse.ToString();
        }

        public async static Task DMReqResponseProcess(string DMReqResponse)
        {

            var JsonObject = JObject.Parse(DMReqResponse);
            var inbox = JsonObject.SelectToken("inbox");

            foreach (var Thread in inbox?.SelectToken("threads") as JArray)
            {
                var Inviter = Thread.SelectToken("inviter");
                var UserID = Inviter.SelectToken("pk").ToString();
                string Username = Inviter.SelectToken("username").ToString();
                string ThreadID = Thread.SelectToken("thread_id").ToString();

                var AllItems = Thread.SelectToken("items") as JArray;

                foreach (var Item in AllItems)
                {
                    var ItemType = Item.SelectToken("item_type").ToString();
                    var TimeStamp = Item.SelectToken("timestamp").ToString();

                    if (ItemType == "text")
                    {
                        var Text = Item.SelectToken("text").ToString();
                        if (Text == "!activate")
                        {
                            Console.WriteLine($"[!] @{Username} requested an activation at {DateTime.Now.ToString("hh:mm:ss")}");
                            if (await Send_Text(UserID, Username, ThreadID, Text))
                                Console.WriteLine($"[!] @{Username} was accepted at {DateTime.Now.ToString("hh:mm:ss")}");
                            else
                                Console.WriteLine($"[!] @{Username} was ignored at {DateTime.Now.ToString("hh:mm:ss")}");
                        }
                    }



                }

            }
        }

        public async static Task DMResponseProcess(string DMResponse)
        {
            try
            {
                var JsonObject = JObject.Parse(DMResponse);
                var inbox = JsonObject.SelectToken("inbox");

                foreach (var Thread in inbox?.SelectToken("threads") as JArray)
                {
                    var Inviter = Thread.SelectToken("inviter");
                    var UserID = Inviter.SelectToken("pk").ToString();
                    string Username = Inviter.SelectToken("username").ToString();
                    string ThreadID = Thread.SelectToken("thread_id").ToString();

                    var AllItems = Thread.SelectToken("items") as JArray;

                    foreach (var Item in AllItems)
                    {
                        var ItemType = Item.SelectToken("item_type").ToString();
                        var TimeStamp = Item.SelectToken("timestamp").ToString();
                        if (!(Program.Timestamps.Contains(TimeStamp)))
                        {
                            await Task.Run(() => {
                                Program.Timestamps.Add(TimeStamp);
                                File.WriteAllLines("Timestamps.txt", Program.Timestamps);
                            });

                            var unixTimestamp = Convert.ToInt32((int)DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1)).TotalSeconds);

                            if ((Program.GoodReqs == 0 || Program.GoodReqs == 1) && (unixTimestamp/1000 > Convert.ToInt32(TimeStamp)))
                                return;

                            switch (ItemType)
                            {
                                //Item types are the type of the sent message, it can be one of those:
                                // 1. text
                                // 2. media_share
                                // 3. xma_media_share
                                // 4. clip

                                case "media_share":

                                    if (Item.ToString().Contains("carousel")) // For multi-photos post
                                    {
                                        Console.WriteLine($"[!] Multi-photos post by @{Username} is found at {DateTime.Now.ToString("hh:mm:ss")}");
                                        var MediaShareBlock = Item.SelectToken("media_share");
                                        var CarouselMediaBlock = MediaShareBlock.SelectToken("carousel_media") as JArray;

                                        foreach (var Media in CarouselMediaBlock)
                                        {
                                            var ImageVersion2 = Media.SelectToken("image_versions2");
                                            var Candidates = ImageVersion2.SelectToken("candidates") as JArray;
                                            MediaUrl = Candidates[0].SelectToken("url").ToString();
                                            MediaName = $"img{new Random().Next(99999999).ToString()}.png";

                                            if (await DownloadMedia(MediaUrl, MediaName))
                                                Console.WriteLine($"[!] Multi-photos post by @{Username} is Downloaded at {DateTime.Now.ToString("hh:mm:ss")}");
                                            else
                                                Console.WriteLine($"[!] Multi-photos post by @{Username} cannot be Downloaded at {DateTime.Now.ToString("hh:mm:ss")}");

                                            FBPhotoID = await SendItemClass.UploadImage(MediaName);

                                            if (await SendItemClass.SendImage(FBPhotoID, ThreadID))
                                                Console.WriteLine($"[!] Multi-photos post by @{Username} is sent at {DateTime.Now.ToString("hh:mm:ss")}");
                                            else
                                                Console.WriteLine($"[!] Multi-photos post by @{Username} cannot be sent at {DateTime.Now.ToString("hh:mm:ss")}");

                                            File.Delete(MediaName);
                                        }
                                    }

                                    else if (Item.ToString().Contains("video_versions"))
                                    {
                                        var VideoShareBlock = Item.SelectToken("media_share");
                                        var VideoBlock_1 = VideoShareBlock.SelectToken("video_versions") as JArray;
                                        MediaUrl = VideoBlock_1[0].SelectToken("url").ToString();
                                        MediaName = $"vid_{new Random().Next(99999999).ToString()}.mp4";

                                        if (await DownloadMedia(MediaUrl, MediaName))
                                            Console.WriteLine($"[!] Video by @{Username} is Downloaded at {DateTime.Now.ToString("hh:mm:ss")}");
                                        else
                                            Console.WriteLine($"[!] Video by @{Username} cannot be Downloaded at {DateTime.Now.ToString("hh:mm:ss")}");

                                        FBVidID = await SendItemClass.UploadVideo(MediaName);

                                        if (await SendItemClass.SendVideo(FBVidID, ThreadID))
                                            Console.WriteLine($"[!] Video by @{Username} is sent at {DateTime.Now.ToString("hh:mm:ss")}");
                                        else
                                            Console.WriteLine($"[!] Video by @{Username} cannot be sent at {DateTime.Now.ToString("hh:mm:ss")}");

                                        File.Delete(MediaName);
                                    }

                                    else
                                    {
                                        var MediaShareBlock = Item.SelectToken("media_share");
                                        var ImageVerionBlock = MediaShareBlock.SelectToken("image_versions2");
                                        var CandidatesBlock = ImageVerionBlock.SelectToken("candidates") as JArray;
                                        MediaUrl = CandidatesBlock[0].SelectToken("url").ToString();
                                        MediaName = $"img{new Random().Next(99999999).ToString()}.png";

                                        if (await DownloadMedia(MediaUrl, MediaName))
                                            Console.WriteLine($"[!] A normal post by @{Username} is Downloaded at {DateTime.Now.ToString("hh:mm:ss")}");
                                        else
                                            Console.WriteLine($"[!] A normal post by @{Username} cannot be Downloaded at {DateTime.Now.ToString("hh:mm:ss")}");

                                        FBPhotoID = await SendItemClass.UploadImage(MediaName);

                                        if (await SendItemClass.SendImage(FBPhotoID, ThreadID))
                                            Console.WriteLine($"[!] A normal post by @{Username} is sent at {DateTime.Now.ToString("hh:mm:ss")}");
                                        else
                                            Console.WriteLine($"[!] A normal post by @{Username} cannot be sent at {DateTime.Now.ToString("hh:mm:ss")}");

                                        File.Delete(MediaName);
                                    }
                                    break;

                                case "xma_media_share":
                                    var xmaMediaShareBlock = Item.SelectToken("xma_media_share") as JArray;
                                    MediaUrl = xmaMediaShareBlock[0].SelectToken("preview_url").ToString();
                                    MediaName = $"img{new Random().Next(99999999).ToString()}.png";

                                    if (await DownloadMedia(MediaUrl, MediaName))
                                        Console.WriteLine($"[!] A normal post by @{Username} is Downloaded at {DateTime.Now.ToString("hh:mm:ss")}");
                                    else
                                        Console.WriteLine($"[!] A normal post by @{Username} cannot be Downloaded at {DateTime.Now.ToString("hh:mm:ss")}");

                                    FBPhotoID = await SendItemClass.UploadImage(MediaName);

                                    if (await SendItemClass.SendImage(FBPhotoID, ThreadID))
                                        Console.WriteLine($"[!] A normal post by @{Username} is sent at {DateTime.Now.ToString("hh:mm:ss")}");
                                    else
                                        Console.WriteLine($"[!] A normal post by @{Username} cannot be sent at {DateTime.Now.ToString("hh:mm:ss")}");

                                    File.Delete(MediaName);
                                    break;

                                case "xma_story_share":
                                    var xmaStoryShareBlock = Item.SelectToken("xma_story_share") as JArray;
                                    MediaUrl = xmaStoryShareBlock[0].SelectToken("preview_url").ToString();
                                    MediaName = $"img{new Random().Next(99999999).ToString()}.png";

                                    if (await DownloadMedia(MediaUrl, MediaName))
                                        Console.WriteLine($"[!] A Story by @{Username} is Downloaded at {DateTime.Now.ToString("hh:mm:ss")}");
                                    else
                                        Console.WriteLine($"[!] A Story by @{Username} cannot be Downloaded at {DateTime.Now.ToString("hh:mm:ss")}");

                                    FBPhotoID = await SendItemClass.UploadImage(MediaName);

                                    if (await SendItemClass.SendImage(FBPhotoID, ThreadID))
                                        Console.WriteLine($"[!] A Story by @{Username} is sent at {DateTime.Now.ToString("hh:mm:ss")}");
                                    else
                                        Console.WriteLine($"[!] A Story by @{Username} cannot be sent at {DateTime.Now.ToString("hh:mm:ss")}");

                                    File.Delete(MediaName);
                                    break;

                                case "clip":
                                    var ClipBlock = Item.SelectToken("clip");
                                    var ClipBlock2 = ClipBlock.SelectToken("clip");
                                    var VideoBlock = ClipBlock2.SelectToken("video_versions") as JArray;
                                    MediaUrl = VideoBlock[0].SelectToken("url").ToString();
                                    MediaName = $"vid_{new Random().Next(99999999).ToString()}.mp4";

                                    if (await DownloadMedia(MediaUrl, MediaName))
                                        Console.WriteLine($"[!] Video by @{Username} is Downloaded at {DateTime.Now.ToString("hh:mm:ss")}");
                                    else
                                        Console.WriteLine($"[!] Video by @{Username} cannot be Downloaded at {DateTime.Now.ToString("hh:mm:ss")}");

                                    FBVidID = await SendItemClass.UploadVideo(MediaName);

                                    if (await SendItemClass.SendVideo(FBVidID, ThreadID))
                                        Console.WriteLine($"[!] Video by @{Username} is sent at {DateTime.Now.ToString("hh:mm:ss")}");
                                    else
                                        Console.WriteLine($"[!] Video by @{Username} cannot be sent at {DateTime.Now.ToString("hh:mm:ss")}");

                                    File.Delete(MediaName);
                                    break;

                            }
                        }

                    }

                }
            }
            catch { }

        }


        public async static Task<bool> DownloadMedia(string Url, string MediaName)
        {

            using (var WebClient = new WebClient())
            {
                try
                {
                    await WebClient.DownloadFileTaskAsync(Url, MediaName);
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }

        }


        public async static Task<bool> Send_Text(string UserID, string Username, string ThreadID, string ClientText)
        {
            var MyText = "";
            if (ClientText.ToLower() == "!activate")
            {
                MyText = $"Hi {Username}\nActivated Successfully!";
            }
            else
            {
                return false;
            }

            var ClientContext = Convert.ToString(new Random().Next(999999999));
            var Request = new RestRequest("https://i.instagram.com/api/v1/direct_v2/threads/broadcast/text/");
            Request.AddHeader("User-Agent", "Instagram 265.0.0.19.301 Android");
            Request.AddHeader("Content-Type", "application/x-www-form-urlencoded; charset=UTF-8");
            Request.AddStringBody($"recipient_users=[[{UserID}]]&mentioned_user_ids=[]&client_context={ClientContext}&_csrftoken=DlpXaOHu2hO61YBpZ4QxKxWYKXpk5BFN&text={MyText}&device_id=android_1c1487babcadb5fd&mutation_token={ClientContext}&offline_threading_id={ClientContext}", DataFormat.None);

            RestResponse HttpResponse = await Program.IGRestClient.ExecutePostAsync(Request);
            var JSONResponse = HttpResponse.Content;

            if (JSONResponse.Contains(@"""status"":""ok"""))
                return true;
            else
                return false;
        }






    }
}
