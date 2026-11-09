namespace Bloxstrap.Fluxer
{
    public static class FluxerApi
    {
        private class PlaceDetails
        {
            [JsonPropertyName("placeId")]
            public long PlaceId { get; set; }

            [JsonPropertyName("universeId")]
            public long UniverseId { get; set; }
        }

        private class PlaceName
        {
            [JsonPropertyName("placeId")]
            public long PlaceId { get; set; }

            [JsonPropertyName("name")]
            public string Name { get; set; } = "";
        }

        private class UserList
        {
            [JsonPropertyName("data")]
            public List<UserEntry> Data { get; set; } = new();
        }

        private class UserEntry
        {
            [JsonPropertyName("id")]
            public long Id { get; set; }

            [JsonPropertyName("name")]
            public string Name { get; set; } = "";
        }

        private static readonly Dictionary<long, long> UniverseCache = new();
        private static readonly Dictionary<long, string> UsernameCache = new();
        private static readonly Dictionary<long, string> PlaceNameCache = new();

        /// <summary>
        /// Usernames for a batch of user ids. Ones that couldn't be looked up are left out.
        /// </summary>
        public static async Task<Dictionary<long, string>> GetUsernames(IEnumerable<long> userIds)
        {
            var wanted = userIds.Where(x => x > 0).Distinct().ToList();

            lock (UsernameCache)
                wanted = wanted.Where(x => !UsernameCache.ContainsKey(x)).ToList();

            if (wanted.Any())
            {
                try
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, UrlBuilder.BuildApiUrl("users", "v1/users"))
                    {
                        Content = new StringContent(JsonSerializer.Serialize(new { userIds = wanted, excludeBannedUsers = false }), Encoding.UTF8, "application/json")
                    };

                    var users = await Http.SendJson<UserList>(request);

                    lock (UsernameCache)
                    {
                        foreach (var user in users?.Data ?? new())
                            UsernameCache[user.Id] = user.Name;
                    }
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException("FluxerApi::GetUsernames", ex);
                }
            }

            lock (UsernameCache)
                return userIds.Where(UsernameCache.ContainsKey).Distinct().ToDictionary(x => x, x => UsernameCache[x]);
        }

        /// <summary>
        /// Game names for a batch of place ids. Ones that couldn't be looked up are left out.
        /// </summary>
        public static async Task<Dictionary<long, string>> GetPlaceNames(IEnumerable<long> placeIds)
        {
            var wanted = placeIds.Where(x => x > 0).Distinct().ToList();

            lock (PlaceNameCache)
                wanted = wanted.Where(x => !PlaceNameCache.ContainsKey(x)).ToList();

            if (wanted.Any())
            {
                try
                {
                    var url = UrlBuilder.BuildApiUrl("games", $"v1/games/multiget-place-details?placeIds={String.Join(",", wanted)}");
                    var places = await Http.GetJson<List<PlaceName>>(url);

                    lock (PlaceNameCache)
                    {
                        foreach (var place in places ?? new())
                            PlaceNameCache[place.PlaceId] = place.Name;
                    }
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException("FluxerApi::GetPlaceNames", ex);
                }
            }

            lock (PlaceNameCache)
                return placeIds.Where(PlaceNameCache.ContainsKey).Distinct().ToDictionary(x => x, x => PlaceNameCache[x]);
        }

        /// <summary>
        /// Looks up which universe a place belongs to. Returns 0 if it couldn't be found.
        /// </summary>
        public static long GetUniverseId(long placeId)
        {
            const string LOG_IDENT = "FluxerApi::GetUniverseId";

            lock (UniverseCache)
            {
                if (UniverseCache.TryGetValue(placeId, out long cached))
                    return cached;
            }

            try
            {
                var url = UrlBuilder.BuildApiUrl("games", $"v1/games/multiget-place-details?placeIds={placeId}");

                // this runs while reading the log, so don't let a slow site hold it up for long
                var places = Http.GetJson<List<PlaceDetails>>(url).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();

                long universeId = places?.FirstOrDefault(x => x.PlaceId == placeId)?.UniverseId ?? 0;

                lock (UniverseCache)
                    UniverseCache[placeId] = universeId;

                App.Logger.WriteLine(LOG_IDENT, $"Place {placeId} is in universe {universeId}");

                return universeId;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Could not get the universe of place {placeId}");
                App.Logger.WriteException(LOG_IDENT, ex);

                return 0;
            }
        }
    }
}
