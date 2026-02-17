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
using System.Linq.Expressions;

namespace IGMediaDownloaderV2
{

    internal class DMClass
    {

        private class MediaContext
        {
            public string MediaName { get; set; } = "";
            public string FBPhotoID { get; set; } = "";
            public string FBVidID { get; set; } = "";
            public string MediaUrl { get; set; } = "";
        }

        // Concurrency controls
        private static readonly SemaphoreSlim _threadSemaphore = new SemaphoreSlim(
            int.TryParse(Environment.GetEnvironmentVariable("MAX_CONCURRENT_THREADS"), out var maxThreads)
                ? maxThreads : 7
        );
        private static readonly SemaphoreSlim _messageSemaphore = new SemaphoreSlim(
            int.TryParse(Environment.GetEnvironmentVariable("MAX_CONCURRENT_MESSAGES"), out var maxMsgs)
                ? maxMsgs : 7
        );
        private static readonly SemaphoreSlim _downloadSemaphore = new SemaphoreSlim(
            int.TryParse(Environment.GetEnvironmentVariable("MAX_CONCURRENT_DOWNLOADS"), out var maxDownloads)
                ? maxDownloads : 7
        );
        // Add a new semaphore for mention processing
        private static readonly SemaphoreSlim _mentionSemaphore = new SemaphoreSlim(
            int.TryParse(Environment.GetEnvironmentVariable("MAX_CONCURRENT_MENTIONS"), out var maxMentions)
                ? maxMentions : 7
        );

        private const long AgeLimitSeconds = 3600; // 1 hour



