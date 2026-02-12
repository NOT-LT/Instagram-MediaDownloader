using Newtonsoft.Json.Linq;
using RestSharp;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.AccessControl;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Runtime.CompilerServices;
using Sprache;

namespace IGMediaDownloaderV2
{

    internal class DMClass
    {
        // Concurrency controls
        private static readonly SemaphoreSlim _threadSemaphore = new SemaphoreSlim(
            int.TryParse(Environment.GetEnvironmentVariable("MAX_CONCURRENT_THREADS"), out var maxThreads)
                ? maxThreads : 5
        );

        private static readonly SemaphoreSlim _messageSemaphore = new SemaphoreSlim(
            int.TryParse(Environment.GetEnvironmentVariable("MAX_CONCURRENT_MESSAGES"), out var maxMsgs)
                ? maxMsgs : 10
        );

        private static readonly SemaphoreSlim _downloadSemaphore = new SemaphoreSlim(
            int.TryParse(Environment.GetEnvironmentVariable("MAX_CONCURRENT_DOWNLOADS"), out var maxDownloads)
                ? maxDownloads : 20
        );

        private const long AgeLimitSeconds = 3600; // 1 hour

        // Context class to avoid shared static state
        private class MediaContext
        {
            public string MediaName { get; set; } = "";
            public string FBPhotoID { get; set; } = "";
            public string FBVidID { get; set; } = "";
            public string MediaUrl { get; set; } = "";
        }

        public static async Task<string> CheckDMReqAPI(string sessionId)
        {
            var request = new RestRequest("https://i.instagram.com/api/v1/direct_v2/pending_inbox/", Method.Get);
            request.AddHeader("User-Agent", Program.IgUserAgent);
            request.AddHeader("X-Ig-App-Id", Program.IgAppId);
            request.AddHeader("X-Mid", "Y-TgjgABAAGyBUo6fl_dzKH6iPdK");
            request.AddHeader("Ig-U-Ds-User-Id", Random.Shared.Next(999_999_999));

            RestResponse httpResponse = await Program.IGRestClient.ExecuteAsync(request);
            return httpResponse.Content ?? string.Empty;
        }

        public static async Task<string> CheckActivityFeedAPI() // For mentions
        {
            var request = new RestRequest("/api/v1/news/inbox/?could_truncate_feed=true&should_skip_su=true&mark_as_seen=false&timezone_offset=28800&timezone_name=Asia%2FShanghai", Method.Get);
            request.AddHeader("User-Agent", Program.IgUserAgent);
            request.AddHeader("X-Ig-App-Id", Program.IgAppId);
            request.AddHeader("X-Mid", "aYuA4gABAAFoIhgQirMYy3a98zul");
            request.AddHeader("Ig-U-Ds-User-Id", Random.Shared.Next(999_999_999));
            RestResponse httpResponse = await Program.IGRestClient.ExecuteAsync(request);
            return httpResponse.Content ?? string.Empty;
        }

