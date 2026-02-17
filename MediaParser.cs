using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

namespace IGMediaDownloaderV2
{
    internal class MediaParser
    {
        public static string ExtractMediaId(string target_url)
        {
            try
            {
                Uri uri = new Uri(target_url);
                var query = HttpUtility.ParseQueryString(uri.Query);

                // 1. Handle Stories (Path segment + reel_owner_id)
                if (target_url.Contains("/stories/"))
                {
                    // The first part of the ID is the last segment of the path
                    string storyPart = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).Last();
                    string ownerPart = query.Get("reel_owner_id");

                    if (!string.IsNullOrEmpty(storyPart) && !string.IsNullOrEmpty(ownerPart))
                    {
                        return $"{storyPart}_{ownerPart}";
                    }
                }

                // 2. Handle Carousel Slides
                string childId = query.Get("carousel_share_child_media_id");
                if (!string.IsNullOrEmpty(childId)) return childId;

                // 3. Handle standard Reels/Posts ID parameter
                string mediaId = query.Get("id");
                if (!string.IsNullOrEmpty(mediaId)) return mediaId;

                // 4. Fallback: Regex for any existing numeric_ID_numeric pattern
                var match = Regex.Match(target_url, @"(\d+_\d+)");
                if (match.Success) return match.Value;
            }
            catch { }

            return "";
        }

        public static string ExtractImageUrlFrom(JObject json)
        {
            try
            {
                return json["items"]?
                  .FirstOrDefault()?["image_versions2"]?
                  ["candidates"]?
                  .FirstOrDefault()?["url"]?
                  .ToString() ?? "";
            }
            catch
            {
                return "";
            }
        }

        public static string ExtractVideoUrl(string json)
        {
            string pattern = @"video_versions.*?\""url\"":\""(.*?)\""";

            var match = Regex.Match(json, pattern, RegexOptions.Singleline);

            if (!match.Success)
                return "";

            string rawUrl = match.Groups[1].Value;

            // Fix escaped chars like \/ and \u0025
            return Regex.Unescape(rawUrl);
        }

        public static int GetMediaType(JObject MediaInfoJSONResponse)
        {
            var mediaType = MediaInfoJSONResponse["items"]?[0]?["media_type"]?.ToString() ?? "-1";
            return int.Parse(mediaType);
        }
        public static async Task<int> GetMediaTypeAsync(string target_url)
        {
            var media_id = ExtractMediaId(target_url);
            var JSONResponse = await DownloadClass.Get_media_info(media_id);
            var mediaType = JObject.Parse(JSONResponse)?["items"]?[0]?["media_type"]?.ToString() ?? "-1";

            return int.Parse(mediaType);
        }


        public static string[] ExtractCarouselChildMediaIds(JObject MediaInfoJSONResponse)
        {
            try
            {
                var carouselChildId = MediaInfoJSONResponse["items"]?[0]?["carousel_media_ids"] as JArray;

                if (carouselChildId != null)
                {
                    // Convert to string array
                    string[] carouselIds = carouselChildId.Select(id => id.ToString()).ToArray();
                    return carouselIds;

                }
                return [];
            }
            catch
            {
                return [];
            }
        }
    }
}