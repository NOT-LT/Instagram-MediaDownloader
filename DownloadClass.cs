using Newtonsoft.Json.Linq;
using RestSharp;
using Sprache;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using static System.Net.WebRequestMethods;

namespace IGMediaDownloaderV2
{
    internal class DownloadClass
    {
        // Shared HttpClient is correct.
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60)
        };

        public static async Task<string> Get_xma_video_download_link(string target_url)
        {
            try
            {
                var media_id = MediaParser.ExtractMediaId(target_url);
                var json = await Get_media_info(media_id);

                var cleanUrl = MediaParser.ExtractVideoUrl(json);

                return cleanUrl;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in Get_xma_video_download_link: {ex.Message}", indent: 2);
                return "";
            }
        }

        public static async Task<string> Get_media_info(string media_id)
        {
            // The internal API endpoint for media information
            string target_url = $"https://i.instagram.com/api/v1/media/{media_id}/info/";

            var request = new RestRequest(target_url, Method.Get);

            request.AddHeader("User-Agent", Program.IgUserAgent);
            request.AddHeader("X-Ig-App-Id", Program.IgAppId);
            RestResponse HttpResponse = await Program.IGRestClient.ExecuteAsync(request);
            var JSONResponse = HttpResponse.Content ?? "";

            if (HttpResponse.IsSuccessStatusCode)
            {
                return JSONResponse;
            } else
            {
               Logger.Error($"Failed to get media info for media_id: {media_id}. Status Code: {HttpResponse.StatusCode}", indent: 2);   
            }

            return "";
        }


        public static async Task<(Int16 MediaType, string MediaUrl)> Get_story_download_link(string owner_user_id, string reel_id) // Returns [media_type, url] where media_type is 1 for image and 2 for video
        {
            string payload = $"signed_body=SIGNATURE.{{\"exclude_media_ids\":\"[]\",\"supported_capabilities_new\":\"[{{\\\"name\\\":\\\"SUPPORTED_SDK_VERSIONS\\\",\\\"value\\\":\\\"149.0,150.0,151.0,152.0,153.0,154.0,155.0,156.0,157.0,158.0,159.0,160.0,161.0,162.0,163.0,164.0,165.0,166.0,167.0,168.0,169.0,170.0,171.0,172.0,173.0,174.0,175.0,176.0,177.0,178.0,179.0,180.0,181.0,182.0,183.0,184.0,185.0,186.0,187.0,188.0,189.0,190.0,191.0\\\"}},{{\\\"name\\\":\\\"SUPPORTED_BETA_SDK_VERSIONS\\\",\\\"value\\\":\\\"189.0-beta,190.0-beta,191.0-beta\\\"}},{{\\\"name\\\":\\\"FACE_TRACKER_VERSION\\\",\\\"value\\\":\\\"14\\\"}},{{\\\"name\\\":\\\"segmentation\\\",\\\"value\\\":\\\"segmentation_enabled\\\"}},{{\\\"name\\\":\\\"COMPRESSION\\\",\\\"value\\\":\\\"ETC2_COMPRESSION\\\"}},{{\\\"name\\\":\\\"world_tracker\\\",\\\"value\\\":\\\"world_tracker_enabled\\\"}},{{\\\"name\\\":\\\"gyroscope\\\",\\\"value\\\":\\\"gyroscope_enabled\\\"}}]\",\"reason\":\"on_tap\",\"media_id\":\"{reel_id}_{owner_user_id}\",\"source\":\"\",\"batch_size\":\"1\",\"_uid\":\"\",\"_uuid\":\"891cbf09-1663-4a98-8008-5e758f84c853\",\"reel_ids\":[\"{owner_user_id}\"]}}";
            var request = new RestRequest("https://i.instagram.com/api/v1/feed/reels_media_stream/");

            request.AddHeader("User-Agent", Program.IgUserAgent);
            request.AddHeader("X-Ig-App-Id", Program.IgAppId);
            request.AddHeader("Content-Type", "application/x-www-form-urlencoded; charset=UTF-8");
            request.AddStringBody(payload, DataFormat.None);
            RestResponse httpResponse = await Program.IGRestClient.ExecutePostAsync(request);
            var JSONResponse = httpResponse.Content ?? "";
            if (httpResponse.IsSuccessStatusCode)
            {
                var reelsObj = JObject.Parse(JSONResponse);
                var storyItems = reelsObj["reels"]?[owner_user_id]?["items"] as JArray;
                if (storyItems == null)
                    return (-1, "");
                foreach (var story in storyItems)
                {
                    var pk = story["pk"]?.ToString() ?? "";
                    if (pk == reel_id)
                    {
                        int media_type = story["media_type"]?.ToObject<int>() ?? 2;
                        if (media_type == 1)
                        {
                            var image_versions = story["image_versions2"]?["candidates"] as JArray;
                            var imageUrl = image_versions?.FirstOrDefault()?["url"]?.ToString() ?? "";
                            return (1, imageUrl);
                        }
                        else
                        {
                            var video_versions = story["video_versions"] as JArray;
                            var videoUrl = video_versions?.FirstOrDefault()?["url"]?.ToString() ?? "";
                            return (2, videoUrl);
                        }
                    }
                    break;
                }
                return (-1, "");
            }
            else
            {
                return (-1, "");
            }
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

                // --- MEDIA TYPE DETECTION ---
                var contentType = response.Content.Headers.ContentType?.MediaType;
                if (contentType != null)
                {
                    if (contentType.StartsWith("video/"))
                        Logger.Info($"Detected Video download: {contentType}", indent: 2);
                    else if (contentType.StartsWith("image/"))
                        Logger.Info($"Detected Image download: {contentType}", indent: 2);
                }
                // ----------------------------

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
    }
}