        public static async Task<string> ActivityFeedProcess(string JSONResponse)
        {
            if (string.IsNullOrEmpty(JSONResponse)) return "No response from API";

            var jsonObject = JObject.Parse(JSONResponse);
            long xMinutesAgo = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (Program.PollMentionDelayMS / 1000);

            var allStories = new List<JToken>();
            if (jsonObject["new_stories"] != null) allStories.AddRange(jsonObject["new_stories"]);
            if (jsonObject["old_stories"] != null) allStories.AddRange(jsonObject["old_stories"]);

            foreach (var story in allStories)
            {
                string notifName = story["notif_name"]?.ToString();
                var args = story["args"];

                if (notifName == "mentioned_comment" && args != null)
                {
                    double rawTs = args["timestamp"]?.Value<double>() ?? 0;
                    long storyTimestamp = (long)rawTs;

                    // 1. Time Cutoff Check
                    if (storyTimestamp < xMinutesAgo) break;

                    string username = args["profile_name"]?.ToString() ?? "unknown";
                    string userId = args["profile_id"]?.ToString() ?? "";
                    string mediaId = args["media"]?[0]?["id"]?.ToString() ?? "";

                    // Use a unique ID for this notification to prevent double-processing
                    // 'M_' prefix helps distinguish it from DM item IDs
                    string mentionNotificationId = $"M_{mediaId}_{storyTimestamp}";

                    // 2. Duplicate Check
                    if (Program.Store.Exists(mentionNotificationId)) continue;

                    Logger.Info($"[FRESH MENTION] {storyTimestamp}, Media ID: {mediaId}");

                    // 3. Thread ID Resolution
                    // Note: If this is the first time they tag you, threadId might be empty
                    var threadId = Program.Store.GetReceiverId(userId) ?? "";
                    Logger.Info($"ThreadId: " + threadId);

                    if (string.IsNullOrEmpty(threadId))
                    {
                        Logger.Warn($"No thread found for {username}. Ensure DM runs first");
                        continue;
                    }
                    Logger.Info($"[FRESH MENTION] HERE");

                    int media_type = await DownloadClass.Get_media_type_by_id(mediaId);
                    var ctx = new MediaContext();
                    Logger.Info($"[FRESH MENTION] {media_type} | {media_type.GetType()}");

                    if (media_type == 1) // PHOTO
                    {
                        ctx.MediaUrl = await DownloadClass.Get_xma_image_download_link_by_id(mediaId);
                        if (string.IsNullOrWhiteSpace(ctx.MediaUrl)) continue;

                        ctx.MediaName = $"img_{Guid.NewGuid():N}.png";

                        await _downloadSemaphore.WaitAsync();
                        try
                        {
                            if (await DownloadClass.DownloadMedia(ctx.MediaUrl, ctx.MediaName))
                            {
                                ctx.FBPhotoID = await SendItemClass.UploadImage(ctx.MediaName);
                                await SendItemClass.SendImage(ctx.FBPhotoID, threadId);
                                // Mark as processed only after successful send
                                Program.Store.UpsertMessage(mentionNotificationId, threadId, storyTimestamp, MessageStatus.Processed);
                                Logger.Success($"Sent photo to @{username}");
                            }
                        }
                        finally
                        {
                            _downloadSemaphore.Release();
                            SafeDelete(ctx.MediaName);
                        }
                    }
                    else if (media_type == 2) // VIDEO
                    {
                        Logger.Info($"[VIDEO]");
                        ctx.MediaUrl = await DownloadClass.Get_xma_video_download_link_by_id(mediaId);
                        if (string.IsNullOrWhiteSpace(ctx.MediaUrl)) continue;

                        ctx.MediaName = $"vid_{Guid.NewGuid():N}.mp4";

                        await _downloadSemaphore.WaitAsync();
                        try
                        {
                            if (await DownloadClass.DownloadMedia(ctx.MediaUrl, ctx.MediaName))
                            {
                                ctx.FBVidID = await SendItemClass.UploadVideo(ctx.MediaName);
                                await SendItemClass.SendVideo(ctx.FBVidID, threadId);
                                Program.Store.UpsertMessage(mentionNotificationId, threadId, storyTimestamp, MessageStatus.Processed);
                                Logger.Success($"Sent video to @{username}");
                            }
                        }
                        finally
                        {
                            _downloadSemaphore.Release();
                            SafeDelete(ctx.MediaName);
                        }
                    }
                }
            }
            return "";
        }

