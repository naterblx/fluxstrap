using Bloxstrap.RobloxInterfaces;

namespace Bloxstrap.Utility
{
    public static class UrlBuilder
    {
        private const string PlacelauncherBaseUrl = Fluxer.FluxerSite.PlaceLauncherUrl;

        public static Uri BuildApiUrl(string service, string path, bool secure = true)
        {
            // fluxer serves the roblox web apis under one path instead of subdomains
            // e.g. games.roblox.com/v1/games -> fluxr.lol/apisite/games/v1/games
            string url = Fluxer.FluxerSite.ApiUrl + "/";
            url += service + "/";
            url += path;

            if (!secure)
                url = url.Replace("https://", "http://");

            return new(url);
        }

        public static string BuildPlacelauncherUrl(long placeId, string? jobId)
        {
            string url = PlacelauncherBaseUrl;
            url += "?request=RequestGameJob&placeId=";
            url += placeId;
            
            if (jobId is not null)
            {
                url += "&gameId=";
                url += jobId;
            }

            return url;
        }

        public static string BuildPrivateGamePlaceLauncher(long placeId, string accessCode)
        {
            string url = PlacelauncherBaseUrl;
            url += "?request=RequestPrivateGame&placeId=";
            url += placeId;
            url += "&accessCode=";
            url += accessCode;

            return url;
        }

        public static string BuildFollowUserPlaceLauncher(long userId)
        {
            string url = PlacelauncherBaseUrl;
            url += "?request=RequestFollowUser&userId=";
            url += userId;

            return url;
        }
    }
}