        public static async Task<string> ActivityFeedProcess(string JSONResponse)
        {
            if (string.IsNullOrEmpty(JSONResponse))
            {
                Logger.Warn("No response from Activity Feed API");
                return "No response from API";
            }

            try
            {
                var jsonObject = JObject.Parse(JSONResponse);
                long cutoffTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (Program.PollMentionDelayMS / 1000);

                var allStories = new List<JToken>();
                if (jsonObject["new_stories"] != null) allStories.AddRange(jsonObject["new_stories"]);
                if (jsonObject["old_stories"] != null) allStories.AddRange(jsonObject["old_stories"]);

                var mentionTasks = new List<Task>();
                int processedCount = 0;

                foreach (var story in allStories)
                {
                    try
                    {
                        string notifName = story["notif_name"]?.ToString();
                        var args = story["args"];

                        if (notifName != "mentioned_comment" || args == null)
                            continue;

                        double rawTs = args["timestamp"]?.Value<double>() ?? 0;
                        long storyTimestamp = (long)rawTs;

                        // 1. Time Cutoff Check - break because stories are sorted by time
                        if (storyTimestamp < cutoffTime)
                        {
                            Logger.Debug($"Reached old mentions (>{Program.PollMentionDelayMS / 1000}s), stopping");
                            break;
                        }

                        string username = args["profile_name"]?.ToString() ?? "unknown";
                        string userId = args["profile_id"]?.ToString() ?? "";
                        string mediaId = args["media"]?[0]?["id"]?.ToString() ?? "";
                        string mentionNotificationId = $"M_{mediaId}_{storyTimestamp}";
                        var threadId = Program.Store.GetThreadId(userId);

                        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(mediaId))
                        {
                            Logger.Warn($"Missing userId or mediaId for mention from @{username}");
                            await Program.Store.UpsertMessageAsync(mentionNotificationId, threadId ?? "", storyTimestamp, MessageStatus.Failed);
                            continue; // Skip this mention but continue processing others
                        }

                        // 'M_' prefix to distinguish from DM item IDs

                        // 2. Duplicate Check - continue to next mention
                        if (Program.Store.Exists(mentionNotificationId))
                        {
                            Logger.Debug($"Mention {mentionNotificationId} already processed, Skipping all older comments ...");
                            break;
                        }

                        Logger.Info($"FRESH MENTION from @{username}, Media ID: {mediaId}, UserId: {userId}");

                        // 3. Thread ID Resolution

                        if (string.IsNullOrEmpty(threadId))
                        {
                            Logger.Warn($"No thread found for @{username} (userId: {userId}). They may need to DM the bot first.");
                            await Program.Store.UpsertMessageAsync(mentionNotificationId, threadId ?? "", storyTimestamp, MessageStatus.Failed);
                            continue; // Skip this mention but continue processing others
                        }

                        // Mark as New (not Processed yet)
                        await Program.Store.UpsertMessageAsync(mentionNotificationId, threadId, storyTimestamp, MessageStatus.New);

                        // Process mention in parallel with concurrency control
                        var mentionTask = Task.Run(async () =>
                        {
                            await _mentionSemaphore.WaitAsync();
                            try
                            {
                                bool success = await PipelineMediaById(mediaId, threadId, username);

                                if (success)
                                {
                                    await Program.Store.UpsertMessageAsync(mentionNotificationId, threadId, storyTimestamp, MessageStatus.Processed);
                                    Interlocked.Increment(ref Program.ProcessedMsgs);
                                    Logger.Success($"Processed a mention download from @{username}");
                                }
                                else
                                {
                                    await Program.Store.UpsertMessageAsync(mentionNotificationId, threadId, storyTimestamp, MessageStatus.Failed);
                                    Logger.Error($"Failed to process mention from @{username}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Error($"Error processing mention from @{username}: {ex.Message}");
                                await Program.Store.UpsertMessageAsync(mentionNotificationId, threadId, storyTimestamp, MessageStatus.Failed);
                            }
                            finally
                            {
                                _mentionSemaphore.Release();
                            }
                        });

                        mentionTasks.Add(mentionTask);
                        processedCount++;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Error processing story notification: {ex.Message}");
                        continue; // Continue to next mention
                    }
                }

                //if (mentionTasks.Any())
                //{
                //    Logger.Info($"Queued {processedCount} mention(s)");

                //    Task.WhenAll(mentionTasks).ContinueWith(_ =>
                //    {
                //        Logger.Success($"Completed processing {processedCount} mention(s)");
                //    });
                //}


                return "";
            }
            catch (Exception ex)
            {
                Logger.Error($"ActivityFeedProcess error: {ex.Message}");
                return $"Error: {ex.Message}";
            }
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

                var userId = inviter["id"]?.ToString() ?? "";
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

                            if (await SendItemClass.SendText(userId, responseMsg))
                            {
                                Program.Store.RegisterThread(threadId, userId);
                                Logger.Success($"Accepted @{username} with id ${userId} and threadId of ${threadId}", indent: 1);
                            }
                            else
                            {
                                Logger.Error($"Could not send the activation message for @{username}", indent: 1);
                            }

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

                // process the messages in the threads
                var threadTasks = threads
                    .Where(t => t["users"] != null)
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
                string threadId = thread["thread_id"]?.ToString() ?? "";
                var user = thread?["users"]?[0] ?? "";
                string username = user?["username"]?.ToString() ?? "";

                Logger.Info($"Processing thread {threadId} from @{username}");

                var items = thread["items"] as JArray;
                if (items == null || items.Count == 0)
                {
                    Logger.Warn("No items", indent: 1);
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

                await Program.Store.EnsureThreadExistsAndAddAsync(threadId, user?["id"]?.ToString() ?? "");

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
                    await Program.Store.UpsertMessageAsync(messageId, threadId, ts, MessageStatus.New);
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
                    await Program.Store.SetCutoffAsync(threadId, finalCutoff);
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
                        Logger.Success($"Processed: {messageId}", indent: 2);
                        break;

                    case "xma_story_share":
                        Logger.Info($"xma_story_share: {messageId}", indent: 1);
                        await HandleXmaStoryShare(item, username, threadId);
                        Logger.Success($"Processed: {messageId}", indent: 2);
                        break;

                    case "xma_clip":
                        Logger.Info($"xma_clip: {messageId}", indent: 1);
                        await HandleXmaClip(item, username, threadId);
                        Logger.Success($"Processed: {messageId}", indent: 2);
                        break;

                    // Was used to handle mentions, currently disabled since mentions are handled via Activity Feed API for accuracy, but can be re-enabled if needed
                    //case "generic_xma":
                    //    Logger.Info($"generic_xma: {messageId}", indent: 1);
                    //    await HandleGenericXma(item, username, threadId);
                    //    Logger.Success($"Processed: {messageId}", indent: 2);
                    //    break;

                    case "media_share":
                        Logger.Info($"media_share: {messageId}", indent: 1);
                        await HandleMediaShare(item, username, threadId);
                        Logger.Success($"Processed: {messageId}", indent: 2);
                        break;

                    case "clip":
                        Logger.Info($"clip: {messageId}", indent: 1);
                        await HandleClip(item, username, threadId);
                        Logger.Success($"Processed: {messageId}", indent: 2);
                        break;

                    case "story_share":
                        Logger.Info($"story_share: {messageId}", indent: 1);
                        await HandleStoryShare(item, username, threadId);
                        Logger.Success($"Processed: {messageId}", indent: 2);
                        break;

                    case "placeholder":
                        var placeholderMsg = item["placeholder"]?["message"]?.ToString() ?? "Unavailable";
                        Logger.Warn($"placeholder: {placeholderMsg}", indent: 1);
                        break;

                    default:
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Processing failed: {ex.Message}", indent: 1);
                await Program.Store.UpsertMessageAsync(messageId, threadId, ts, MessageStatus.Failed);
            }
            finally
            {
                await Program.Store.UpsertMessageAsync(messageId, threadId, ts, MessageStatus.Processed);
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
            var mediaId = MediaParser.ExtractMediaId(xma[0]["target_url"].ToString());
            if(String.IsNullOrEmpty(mediaId.Trim()))
            {
                mediaId = item["original_media_igid"].ToString();
            }
            await PipelineMediaById(mediaId, threadId, username);
        }

        private static async Task HandleXmaStoryShare(JToken item, string username, string threadId)
        {
            var mediaId = item["original_media_igid"].ToString() ?? "";

            bool success = await PipelineMediaById(mediaId, threadId, username);

            if (success)
            {
                Logger.Success($"Processed an XMA Story download from @{username}");
            }
            else
            {
                Logger.Error($"Failed to process an XMA Story from @{username}");
            }

            //(int MediaType, string MediaUrl) = await DownloadClass.Get_story_download_link(OwnerUserId, StoryId);
            //if (String.IsNullOrWhiteSpace(MediaUrl))
            //{
            //    Logger.Warn($"Failed to get XMA Story Share (video) download link for @{username}", indent: 2);
            //    return;
            //}


            //if (MediaType == 1)
            //{
            //    ctx.MediaName = $"img_{Guid.NewGuid():N}.png";

            //    await _downloadSemaphore.WaitAsync();
            //    try
            //    {
            //        if (await DownloadClass.DownloadMedia(MediaUrl, ctx.MediaName))
            //            Logger.Success($"Downloaded XMA Story Share (Image) for @{username}", indent: 2);
            //        else
            //            Logger.Warn($"Failed XMA Story Share (Image) download for @{username}", indent: 2);
            //    }
            //    finally
            //    {
            //        _downloadSemaphore.Release();
            //    }

            //    ctx.FBPhotoID = await SendItemClass.UploadImage(ctx.MediaName);

            //    if (await SendItemClass.SendImage(ctx.FBPhotoID, threadId))
            //        Logger.Success($"Sent XMA Story Share (Image) to @{username}", indent: 2);
            //    else
            //        Logger.Warn($"Failed to send XMA Story Share (Image) to @{username}", indent: 2);
            //}
            //else
            //{
            //    ctx.MediaName = $"vid_{Guid.NewGuid():N}.mp4";

            //    await _downloadSemaphore.WaitAsync();
            //    try
            //    {
            //        if (await DownloadClass.DownloadMedia(MediaUrl, ctx.MediaName))
            //            Logger.Success($"Downloaded XMA Story Share (video) for @{username}", indent: 2);
            //        else
            //            Logger.Warn($"Failed XMA Story Share (video) download for @{username}", indent: 2);
            //    }
            //    finally
            //    {
            //        _downloadSemaphore.Release();
            //    }

            //    ctx.FBVidID = await SendItemClass.UploadVideo(ctx.MediaName);

            //    if (await SendItemClass.SendVideo(ctx.FBVidID, threadId))
            //        Logger.Success($"Sent XMA Story Share (video) to @{username}", indent: 2);
            //    else
            //        Logger.Warn($"Failed to send XMA Story Share (video) to @{username}", indent: 2);
            //}

            //SafeDelete(ctx.MediaName);
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

        // HandleGenericXma() method is deprecated and currently not used since mentions are now handled via Activity Feed API for better accuracy, but the method is left here in case we want to handle other generic_xma types in the future or if we want to revert mentions back to being handled via DM API
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


        private static async Task<bool> PipelineMediaById(string mediaId, string threadId, string username = "unknown")
        {
            MediaContext ctx = new MediaContext();
            int mediaType;

            // -------- Phase 1: Resolve media info --------
            await _messageSemaphore.WaitAsync();
            try
            {
                var MediaInfoJSON = await DownloadClass.Get_media_info(mediaId);
                var MediaInfoJObject = JObject.Parse(MediaInfoJSON);
                mediaType = MediaParser.GetMediaType(MediaInfoJObject);

                if (mediaType == 1) // PHOTO
                {
                    ctx.MediaUrl = MediaParser.ExtractImageUrlFrom(MediaInfoJObject);
                    ctx.MediaName = $"img_{Guid.NewGuid():N}.png";
                }
                else if (mediaType == 2) // VIDEO
                {
                    ctx.MediaUrl = MediaParser.ExtractVideoUrl(MediaInfoJSON);
                    ctx.MediaName = $"vid_{Guid.NewGuid():N}.mp4";
                }
                else if (mediaType == 8) // CAROUSEL
                {
                    var carouselItems = MediaParser.ExtractCarouselChildMediaIds(MediaInfoJObject);
                    Logger.Debug("carouselItems ids: " + String.Join(",",carouselItems));
                    foreach (var childMediaId in carouselItems)
                    {
                        _ = PipelineMediaById(childMediaId, threadId, username);
                    }
                    return true;
                }
                else
                {
                    return false; // Unknown type
                }

                if (string.IsNullOrWhiteSpace(ctx.MediaUrl))
                    return false;
            }
            catch (Exception ex)
            {
                Logger.Error($"PipelineMediaById error: {ex.Message}", indent: 2);
                return false;
            }
            finally
            {
                _messageSemaphore.Release();
            }

            // -------- Phase 2: Download + Upload + Send --------
            await _downloadSemaphore.WaitAsync();
            try
            {
                if (!await DownloadClass.DownloadMedia(ctx.MediaUrl, ctx.MediaName))
                    return false;

                if (mediaType == 1) // PHOTO
                {
                    ctx.FBPhotoID = await SendItemClass.UploadImage(ctx.MediaName);
                    if (await SendItemClass.SendImage(ctx.FBPhotoID, threadId))
                    {
                        Logger.Success($"Sent a an Image to @{username}");
                        return true;
                    }
                    else
                    {
                        Logger.Error($"Failed to send an Image to @{username}");
                        return false;
                    }

                }
                else // VIDEO
                {
                    ctx.FBVidID = await SendItemClass.UploadVideo(ctx.MediaName);
                    if (await SendItemClass.SendVideo(ctx.FBVidID, threadId))
                    {
                        Logger.Success($"Sent a video to @{username}");
                        return true;
                    }
                    else
                    {
                        Logger.Error($"Failed to send a videp to @{username}");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"HandleMediaOperation_ByMediaId error: {ex.Message}", indent: 2);
                return false;
            }
            finally
            {
                _downloadSemaphore.Release();
                SafeDelete(ctx.MediaName);
            }
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
