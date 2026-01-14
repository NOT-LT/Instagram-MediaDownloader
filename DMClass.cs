using Newtonsoft.Json.Linq;
using RestSharp;
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace IGMediaDownloaderV2
{
    internal static class Logger
    {
        private static readonly object _lock = new object();

        private enum Level { Debug, Info, Success, Warn, Error }

        public static void Debug(string message, int indent = 0) => Write(Level.Debug, message, indent);
        public static void Info(string message, int indent = 0) => Write(Level.Info, message, indent);
        public static void Success(string message, int indent = 0) => Write(Level.Success, message, indent);
        public static void Warn(string message, int indent = 0) => Write(Level.Warn, message, indent);
        public static void Error(string message, int indent = 0) => Write(Level.Error, message, indent);

        private static void Write(Level level, string message, int indent)
        {
            indent = Math.Max(0, indent);
            string pad = new string(' ', indent * 2);

            string ts = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
            string prefix = level.ToString().ToUpperInvariant();

            lock (_lock)
            {
                var old = Console.ForegroundColor;

                Console.ForegroundColor = level switch
                {
                    Level.Debug => ConsoleColor.DarkGray,
                    Level.Info => ConsoleColor.Cyan,
                    Level.Success => ConsoleColor.Green,
                    Level.Warn => ConsoleColor.Yellow,
                    Level.Error => ConsoleColor.Red,
                    _ => ConsoleColor.White
                };

                string line = $"[{ts}Z] [{prefix}] {pad}{message}";

                // ✅ Docker captures stdout/stderr automatically
                if (level == Level.Warn || level == Level.Error)
                    Console.Error.WriteLine(line);
                else
                    Console.Out.WriteLine(line);

                Console.ForegroundColor = old;
            }
        }
    }

    internal class DMClass
    {
        // Shared HttpClient is correct. Add a timeout so downloads don't hang forever.
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60)
        };

        // NOTE: these are shared static fields; if you process multiple threads concurrently,
        // this can overwrite values. It's fine if your code runs single-threaded per message.
        public static string MediaName = "", FBPhotoID = "", FBVidID = "", MediaUrl = "";

        private const string IgUserAgent = "Instagram 237.0.0.14.102 Android (31/12; 640dpi; 1644x3840; 373310563)";
        private const string IgAppId = "567067343352427";
        private const long AgeLimitSeconds = 3600; // 1 hour

        public static async Task<string> CheckDMReqAPI(string sessionId)
        {
            var request = new RestRequest("https://i.instagram.com/api/v1/direct_v2/pending_inbox/", Method.Get);
            request.AddHeader("User-Agent", IgUserAgent);
            request.AddHeader("X-Ig-App-Id", IgAppId);
            request.AddHeader("X-Mid", "Y-TgjgABAAGyBUo6fl_dzKH6iPdK");
            request.AddHeader("Ig-U-Ds-User-Id", Random.Shared.Next(999_999_999));

            RestResponse httpResponse = await Program.IGRestClient.ExecuteAsync(request);
            return httpResponse.Content ?? string.Empty;
        }

        public static async Task<string> CheckDMAPI(string sessionId)
        {
            var request = new RestRequest(
                "https://i.instagram.com/api/v1/direct_v2/inbox/?visual_message_return_type=unseen&thread_message_limit=10&persistentBadging=true&limit=20&fetch_reason=manual_refresh",
                Method.Get);

            request.AddHeader("User-Agent", IgUserAgent);
            request.AddHeader("X-Ig-App-Id", IgAppId);

            request.AddHeader("Ig-U-Ds-User-Id", "25025320");
            request.AddHeader("Ig-Intended-User-Id", "25025320");
            request.AddHeader("X-Bloks-Version-Id", "8dab28e76d3286a104a7f1c9e0c632386603a488cf584c9b49161c2f5182fe07");

            RestResponse httpResponse = await Program.IGRestClient.ExecuteAsync(request);
            return httpResponse.Content ?? string.Empty;
        }

        public static async Task DMReqResponseProcess(string dmReqResponse)
        {
            var jsonObject = JObject.Parse(dmReqResponse);
            var threads = jsonObject.SelectToken("inbox")?["threads"] as JArray;
            if (threads == null) return;

            foreach (var thread in threads)
            {
                var inviter = thread["inviter"];
                if (inviter == null) continue;

                var userId = inviter["pk"]?.ToString() ?? "";
                var username = inviter["username"]?.ToString() ?? "";
                var threadId = thread["thread_id"]?.ToString() ?? "";

                var items = thread["items"] as JArray;
                if (items == null) continue;

                foreach (var item in items)
                {
                    var itemType = item["item_type"]?.ToString() ?? "";
                    if (itemType != "text") continue;

                    var text = item["text"]?.ToString() ?? "";

                    Logger.Info($"Activation request from @{username}", indent: 0);
                    if (await Send_Text(userId, username, threadId, text))
                        Logger.Success($"Accepted @{username}", indent: 1);
                    else
                        Logger.Warn($"Ignored @{username}", indent: 1);
                }
            }
        }

        public static async Task DMResponseProcess(string dmResponse)
        {
            try
            {
                var jsonObject = JObject.Parse(dmResponse);
                var threads = jsonObject.SelectToken("inbox")?["threads"] as JArray;
                if (threads == null) return;

                foreach (var thread in threads)
                {
                    var inviter = thread["inviter"];
                    if (inviter == null) continue;

                    string username = inviter["username"]?.ToString() ?? "";
                    string threadId = thread["thread_id"]?.ToString() ?? "";

                    Logger.Info($"Processing thread {threadId} from @{username}");

                    var items = thread["items"] as JArray;
                    if (items == null || items.Count == 0)
                    {
                        Logger.Debug("No items", indent: 1);
                        continue;
                    }

                    // One DB read per thread
                    long cutoff = Program.Store.GetCutoff(threadId);

                    // One time per thread
                    long nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                    // Track how far down we processed this run (microseconds)
                    long oldestProcessedThisRun = long.MaxValue;

                    foreach (var item in items)
                    {
                        // Skip messages you sent
                        if (item["is_sent_by_viewer"]?.ToObject<bool>() == true)
                            continue;

                        long ts = item["timestamp"]?.ToObject<long>() ?? 0; // microseconds
                        if (ts == 0) continue;

                        // Hard stop by cutoff (microseconds)
                        if (ts <= cutoff)
                        {
                            Logger.Debug("Below cutoff → stop", indent: 1);
                            break;
                        }

                        string messageId = item["item_id"]?.ToString() ?? "";
                        if (string.IsNullOrWhiteSpace(messageId))
                            continue;

                        string itemType = item["item_type"]?.ToString() ?? "";

                        // Convert microseconds → seconds for age filtering
                        long tsSeconds = ts / 1_000_000;

                        Logger.Debug($"Item {messageId} type={itemType} ts={tsSeconds}s cutoff={cutoff}", indent: 1);

                        // If too old, stop (newest→oldest ordering assumption)
                        if (nowSeconds - tsSeconds > AgeLimitSeconds)
                        {
                            Logger.Warn($"Too old (> {AgeLimitSeconds}s) → stop", indent: 1);
                            cutoff = ts; // move cutoff so next run stops earlier
                            break;
                        }

                        // Only now do we hit DB for “already processed”
                        if (Program.Store.IsTerminalProcessed(messageId))
                        {
                            Logger.Debug("Already processed → stop", indent: 1);
                            cutoff = ts;
                            break;
                        }

                        // Mark seen/new
                        Program.Store.UpsertMessage(messageId, threadId, ts, MessageStatus.New);

                        try
                        {
                            switch (itemType)
                            {
                                case "media_share":
                                    Logger.Info($"media_share: {messageId}", indent: 1);
                                    await HandleMediaShare(item, username, threadId);
                                    Program.Store.UpsertMessage(messageId, threadId, ts, MessageStatus.Processed);
                                    Logger.Success($"Processed: {messageId}", indent: 2);
                                    break;

                                case "xma_media_share":
                                    Logger.Info($"xma_media_share: {messageId}", indent: 1);
                                    await HandleXmaMediaShare(item, username, threadId);
                                    Program.Store.UpsertMessage(messageId, threadId, ts, MessageStatus.Processed);
                                    Logger.Success($"Processed: {messageId}", indent: 2);
                                    break;

                                case "xma_story_share":
                                    Logger.Info($"xma_story_share: {messageId}", indent: 1);
                                    await HandleXmaStoryShare(item, username, threadId);
                                    Program.Store.UpsertMessage(messageId, threadId, ts, MessageStatus.Processed);
                                    Logger.Success($"Processed: {messageId}", indent: 2);
                                    break;

                                case "clip":
                                    Logger.Info($"clip: {messageId}", indent: 1);
                                    await HandleClip(item, username, threadId);
                                    Program.Store.UpsertMessage(messageId, threadId, ts, MessageStatus.Processed);
                                    Logger.Success($"Processed: {messageId}", indent: 2);
                                    break;

                                case "story_share":
                                    Logger.Info($"story_share: {messageId}", indent: 1);
                                    await HandleStoryShare(item, username, threadId);
                                    Program.Store.UpsertMessage(messageId, threadId, ts, MessageStatus.Processed);
                                    Logger.Success($"Processed: {messageId}", indent: 2);
                                    break;

                                case "placeholder":
                                    var placeholderMsg = item["placeholder"]?["message"]?.ToString() ?? "Unavailable";
                                    Logger.Warn($"placeholder: {placeholderMsg}", indent: 1);
                                    Program.Store.UpsertMessage(messageId, threadId, ts, MessageStatus.Skipped);
                                    break;

                                default:
                                    Logger.Warn($"Unhandled type: {itemType}", indent: 1);
                                    Program.Store.UpsertMessage(messageId, threadId, ts, MessageStatus.Skipped);
                                    break;
                            }

                            if (ts < oldestProcessedThisRun)
                                oldestProcessedThisRun = ts;
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"Processing failed: {ex.Message}", indent: 1);
                            Program.Store.UpsertMessage(messageId, threadId, ts, MessageStatus.Failed);
                        }
                    }

                    // Set cutoff ONCE per thread
                    long finalCutoff = cutoff;
                    if (oldestProcessedThisRun != long.MaxValue)
                        finalCutoff = Math.Min(finalCutoff == 0 ? long.MaxValue : finalCutoff, oldestProcessedThisRun);

                    if (finalCutoff != 0)
                    {
                        Program.Store.SetCutoff(threadId, finalCutoff);
                        Logger.Debug($"Cutoff updated to {finalCutoff}", indent: 1);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"DMResponseProcess error: {ex.Message}");
            }
        }

        private static async Task HandleMediaShare(JToken item, string username, string threadId)
        {
            if (!item.ToString().Contains("direct_media_share"))
            {
                Logger.Warn("media_share without direct_media_share payload", indent: 2);
                return;
            }

            int mediaType = item.SelectToken("direct_media_share.media.media_type")?.ToObject<int>() ?? -1;

            if (mediaType == 1)
            {
                MediaName = $"img_{Random.Shared.Next(99999999)}.png";
                MediaUrl = item.SelectToken("direct_media_share.media.image_versions2.candidates[0].url")?.ToString() ?? "";

                if (await DownloadMedia(MediaUrl, MediaName))
                    Logger.Success($"Downloaded photo for @{username}", indent: 2);
                else
                    Logger.Warn($"Failed to download photo for @{username}", indent: 2);

                FBPhotoID = await SendItemClass.UploadImage(MediaName);

                if (await SendItemClass.SendImage(FBPhotoID, threadId))
                    Logger.Success($"Sent photo to @{username}", indent: 2);
                else
                    Logger.Warn($"Failed to send photo to @{username}", indent: 2);

                SafeDelete(MediaName);
            }
            else if (mediaType == 2)
            {
                MediaName = $"vid_{Random.Shared.Next(99999999)}.mp4";
                MediaUrl = item.SelectToken("direct_media_share.media.video_versions[0].url")?.ToString() ?? "";

                if (await DownloadMedia(MediaUrl, MediaName))
                    Logger.Success($"Downloaded video for @{username}", indent: 2);
                else
                    Logger.Warn($"Failed to download video for @{username}", indent: 2);

                FBVidID = await SendItemClass.UploadVideo(MediaName);

                if (await SendItemClass.SendVideo(FBVidID, threadId))
                    Logger.Success($"Sent video to @{username}", indent: 2);
                else
                    Logger.Warn($"Failed to send video to @{username}", indent: 2);

                SafeDelete(MediaName);
            }
            else if (mediaType == 8) // carousel
            {
                var allPosts = item.SelectToken("direct_media_share.media.carousel_media") as JArray;
                if (allPosts is null)
                {
                    Logger.Warn("carousel_media missing", indent: 2);
                    return;
                }

                foreach (var post in allPosts)
                {
                    int postType = post.SelectToken("media_type")?.ToObject<int>() ?? -1;

                    if (postType == 1)
                    {
                        MediaUrl = post.SelectToken("image_versions2.candidates[0].url")?.ToString() ?? "";
                        MediaName = $"img_{Random.Shared.Next(99999999)}.png";

                        if (await DownloadMedia(MediaUrl, MediaName))
                            Logger.Success($"Downloaded carousel image for @{username}", indent: 2);
                        else
                            Logger.Warn($"Failed carousel image download for @{username}", indent: 2);

                        FBPhotoID = await SendItemClass.UploadImage(MediaName);

                        if (await SendItemClass.SendImage(FBPhotoID, threadId))
                            Logger.Success($"Sent carousel image to @{username}", indent: 2);
                        else
                            Logger.Warn($"Failed to send carousel image to @{username}", indent: 2);

                        SafeDelete(MediaName);
                    }
                    else if (postType == 2)
                    {
                        MediaUrl = post.SelectToken("video_versions[0].url")?.ToString() ?? "";
                        MediaName = $"vid_{Random.Shared.Next(99999999)}.mp4";

                        if (await DownloadMedia(MediaUrl, MediaName))
                            Logger.Success($"Downloaded carousel video for @{username}", indent: 2);
                        else
                            Logger.Warn($"Failed carousel video download for @{username}", indent: 2);

                        // NOTE: you had UploadImage here before; that was likely a bug.
                        FBVidID = await SendItemClass.UploadVideo(MediaName);

                        if (await SendItemClass.SendVideo(FBVidID, threadId))
                            Logger.Success($"Sent carousel video to @{username}", indent: 2);
                        else
                            Logger.Warn($"Failed to send carousel video to @{username}", indent: 2);

                        SafeDelete(MediaName);
                    }
                    else
                    {
                        Logger.Warn($"Unknown carousel post type: {postType}", indent: 2);
                    }
                }
            }
            else
            {
                Logger.Warn($"Unknown media_type: {mediaType}", indent: 2);
            }
        }

        private static async Task HandleStoryShare(JToken item, string username, string threadId)
        {
            int mediaType = item.SelectToken("story_share.media.media_type")?.ToObject<int>() ?? -1;

            if (mediaType == 1)
            {
                MediaName = $"img_{Random.Shared.Next(99999999)}.png";
                MediaUrl = item.SelectToken("story_share.media.image_versions2.candidates[0].url")?.ToString() ?? "";

                if (await DownloadMedia(MediaUrl, MediaName))
                    Logger.Success($"Downloaded story photo for @{username}", indent: 2);
                else
                    Logger.Warn($"Failed to download story photo for @{username}", indent: 2);

                FBPhotoID = await SendItemClass.UploadImage(MediaName);

                if (await SendItemClass.SendImage(FBPhotoID, threadId))
                    Logger.Success($"Sent story photo to @{username}", indent: 2);
                else
                    Logger.Warn($"Failed to send story photo to @{username}", indent: 2);

                SafeDelete(MediaName);
            }
            else if (mediaType == 2)
            {
                MediaName = $"vid_{Random.Shared.Next(99999999)}.mp4";
                MediaUrl = item.SelectToken("story_share.media.video_versions[0].url")?.ToString() ?? "";

                if (await DownloadMedia(MediaUrl, MediaName))
                    Logger.Success($"Downloaded story video for @{username}", indent: 2);
                else
                    Logger.Warn($"Failed to download story video for @{username}", indent: 2);

                FBVidID = await SendItemClass.UploadVideo(MediaName);

                // NOTE: you previously called SendImage here; that was likely a bug.
                if (await SendItemClass.SendVideo(FBVidID, threadId))
                    Logger.Success($"Sent story video to @{username}", indent: 2);
                else
                    Logger.Warn($"Failed to send story video to @{username}", indent: 2);

                SafeDelete(MediaName);
            }
            else
            {
                Logger.Warn($"Unknown story media_type: {mediaType}", indent: 2);
            }
        }

        private static async Task HandleXmaMediaShare(JToken item, string username, string threadId)
        {
            var xma = item["xma_media_share"] as JArray;
            if (xma == null || xma.Count == 0) return;

            MediaUrl = xma[0]?["preview_url"]?.ToString() ?? "";
            MediaName = $"img_{Random.Shared.Next(99999999)}.png";

            if (await DownloadMedia(MediaUrl, MediaName))
                Logger.Success($"Downloaded xma media for @{username}", indent: 2);
            else
                Logger.Warn($"Failed xma media download for @{username}", indent: 2);

            FBPhotoID = await SendItemClass.UploadImage(MediaName);

            if (await SendItemClass.SendImage(FBPhotoID, threadId))
                Logger.Success($"Sent xma media to @{username}", indent: 2);
            else
                Logger.Warn($"Failed to send xma media to @{username}", indent: 2);

            SafeDelete(MediaName);
        }

        private static async Task HandleXmaStoryShare(JToken item, string username, string threadId)
        {
            var xma = item["xma_story_share"] as JArray;
            if (xma == null || xma.Count == 0) return;

            MediaUrl = xma[0]?["preview_url"]?.ToString() ?? "";
            MediaName = $"img_{Random.Shared.Next(99999999)}.png";

            if (await DownloadMedia(MediaUrl, MediaName))
                Logger.Success($"Downloaded xma story for @{username}", indent: 2);
            else
                Logger.Warn($"Failed xma story download for @{username}", indent: 2);

            FBPhotoID = await SendItemClass.UploadImage(MediaName);

            if (await SendItemClass.SendImage(FBPhotoID, threadId))
                Logger.Success($"Sent xma story to @{username}", indent: 2);
            else
                Logger.Warn($"Failed to send xma story to @{username}", indent: 2);

            SafeDelete(MediaName);
        }

        private static async Task HandleClip(JToken item, string username, string threadId)
        {
            var vids = item["clip"]?["clip"]?["video_versions"] as JArray;
            if (vids == null || vids.Count == 0) return;

            MediaUrl = vids[0]?["url"]?.ToString() ?? "";
            MediaName = $"vid_{Random.Shared.Next(99999999)}.mp4";

            if (await DownloadMedia(MediaUrl, MediaName))
                Logger.Success($"Downloaded clip for @{username}", indent: 2);
            else
                Logger.Warn($"Failed to download clip for @{username}", indent: 2);

            FBVidID = await SendItemClass.UploadVideo(MediaName);

            if (await SendItemClass.SendVideo(FBVidID, threadId))
                Logger.Success($"Sent clip to @{username}", indent: 2);
            else
                Logger.Warn($"Failed to send clip to @{username}", indent: 2);

            SafeDelete(MediaName);
        }

        public static async Task<bool> DownloadMedia(string url, string mediaName)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                Logger.Warn("Download URL is empty", indent: 2);
                return false;
            }

            try
            {
                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                await using var input = await response.Content.ReadAsStreamAsync();
                await using var output = System.IO.File.Create(mediaName);

                await input.CopyToAsync(output);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Download failed: {ex.Message}", indent: 2);
                return false;
            }
        }

        public static async Task<bool> Send_Text(string userId, string username, string threadId, string clientText)
        {
            string myText = $"Hi {username}! 👋\n\n" +
                            "I can help you save Instagram content 📥\n" +
                            "Just send me a post, reel, or story, and I’ll re-send it back to you so you can save it easily ✨\n\n" +
                            "Developed by Taha Aljamri (@zdlk) 🤖";

            var clientContext = Convert.ToString(Random.Shared.Next(999999999));
            var request = new RestRequest("https://i.instagram.com/api/v1/direct_v2/threads/broadcast/text/");
            request.AddHeader("User-Agent", IgUserAgent);
            request.AddHeader("Content-Type", "application/x-www-form-urlencoded; charset=UTF-8");

            request.AddStringBody(
                $"recipient_users=[[{userId}]]&mentioned_user_ids=[]&client_context={clientContext}&_csrftoken=DlpXaOHu2hO61YBpZ4QxKxWYKXpk5BFN&text={myText}&device_id=android_1c1487babcadb5fd&mutation_token={clientContext}&offline_threading_id={clientContext}",
                DataFormat.None);

            RestResponse httpResponse = await Program.IGRestClient.ExecutePostAsync(request);
            var json = httpResponse.Content ?? "";

            return json.Contains(@"""status"":""ok""");
        }

        private static void SafeDelete(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
                    System.IO.File.Delete(path);
            }
            catch
            {
                // ignore
            }
        }
    }
}
