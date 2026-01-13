using RestSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IGMediaDownloader
{
    internal class LoginClass
    {

        public async static Task<bool> Login(string username, string password)
        {
            var Data = $@"signed_body=SIGNATURE.{{""jazoest"":""22478"",""country_codes"":""[{{\""country_code\"":\""1\"",\""source\"":[\""default\""]}},{{\""country_code\"":\""973\"",\""source\"":[\""sim\""]}}]"",""phone_id"":""6434cf6d-09b4-4f66-aa6b-db68aa9c1479"",""enc_password"":""#PWD_INSTAGRAM:0:0:{password}"",""username"":""{username}"",""adid"":""8ba78d7d-c87f-4369-b88d-ddf3174c309d"",""guid"":""{Guid.NewGuid()}"",""device_id"":""android-1c1487babcadb5fd"",""google_tokens"":""[]"",""login_attempt_count"":""0""}}";
            var Request = new RestRequest("https://i.instagram.com/api/v1/accounts/login/");
            Request.AddHeader("User-Agent", "Instagram 265.0.0.19.301 Android");
            Request.AddHeader("Content-Type", "application/x-www-form-urlencoded; charset=UTF-8");
            Request.AddBody(Data);
            var HttpResponse = await Program.IGRestClient.ExecutePostAsync(Request);
            var JSONResponse = HttpResponse.Content;
            if (JSONResponse.Contains($@"""username"":""{username}"""))
            {
                Program.Authorization = HttpResponse.Headers.ToList()[0].Value.ToString();
                return true;
            }
            else
            {
                return false;
            }
        }

    }
}
