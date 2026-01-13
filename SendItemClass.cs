using Newtonsoft.Json.Linq;
using RestSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IGMediaDownloader
{
    internal class SendItemClass
    {

        public async static Task<string> UploadImage(string MediaName)
        {
            var EntityName = $"{new Random().Next(999999999)}_0_{new Random().Next(899999999)}";
            byte[] imageArray = System.IO.File.ReadAllBytes($"{MediaName}");
            RestRequest Request = new RestRequest($"https://rupload.facebook.com/messenger_image/{EntityName}");
            Request.AddHeader("User-Agent", "Instagram 265.0.0.19.301 Android");
            //Request.AddHeader("X-Ig-App-Id", "567067343352427");
            //Request.AddHeader("X-Mid", "XOkgFgAAAAFYtHhheQRQBAhzSnhS");
            //Request.AddHeader("Ig-U-Ds-User-Id", new Random().Next(999999999));
            Request.AddHeader("Content-Type", "application/octet-stream");
            Request.AddHeader("X-Entity-Name", EntityName);
            Request.AddHeader("X-Entity-Length", imageArray.Length);
            Request.AddHeader("Image_type", "FILE_ATTACHMENT");
            Request.AddHeader("Offset", "0");
            Request.AddBody(imageArray, "application/octet-stream");
            RestResponse HttpResponse = await Program.FBRestClient.ExecutePostAsync(Request);
            var JSONResponse = HttpResponse.Content;
            var FBPhotoID = JObject.Parse(JSONResponse).SelectToken("media_id").ToString();
            return FBPhotoID;
        }

        public async static Task<bool> SendImage(string FBPhotoID, string ThreadID)
        {
            var ClientContext = Convert.ToString(new Random().Next(999999999));
            var Request = new RestRequest("https://i.instagram.com/api/v1/direct_v2/threads/broadcast/photo_attachment/");
            Request.AddHeader("User-Agent", "Instagram 265.0.0.19.301 Android");
            //Request.AddHeader("X-Ig-App-Id", "567067343352427");
            //Request.AddHeader("X-Mid", "XOkgFgAAAAFYtHhheQRQBAhzSnhS");
            //Request.AddHeader("Ig-U-Ds-User-Id", new Random().Next(999999999));
            Request.AddHeader("Content-Type", "application/x-www-form-urlencoded; charset=UTF-8");
            Request.AddStringBody($"action=send_item&is_x_transport_forward=false&is_shh_mode=0&thread_ids=[{ThreadID}]&send_attribution=direct_thread&client_context={ClientContext}&attachment_fbid={FBPhotoID}&device_id=android-1c1487babcadb5fd&mutation_token={ClientContext}&_uuid=2017b4cf-8663-4731-a169-5c345918f5e2&allow_full_aspect_ratio=true&nav_chain=MainFeedFragment:feed_timeline:1:cold_start:1675526443.62::,DirectInboxFragment:direct_inbox:3:on_launch_direct_inbox:1675526459.808::,DirectThreadFragment:direct_thread:9:inbox:1675530400.355::,TRUNCATEDx1,DirectThreadFragment:direct_thread:12:button:1675531679.548::,DirectMediaPickerPhotosFragment:direct_media_picker_photos_fragment:13:button:1675532284.361::,DirectThreadFragment:direct_thread:14:button:1675532287.289::,DirectMediaPickerPhotosFragment:direct_media_picker_photos_fragment:15:button:1675532431.383::,DirectThreadFragment:direct_thread:16:button:1675532434.816::,DirectMediaPickerPhotosFragment:direct_media_picker_photos_fragment:17:button:1675532461.143::,DirectThreadFragment:direct_thread:18:button:1675532463.911::&offline_threading_id={ClientContext}", DataFormat.None);

            RestResponse HttpResponse = await Program.IGRestClient.ExecutePostAsync(Request);
            var JSONResponse = HttpResponse.Content;
            if (JSONResponse.Contains(@"""status"":""ok"""))
                return true;
            else
                return false;
        }



        public async static Task<string> UploadVideo(string MediaName)
        {
            byte[] VidArray = System.IO.File.ReadAllBytes($"{MediaName}");
            var EntityName = $"{new Random().Next(999999999)}-0-{VidArray.Length}-lessO-{new Random().Next(899999999)}";
            RestRequest Request = new RestRequest($"https://rupload.facebook.com/messenger_video/{EntityName}");
            Request.AddHeader("User-Agent", "Instagram 265.0.0.19.301 Android");
            Request.AddHeader("X-Ig-App-Id", "567067343352427");
            Request.AddHeader("Content-Type", "application/octet-stream");
            Request.AddHeader("X-Entity-Name", EntityName);
            Request.AddHeader("X-Entity-Type", "video/mp4");
            Request.AddHeader("X-Entity-Length", VidArray.Length);
            Request.AddHeader("Video_type", "FILE_ATTACHMENT");
            Request.AddHeader("Offset", "0");
            Request.AddBody(VidArray, "application/octet-stream");

            RestResponse HttpResponse = await Program.FBRestClient.ExecutePostAsync(Request);
            var JSONResponse = HttpResponse.Content;
            var VidPhotoID = JObject.Parse(JSONResponse).SelectToken("media_id").ToString();
            return VidPhotoID;
        }

        public async static Task<bool> SendVideo(string FBVidID, string ThreadID)
        {
            var ClientContext = Convert.ToString(new Random().Next(999999999));
            var Request = new RestRequest("https://i.instagram.com/api/v1/direct_v2/threads/broadcast/video_attachment/");
            Request.AddHeader("User-Agent", "Instagram 265.0.0.19.301 Android");
            //Request.AddHeader("X-Ig-App-Id", "567067343352427");
            //Request.AddHeader("X-Mid", "XOkgFgAAAAFYtHhheQRQBAhzSnhS");
            //Request.AddHeader("Ig-U-Ds-User-Id", new Random().Next(999999999));
            Request.AddHeader("Content-Type", "application/x-www-form-urlencoded; charset=UTF-8");
            Request.AddStringBody($"action=send_item&is_x_transport_forward=false&is_shh_mode=0&thread_ids=[{ThreadID}]&send_attribution=direct_thread&client_context={ClientContext}&attachment_fbid={FBVidID}&video_result={FBVidID}&device_id=android-1c1487babcadb5fd&mutation_token={ClientContext}&_uuid=2017b4cf-8663-4731-a169-5c345918f5e2&allow_full_aspect_ratio=true&nav_chain=MainFeedFragment:feed_timeline:1:cold_start:1675526443.62::,DirectInboxFragment:direct_inbox:3:on_launch_direct_inbox:1675526459.808::,DirectThreadFragment:direct_thread:9:inbox:1675530400.355::,TRUNCATEDx1,DirectThreadFragment:direct_thread:12:button:1675531679.548::,DirectMediaPickerPhotosFragment:direct_media_picker_photos_fragment:13:button:1675532284.361::,DirectThreadFragment:direct_thread:14:button:1675532287.289::,DirectMediaPickerPhotosFragment:direct_media_picker_photos_fragment:15:button:1675532431.383::,DirectThreadFragment:direct_thread:16:button:1675532434.816::,DirectMediaPickerPhotosFragment:direct_media_picker_photos_fragment:17:button:1675532461.143::,DirectThreadFragment:direct_thread:18:button:1675532463.911::&offline_threading_id={ClientContext}", DataFormat.None);

            RestResponse HttpResponse = await Program.IGRestClient.ExecutePostAsync(Request);
            var JSONResponse = HttpResponse.Content;
            if (JSONResponse.Contains(@"""status"":""ok"""))
                return true;
            else
                return false;
        }

    }
}
