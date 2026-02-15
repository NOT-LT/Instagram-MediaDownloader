using RestSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace IGMediaDownloaderV2
{
    internal class LoginClass
    {

        public async static Task<bool> Login(string username, string password)
        {
            try
            {
                var Data = $@"signed_body=SIGNATURE.{{""jazoest"":""22478"",""country_codes"":""[{{\""country_code\"":\""1\"",\""source\"":[\""default\""]}},{{\""country_code\"":\""973\"",\""source\"":[\""sim\""]}}]"",""phone_id"":""6434cf6d-09b4-4f66-aa6b-db68aa9c1479"",""enc_password"":""#PWD_INSTAGRAM:0:0:{password}"",""username"":""{username}"",""adid"":""8ba78d7d-c87f-4369-b88d-ddf3174c309d"",""guid"":""{Guid.NewGuid()}"",""device_id"":""android-1c1487babcadb5fd"",""google_tokens"":""[]"",""login_attempt_count"":""0""}}";
                var Request = new RestRequest("https://i.instagram.com/api/v1/accounts/login/");
                Request.AddHeader("User-Agent", "Instagram 105.0.0.19.301 Android");
                Request.AddHeader("Content-Type", "application/x-www-form-urlencoded; charset=UTF-8");
                Request.AddBody(Data);
                var HttpResponse = await Program.IGRestClient.ExecutePostAsync(Request);
                var JSONResponse = HttpResponse.Content ?? "";
                if (JSONResponse.Contains($@"""username"":""{username}"""))
                {
                    Program.Authorization = HttpResponse.Headers.ToList()[0].Value.ToString();
                    var filePath = Environment.GetEnvironmentVariable("AUTH_STORE_PATH") ?? "Auth.txt";
                    await File.WriteAllTextAsync(filePath, Program.Authorization);
                    return true;
                }
                else
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"An error occurred during the login process., Error Message: {ex.Message}");
                return false;
            }
        }

        public async static Task<bool> IsValidAuthToken(string bearerToken)
        {
            var data = "surface=feed&user_id=193919249&_uuid=d152e68d-6663-41e9-bddd-a7ea9d7e7a02&bk_client_context={\"bloks_version\":\"8dab28e76d3286a104a7f1c9e0c632386603a488cf584c9b49161c2f5182fe07\",\"styles_id\":\"instagram\"}&bloks_versioning_id=8dab28e76d3286a104a7f1c9e0c632386603a488cf584c9b49161c2f5182fe07";
            const string url = "https://i.instagram.com/api/v1/clips/discover/stream/";

            // Helper to create and send requests with shared configuration
            async Task<bool> CheckStatus(Action<RestRequest> authAction)
            {
                var request = new RestRequest(url, Method.Post);
                request.AddHeader("User-Agent", Program.IgUserAgent);
                request.AddHeader("X-Ig-App-Id", "567067343352427");
                request.AddParameter("application/x-www-form-urlencoded", data, ParameterType.RequestBody);

                authAction(request);

                var response = await Program.IGRestClient.ExecuteAsync(request);
                return response.IsSuccessStatusCode;
            }

            bool bearerValid = await CheckStatus(r => r.AddHeader("Authorization", bearerToken));
            return bearerValid;
        }







    }
}
