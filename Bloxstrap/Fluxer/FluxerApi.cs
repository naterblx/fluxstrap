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

        private static readonly Dictionary<long, long> UniverseCache = new();

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
