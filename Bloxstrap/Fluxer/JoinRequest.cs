using System.Web;

namespace Bloxstrap.Fluxer
{
    /// <summary>
    /// A parsed fluxer-player: or fluxerclient:// launch link
    /// </summary>
    public class JoinRequest
    {
        public string Version { get; set; } = "2021L";

        public string? Ticket { get; set; }

        public string? JoinUrl { get; set; }

        public string? PlaceId { get; set; }

        public string? UniverseId { get; set; }

        public bool Studio { get; set; }

        public bool HasTicket => !String.IsNullOrEmpty(Ticket) && !String.IsNullOrEmpty(JoinUrl);

        public static bool IsFluxerUri(string uri) => FluxerSite.Schemes.Any(x => uri.StartsWith(x + ":", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Returns null if the link isn't a usable Fluxer game link
        /// </summary>
        public static JoinRequest? Parse(string uri)
        {
            const string LOG_IDENT = "JoinRequest::Parse";

            int fragmentIdx = uri.IndexOf('#');

            if (fragmentIdx >= 0)
                uri = uri[..fragmentIdx];

            var request = new JoinRequest();

            if (uri.StartsWith("fluxer-player:", StringComparison.OrdinalIgnoreCase))
            {
                // fluxer-player:1+launchmode:play+gameinfo:<ticket>+placelauncherurl:<url>+clientversion:2021L
                foreach (string segment in uri["fluxer-player:".Length..].Split('+', StringSplitOptions.RemoveEmptyEntries))
                {
                    int separatorIdx = segment.IndexOf(':');

                    if (separatorIdx < 0)
                        continue;

                    string key = segment[..separatorIdx].ToLowerInvariant();
                    string value = segment[(separatorIdx + 1)..];

                    switch (key)
                    {
                        case "gameinfo":
                            request.Ticket = value;
                            break;

                        case "placelauncherurl":
                            request.JoinUrl = value;
                            break;

                        case "clientversion":
                            request.Version = value;
                            break;

                        case "placeid":
                            request.PlaceId = value;
                            break;

                        case "universeid":
                            request.UniverseId = value;
                            break;

                        case "launchmode":
                            if (value.Equals("edit", StringComparison.OrdinalIgnoreCase))
                                request.Studio = true;
                            break;
                    }
                }
            }
            else if (uri.StartsWith("fluxerclient://", StringComparison.OrdinalIgnoreCase))
            {
                // fluxerclient://join?place=123&ticket=<ticket>&year=2021
                var query = HttpUtility.ParseQueryString(new Uri(uri).Query);

                string? place = query["place"] ?? query["placeId"];
                string year = query["year"] ?? "2021";

                request.Ticket = query["ticket"];
                request.Version = year.EndsWith("L", StringComparison.OrdinalIgnoreCase) ? year : year + "L";

                if (place is not null)
                {
                    request.PlaceId = place;
                    request.JoinUrl = $"{FluxerSite.PlaceLauncherUrl}?request=RequestGame&placeId={Uri.EscapeDataString(place)}&isPartyLeader=false&gender=&isTeleport=true";
                }
            }
            else
            {
                App.Logger.WriteLine(LOG_IDENT, "Not a Fluxer link");
                return null;
            }

            if (request.Studio && !String.IsNullOrEmpty(request.PlaceId))
            {
                if (!request.Version.EndsWith("S", StringComparison.OrdinalIgnoreCase))
                    request.Version = request.Version[..Math.Min(4, request.Version.Length)] + "S";

                return request;
            }

            if (!request.HasTicket)
            {
                App.Logger.WriteLine(LOG_IDENT, "Link is missing a ticket or join url");
                return null;
            }

            request.JoinUrl = FluxerSite.ToPublicHost(request.JoinUrl!);

            if (String.IsNullOrEmpty(request.PlaceId))
            {
                var match = Regex.Match(request.JoinUrl, @"placeId=(\d+)", RegexOptions.IgnoreCase);

                if (match.Success)
                    request.PlaceId = match.Groups[1].Value;
            }

            return request;
        }
    }
}
