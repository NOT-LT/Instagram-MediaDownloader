using RestSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IGMediaDownloaderV2
{
    internal class InstagramApiClient
    {

        public static async Task<string> GetDirectMessagesAsync()
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

        public static async Task<string> GetPendingDMInboxAsync()
        {
            var request = new RestRequest("https://i.instagram.com/api/v1/direct_v2/pending_inbox/", Method.Get);
            request.AddHeader("User-Agent", Program.IgUserAgent);
            request.AddHeader("X-Ig-App-Id", Program.IgAppId);
            request.AddHeader("X-Mid", "Y-TgjgABAAGyBUo6fl_dzKH6iPdK");
            request.AddHeader("Ig-U-Ds-User-Id", Random.Shared.Next(999_999_999));

            RestResponse httpResponse = await Program.IGRestClient.ExecuteAsync(request);
            return httpResponse.Content ?? string.Empty;
        }



        public static async Task<string> GetActivityFeedAsync() // For mentions
        {
            var request = new RestRequest("/api/v1/news/inbox/?could_truncate_feed=true&should_skip_su=true&mark_as_seen=false&timezone_offset=28800&timezone_name=Asia%2FShanghai", Method.Get);
            request.AddHeader("User-Agent", Program.IgUserAgent);
            request.AddHeader("X-Ig-App-Id", Program.IgAppId);
            request.AddHeader("X-Mid", "aYuA4gABAAFoIhgQirMYy3a98zul");
            request.AddHeader("Ig-U-Ds-User-Id", Random.Shared.Next(999_999_999));
            RestResponse httpResponse = await Program.IGRestClient.ExecuteAsync(request);
            return httpResponse.Content ?? string.Empty;
        }

    }
}