        public static async Task<string> CheckDMAPI(string sessionId)
        {
            var request = new RestRequest(
                "https://i.instagram.com/api/v1/direct_v2/inbox/?visual_message_return_type=unseen&thread_message_limit=10&persistentBadging=true&limit=20&fetch_reason=manual_refresh",
                Method.Get);

            request.AddHeader("User-Agent", Program.IgUserAgent);
            request.AddHeader("X-Ig-App-Id", Program.IgAppId);

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

            // Process activation requests in parallel
            var tasks = new List<Task>();
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

                    // Fire and forget pattern for activation requests
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            Logger.Info($"Activation request from @{username}", indent: 0);
                            var responseMsg = Environment.GetEnvironmentVariable("NEW_USER_WELCOME_MSG");
                            if (responseMsg == null || String.IsNullOrEmpty(responseMsg))
                            {
                                responseMsg = $"Hi {username}!\nSend me an Instagram post, reel, or story and I'll send it back so you can save it\nYou can also tag me in a post's comments and I'll DM it to you automatically\n- Developed by Taha Aljamri (@zdlk)";
                            }

                            if (await SendItemClass.SendText(userId, username, threadId, responseMsg))
                                Logger.Success($"Accepted @{username}", indent: 1);
                            else
                                Logger.Warn($"Ignored @{username}", indent: 1);
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"Activation failed for @{username}: {ex.Message}", indent: 1);
                        }
                    }));
                }
            }

            // Wait for all activation requests to complete (with timeout)
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        public static async Task DMResponseProcess(string dmResponse)
        {
            try
            {
                var jsonObject = JObject.Parse(dmResponse);
                var threads = jsonObject.SelectToken("inbox")?["threads"] as JArray;
                if (threads == null) return;

                // 1. Collect mappings first (Synchronously - very fast)
                var threadsToRegister = new List<(string, string)>();
                foreach (var t in threads)
                {
                    var threadId = t["thread_id"]?.ToString();
                    // Note: Instagram usually uses 'pk' or 'id' for the user ID
                    var userId = t["inviter"]?["pk"]?.ToString() ?? t["inviter"]?["id"]?.ToString();

                    if (!string.IsNullOrEmpty(threadId) && !string.IsNullOrEmpty(userId))
                    {
                        threadsToRegister.Add((threadId, userId));
                    }
                }

                // 2. Register them in the DB immediately (uses your fast HashSet cache)
                // This ensures GetThreadId(userId) will work for the ActivityFeed right away.
                Program.Store.EnsureThreadsExist(threadsToRegister);

                // 3. Now process the messages in the threads
                var threadTasks = threads
                    .Where(t => t["inviter"] != null)
                    .Select(async thread =>
                    {
                        await _threadSemaphore.WaitAsync();
                        try
                        {
                            await ProcessThread(thread);
                        }
                        finally
                        {
                            _threadSemaphore.Release();
                        }
                    });

                await Task.WhenAll(threadTasks);
            }
            catch (Exception ex)
            {
                Logger.Error($"DMResponseProcess error: {ex.Message}");
            }
        }

        private static async Task ProcessThread(JToken thread)
        {
            try
            {
                var inviter = thread["inviter"];
                string username = inviter?["username"]?.ToString() ?? "";
                string threadId = thread["thread_id"]?.ToString() ?? "";

                Logger.Info($"Processing thread {threadId} from @{username}");

                var items = thread["items"] as JArray;
                if (items == null || items.Count == 0)
                {
                    Logger.Debug("No items", indent: 1);
                    return;
                }

                // One DB read per thread
                long cutoff = Program.Store.GetCutoff(threadId);

                // Use app startup timestamp as minimum cutoff
                long effectiveCutoff = Math.Max(cutoff, Program.AppStartupTimestamp);

                long nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                long oldestProcessedThisRun = long.MaxValue;

                // Process messages in parallel with controlled concurrency
                var messageTasks = new List<Task>();

                foreach (var item in items)
                {
                    // Skip messages you sent
                    if (item["is_sent_by_viewer"]?.ToObject<bool>() == true)
                        continue;

                    long ts = item["timestamp"]?.ToObject<long>() ?? 0;
                    if (ts == 0) continue;

                    // Check against effective cutoff (includes app startup timestamp)
                    if (ts <= effectiveCutoff)
                    {
                        Logger.Debug($"Below cutoff -> stop (ts={ts}, cutoff={effectiveCutoff}, appStart={Program.AppStartupTimestamp})", indent: 1);
                        break;
                    }

                    string messageId = item["item_id"]?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(messageId))
                        continue;

                    string itemType = item["item_type"]?.ToString() ?? "";
                    long tsSeconds = ts / 1_000_000;

                    Logger.Debug($"Item {messageId} type={itemType} ts={tsSeconds}s cutoff={effectiveCutoff}", indent: 1);

                    if (nowSeconds - tsSeconds > AgeLimitSeconds)
                    {
                        Logger.Warn($"Too old (> {AgeLimitSeconds}s) -> stop", indent: 1);
                        cutoff = ts;
                        break;
                    }

                    if (Program.Store.IsTerminalProcessed(messageId))
                    {
                        Logger.Debug("Already processed -> stop", indent: 1);
                        cutoff = ts;
                        break;
                    }

                    // Mark as new
                    Program.Store.UpsertMessage(messageId, threadId, ts, MessageStatus.New);
                    Interlocked.Increment(ref Program.ProcessedMsgs);

                    // Process message in parallel
                    var messageTask = ProcessMessage(item, username, threadId, messageId, ts, itemType);
                    messageTasks.Add(messageTask);

                    // Update oldest timestamp
                    if (ts < oldestProcessedThisRun)
                        oldestProcessedThisRun = ts;
                }

                // Wait for all messages in this thread to complete
                await Task.WhenAll(messageTasks);

                // Set cutoff ONCE per thread
                long finalCutoff = cutoff;
                if (oldestProcessedThisRun != long.MaxValue)
                    finalCutoff = Math.Min(finalCutoff == 0 ? long.MaxValue : finalCutoff, oldestProcessedThisRun);

                // Ensure cutoff is never less than app startup timestamp
                finalCutoff = Math.Max(finalCutoff, Program.AppStartupTimestamp);

                if (finalCutoff != 0)
                {
                    Program.Store.SetCutoff(threadId, finalCutoff);
                    Logger.Debug($"Cutoff updated to {finalCutoff} (appStart={Program.AppStartupTimestamp})", indent: 1);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"ProcessThread error: {ex.Message}");
            }
        }

        private static async Task ProcessMessage(JToken item, string username, string threadId, string messageId, long ts, string itemType)
        {
            await _messageSemaphore.WaitAsync();
            try
            {
                switch (itemType)
                {
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

                    case "xma_clip":
                        Logger.Info($"xma_clip: {messageId}", indent: 1);
                        await HandleXmaClip(item, username, threadId);
                        Program.Store.UpsertMessage(messageId, threadId, ts, MessageStatus.Processed);
                        Logger.Success($"Processed: {messageId}", indent: 2);
                        break;

                    case "generic_xma":
                        Logger.Info($"generic_xma: {messageId}", indent: 1);
                        await HandleGenericXma(item, username, threadId);
                        Program.Store.UpsertMessage(messageId, threadId, ts, MessageStatus.Processed);
                        Logger.Success($"Processed: {messageId}", indent: 2);
                        break;

                    case "media_share":
                        Logger.Info($"media_share: {messageId}", indent: 1);
                        await HandleMediaShare(item, username, threadId);
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
            }
            catch (Exception ex)
            {
                Logger.Error($"Processing failed: {ex.Message}", indent: 1);
                Program.Store.UpsertMessage(messageId, threadId, ts, MessageStatus.Failed);
            }
            finally
            {
                _messageSemaphore.Release();
            }
        }

        private static async Task HandleMediaShare(JToken item, string username, string threadId)
        {
            var ctx = new MediaContext();

            if (!item.ToString().Contains("direct_media_share"))
            {
                Logger.Warn("media_share without direct_media_share payload", indent: 2);
                return;
            }

            int mediaType = item.SelectToken("direct_media_share.media.media_type")?.ToObject<int>() ?? -1;

            if (mediaType == 1)
            {
                ctx.MediaName = $"img_{Guid.NewGuid():N}.png";
                ctx.MediaUrl = item.SelectToken("direct_media_share.media.image_versions2.candidates[0].url")?.ToString() ?? "";

                await _downloadSemaphore.WaitAsync();
                try
                {
                    if (await DownloadClass.DownloadMedia(ctx.MediaUrl, ctx.MediaName))
                        Logger.Success($"Downloaded photo for @{username}", indent: 2);
                    else
                        Logger.Warn($"Failed to download photo for @{username}", indent: 2);
                }
                finally
                {
                    _downloadSemaphore.Release();
                }

                ctx.FBPhotoID = await SendItemClass.UploadImage(ctx.MediaName);

                if (await SendItemClass.SendImage(ctx.FBPhotoID, threadId))
                    Logger.Success($"Sent photo to @{username}", indent: 2);
                else
                    Logger.Warn($"Failed to send photo to @{username}", indent: 2);

                SafeDelete(ctx.MediaName);
            }
            else if (mediaType == 2)
            {
                ctx.MediaName = $"vid_{Guid.NewGuid():N}.mp4";
                ctx.MediaUrl = item.SelectToken("direct_media_share.media.video_versions[0].url")?.ToString() ?? "";

                await _downloadSemaphore.WaitAsync();
                try
                {
                    if (await DownloadClass.DownloadMedia(ctx.MediaUrl, ctx.MediaName))
                        Logger.Success($"Downloaded video for @{username}", indent: 2);
                    else
                        Logger.Warn($"Failed to download video for @{username}", indent: 2);
                }
                finally
                {
                    _downloadSemaphore.Release();
                }

                ctx.FBVidID = await SendItemClass.UploadVideo(ctx.MediaName);

                if (await SendItemClass.SendVideo(ctx.FBVidID, threadId))
                    Logger.Success($"Sent video to @{username}", indent: 2);
                else
                    Logger.Warn($"Failed to send video to @{username}", indent: 2);

                SafeDelete(ctx.MediaName);
            }
            else if (mediaType == 8) // carousel
            {
                var allPosts = item.SelectToken("direct_media_share.media.carousel_media") as JArray;
                if (allPosts is null)
                {
                    Logger.Warn("carousel_media missing", indent: 2);
                    return;
                }

                // Process carousel items in parallel
                var carouselTasks = allPosts.Select(async post =>
                {
                    var itemCtx = new MediaContext();
                    int postType = post.SelectToken("media_type")?.ToObject<int>() ?? -1;

                    if (postType == 1)
                    {
                        itemCtx.MediaUrl = post.SelectToken("image_versions2.candidates[0].url")?.ToString() ?? "";
                        itemCtx.MediaName = $"img_{Guid.NewGuid():N}.png";

                        await _downloadSemaphore.WaitAsync();
                        try
                        {
                            if (await DownloadClass.DownloadMedia(itemCtx.MediaUrl, itemCtx.MediaName))
                                Logger.Success($"Downloaded carousel image for @{username}", indent: 2);
                            else
                                Logger.Warn($"Failed carousel image download for @{username}", indent: 2);
                        }
                        finally
                        {
                            _downloadSemaphore.Release();
                        }

                        itemCtx.FBPhotoID = await SendItemClass.UploadImage(itemCtx.MediaName);

                        if (await SendItemClass.SendImage(itemCtx.FBPhotoID, threadId))
                            Logger.Success($"Sent carousel image to @{username}", indent: 2);
                        else
                            Logger.Warn($"Failed to send carousel image to @{username}", indent: 2);

                        SafeDelete(itemCtx.MediaName);
                    }
                    else if (postType == 2)
                    {
                        itemCtx.MediaUrl = post.SelectToken("video_versions[0].url")?.ToString() ?? "";
                        itemCtx.MediaName = $"vid_{Guid.NewGuid():N}.mp4";

                        await _downloadSemaphore.WaitAsync();
                        try
                        {
                            if (await DownloadClass.DownloadMedia(itemCtx.MediaUrl, itemCtx.MediaName))
                                Logger.Success($"Downloaded carousel video for @{username}", indent: 2);
                            else
                                Logger.Warn($"Failed carousel video download for @{username}", indent: 2);
                        }
                        finally
                        {
                            _downloadSemaphore.Release();
                        }

                        itemCtx.FBVidID = await SendItemClass.UploadVideo(itemCtx.MediaName);

                        if (await SendItemClass.SendVideo(itemCtx.FBVidID, threadId))
                            Logger.Success($"Sent carousel video to @{username}", indent: 2);
                        else
                            Logger.Warn($"Failed to send carousel video to @{username}", indent: 2);

                        SafeDelete(itemCtx.MediaName);
                    }
                    else
                    {
                        Logger.Warn($"Unknown carousel post type: {postType}", indent: 2);
                    }
                });

                await Task.WhenAll(carouselTasks);
            }
            else
            {
                Logger.Warn($"Unknown media_type: {mediaType}", indent: 2);
            }
        }

        private static async Task HandleStoryShare(JToken item, string username, string threadId)
        {
            var ctx = new MediaContext();
            int mediaType = item.SelectToken("story_share.media.media_type")?.ToObject<int>() ?? -1;

            if (mediaType == 1)
            {
                ctx.MediaName = $"img_{Guid.NewGuid():N}.png";
                ctx.MediaUrl = item.SelectToken("story_share.media.image_versions2.candidates[0].url")?.ToString() ?? "";

                await _downloadSemaphore.WaitAsync();
                try
                {
                    if (await DownloadClass.DownloadMedia(ctx.MediaUrl, ctx.MediaName))
                        Logger.Success($"Downloaded story photo for @{username}", indent: 2);
                    else
                        Logger.Warn($"Failed to download story photo for @{username}", indent: 2);
                }
                finally
                {
                    _downloadSemaphore.Release();
                }

                ctx.FBPhotoID = await SendItemClass.UploadImage(ctx.MediaName);

                if (await SendItemClass.SendImage(ctx.FBPhotoID, threadId))
                    Logger.Success($"Sent story photo to @{username}", indent: 2);
                else
                    Logger.Warn($"Failed to send story photo to @{username}", indent: 2);

                SafeDelete(ctx.MediaName);
            }
            else if (mediaType == 2)
            {
                ctx.MediaName = $"vid_{Guid.NewGuid():N}.mp4";
                ctx.MediaUrl = item.SelectToken("story_share.media.video_versions[0].url")?.ToString() ?? "";

                await _downloadSemaphore.WaitAsync();
                try
                {
                    if (await DownloadClass.DownloadMedia(ctx.MediaUrl, ctx.MediaName))
                        Logger.Success($"Downloaded story video for @{username}", indent: 2);
                    else
                        Logger.Warn($"Failed to download story video for @{username}", indent: 2);
                }
                finally
                {
                    _downloadSemaphore.Release();
                }

                ctx.FBVidID = await SendItemClass.UploadVideo(ctx.MediaName);

                if (await SendItemClass.SendVideo(ctx.FBVidID, threadId))
                    Logger.Success($"Sent story video to @{username}", indent: 2);
                else
                    Logger.Warn($"Failed to send story video to @{username}", indent: 2);

                SafeDelete(ctx.MediaName);
            }
            else
            {
                Logger.Warn($"Unknown story media_type: {mediaType}", indent: 2);
            }
        }

        private static async Task HandleXmaMediaShare(JToken item, string username, string threadId)
        {
            var ctx = new MediaContext();
            var xma = item["xma_media_share"] as JArray;
            if (xma == null || xma.Count == 0) return;

            string TargetUrl = Regex.Unescape(xma[0]["target_url"]?.ToString()) ?? "";
            var media_type = await DownloadClass.Get_media_type(TargetUrl);
            if (media_type == 1)
            {
                ctx.MediaUrl = xma[0]?["preview_url"]?.ToString() ?? "";
                ctx.MediaName = $"img_{Guid.NewGuid():N}.png";

                await _downloadSemaphore.WaitAsync();
                try
                {
                    if (await DownloadClass.DownloadMedia(ctx.MediaUrl, ctx.MediaName))
                        Logger.Success($"Downloaded xma media for @{username}", indent: 2);
                    else
                        Logger.Warn($"Failed xma media download for @{username}", indent: 2);
                }
                finally
                {
                    _downloadSemaphore.Release();
                }

                ctx.FBPhotoID = await SendItemClass.UploadImage(ctx.MediaName);

                if (await SendItemClass.SendImage(ctx.FBPhotoID, threadId))
                    Logger.Success($"Sent xma media to @{username}", indent: 2);
                else
                    Logger.Warn($"Failed to send xma media to @{username}", indent: 2);
            }
            else if (media_type == 2)
            {
                ctx.MediaUrl = await DownloadClass.Get_xma_video_download_link(TargetUrl);
                if (String.IsNullOrWhiteSpace(ctx.MediaUrl))
                {
                    Logger.Warn($"Failed to get XMA Media Share (video) download link for @{username}", indent: 2);
                    return;
                }
                ctx.MediaName = $"vid_{Guid.NewGuid():N}.mp4";

                await _downloadSemaphore.WaitAsync();
                try
                {
                    if (await DownloadClass.DownloadMedia(ctx.MediaUrl, ctx.MediaName))
                        Logger.Success($"Downloaded XMA Media Share (video) for @{username}", indent: 2);
                    else
                        Logger.Warn($"Failed XMA Media Share (video) download for @{username}", indent: 2);
                }
                finally
                {
                    _downloadSemaphore.Release();
                }

                ctx.FBVidID = await SendItemClass.UploadVideo(ctx.MediaName);

                if (await SendItemClass.SendVideo(ctx.FBVidID, threadId))
                    Logger.Success($"Sent XMA Media Share (video) to @{username}", indent: 2);
                else
                    Logger.Warn($"Failed to send XMA Media Share (video) to @{username}", indent: 2);
            }

            SafeDelete(ctx.MediaName);
        }

        private static async Task HandleXmaStoryShare(JToken item, string username, string threadId)
        {
            var ctx = new MediaContext();
            var xma = item["xma_story_share"] as JArray;
            if (xma == null || xma.Count == 0) return;

            var TargetUrl = xma[0]["target_url"]?.ToString() ?? "";
            var StoryId = Regex.Match(TargetUrl, @"/stories/[^/]+/(\d+)").Groups[1].Value;
            var OwnerUserId = Regex.Match(TargetUrl, @"reel_owner_id=(\d+)").Groups[1].Value;

            (int MediaType, string MediaUrl) = await DownloadClass.Get_story_download_link(OwnerUserId, StoryId);
            if (String.IsNullOrWhiteSpace(MediaUrl))
            {
                Logger.Warn($"Failed to get XMA Story Share (video) download link for @{username}", indent: 2);
                return;
            }

            if (MediaType == 1)
            {
                ctx.MediaName = $"img_{Guid.NewGuid():N}.png";

                await _downloadSemaphore.WaitAsync();
                try
                {
                    if (await DownloadClass.DownloadMedia(MediaUrl, ctx.MediaName))
                        Logger.Success($"Downloaded XMA Story Share (Image) for @{username}", indent: 2);
                    else
                        Logger.Warn($"Failed XMA Story Share (Image) download for @{username}", indent: 2);
                }
                finally
                {
                    _downloadSemaphore.Release();
                }

                ctx.FBPhotoID = await SendItemClass.UploadImage(ctx.MediaName);

                if (await SendItemClass.SendImage(ctx.FBPhotoID, threadId))
                    Logger.Success($"Sent XMA Story Share (Image) to @{username}", indent: 2);
                else
                    Logger.Warn($"Failed to send XMA Story Share (Image) to @{username}", indent: 2);
            }
            else
            {
                ctx.MediaName = $"vid_{Guid.NewGuid():N}.mp4";

                await _downloadSemaphore.WaitAsync();
                try
                {
                    if (await DownloadClass.DownloadMedia(MediaUrl, ctx.MediaName))
                        Logger.Success($"Downloaded XMA Story Share (video) for @{username}", indent: 2);
                    else
                        Logger.Warn($"Failed XMA Story Share (video) download for @{username}", indent: 2);
                }
                finally
                {
                    _downloadSemaphore.Release();
                }

                ctx.FBVidID = await SendItemClass.UploadVideo(ctx.MediaName);

                if (await SendItemClass.SendVideo(ctx.FBVidID, threadId))
                    Logger.Success($"Sent XMA Story Share (video) to @{username}", indent: 2);
                else
                    Logger.Warn($"Failed to send XMA Story Share (video) to @{username}", indent: 2);
            }

            SafeDelete(ctx.MediaName);
        }

        private static async Task HandleXmaClip(JToken item, string username, string threadId)
        {
            var ctx = new MediaContext();
            try
            {
                var xma_clip = item["xma_clip"] as JArray;
                if (xma_clip == null || xma_clip.Count == 0) return;

                var TargetUrl = xma_clip[0]["target_url"]?.ToString() ?? "";

                ctx.MediaUrl = await DownloadClass.Get_xma_video_download_link(TargetUrl);
                if (String.IsNullOrWhiteSpace(ctx.MediaUrl))
                {
                    Logger.Warn($"Failed to get xma clip download link for @{username}", indent: 2);
                    return;
                }
                ctx.MediaName = $"vid_{Guid.NewGuid():N}.mp4";

                await _downloadSemaphore.WaitAsync();
                try
                {
                    if (await DownloadClass.DownloadMedia(ctx.MediaUrl, ctx.MediaName))
                        Logger.Success($"Downloaded XMA Clip (Reel) for @{username}", indent: 2);
                    else
                        Logger.Warn($"Failed XMA Clip (Reel) download for @{username}", indent: 2);
                }
                finally
                {
                    _downloadSemaphore.Release();
                }

                ctx.FBVidID = await SendItemClass.UploadVideo(ctx.MediaName);

                if (await SendItemClass.SendVideo(ctx.FBVidID, threadId))
                    Logger.Success($"Sent XMA Clip (Reel) to @{username}", indent: 2);
                else
                    Logger.Warn($"Failed to send XMA Clip (Reel) to @{username}", indent: 2);

            }
            catch (Exception ex)
            {
                Logger.Error($"HandleXmaClip error: {ex.Message}", indent: 2);
            }
            finally
            {
                SafeDelete(ctx.MediaName);
            }
        }

        private static async Task HandleGenericXma(JToken item, string username, string threadId)
        {
            var ctx = new MediaContext();
            try
            {
                var xma = item["generic_xma"] as JArray;
                if (xma == null || xma.Count == 0) return;

                var subtitle_text = xma[0]["subtitle_text"]?.ToString() ?? "";
                if (!subtitle_text.Contains(Environment.GetEnvironmentVariable("USERNAME")))
                {
                    return;
                }

                var cta_buttons = xma[0]["cta_buttons"] as JArray;
                if (cta_buttons == null || cta_buttons.Count == 0) return;

                var TargetUrl = cta_buttons[1]["action_url"]?.ToString() ?? "";
                if (String.IsNullOrWhiteSpace(TargetUrl))
                {
                    Logger.Warn($"CTA button missing action_url for @{username}", indent: 2);
                    return;
                }

                if (TargetUrl.Contains("reel"))
                {
                    ctx.MediaUrl = await DownloadClass.Get_xma_video_download_link(TargetUrl);
                    if (String.IsNullOrWhiteSpace(ctx.MediaUrl))
                    {
                        Logger.Warn($"Failed to get XMA Media Share (video) download link for @{username}", indent: 2);
                        return;
                    }
                    ctx.MediaName = $"vid_{Guid.NewGuid():N}.mp4";

                    await _downloadSemaphore.WaitAsync();
                    try
                    {
                        if (await DownloadClass.DownloadMedia(ctx.MediaUrl, ctx.MediaName))
                            Logger.Success($"Downloaded XMA Media Share (video) for @{username}", indent: 2);
                        else
                            Logger.Warn($"Failed XMA Media Share (video) download for @{username}", indent: 2);
                    }
                    finally
                    {
                        _downloadSemaphore.Release();
                    }

                    ctx.FBVidID = await SendItemClass.UploadVideo(ctx.MediaName);

                    if (await SendItemClass.SendVideo(ctx.FBVidID, threadId))
                        Logger.Success($"Sent XMA Media Share (video) to @{username}", indent: 2);
                    else
                        Logger.Warn($"Failed to send XMA Media Share (video) to @{username}", indent: 2);
                }
                else if (TargetUrl.Contains("/p/"))
                {
                    ctx.MediaUrl = xma[0]?["preview_url"]?.ToString() ?? "";
                    ctx.MediaName = $"img_{Guid.NewGuid():N}.png";

                    await _downloadSemaphore.WaitAsync();
                    try
                    {
                        if (await DownloadClass.DownloadMedia(ctx.MediaUrl, ctx.MediaName))
                            Logger.Success($"Downloaded xma media for @{username}", indent: 2);
                        else
                            Logger.Warn($"Failed xma media download for @{username}", indent: 2);
                    }
                    finally
                    {
                        _downloadSemaphore.Release();
                    }

                    ctx.FBPhotoID = await SendItemClass.UploadImage(ctx.MediaName);

                    if (await SendItemClass.SendImage(ctx.FBPhotoID, threadId))
                        Logger.Success($"Sent xma media to @{username}", indent: 2);
                    else
                        Logger.Warn($"Failed to send xma media to @{username}", indent: 2);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"HandleGenericXma error: {ex.Message}", indent: 2);
            }
            finally
            {
                SafeDelete(ctx.MediaName);
            }
        }

        private static async Task HandleClip(JToken item, string username, string threadId)
        {
            var ctx = new MediaContext();
            var vids = item["clip"]?["clip"]?["video_versions"] as JArray;
            if (vids == null || vids.Count == 0) return;

            ctx.MediaUrl = vids[0]?["url"]?.ToString() ?? "";
            ctx.MediaName = $"vid_{Guid.NewGuid():N}.mp4";

            await _downloadSemaphore.WaitAsync();
            try
            {
                if (await DownloadClass.DownloadMedia(ctx.MediaUrl, ctx.MediaName))
                    Logger.Success($"Downloaded clip for @{username}", indent: 2);
                else
                    Logger.Warn($"Failed to download clip for @{username}", indent: 2);
            }
            finally
            {
                _downloadSemaphore.Release();
            }

            ctx.FBVidID = await SendItemClass.UploadVideo(ctx.MediaName);

            if (await SendItemClass.SendVideo(ctx.FBVidID, threadId))
                Logger.Success($"Sent clip to @{username}", indent: 2);
            else
                Logger.Warn($"Failed to send clip to @{username}", indent: 2);

            SafeDelete(ctx.MediaName);
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
