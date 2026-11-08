using System.IO.Compression;

namespace Bloxstrap.Fluxer
{
    /// <summary>
    /// Which Fast Flags each Fluxer client actually registers, read out of the running clients
    /// by Scripts/FluxerFlags/dump_flags.py and bundled as Resources/FluxerFlags/&lt;year&gt;.txt.gz
    /// </summary>
    public static class FlagSupport
    {
        private static readonly string[] TypePrefixes = { "DFFlag", "DFInt", "DFString", "DFLog", "SFFlag", "FFlag", "FInt", "FString", "FLog" };

        private static readonly Dictionary<string, HashSet<string>?> Cache = new();

        /// <summary>
        /// The registered flag names (without their type prefix) for a client year, or null if we have no list for it
        /// </summary>
        public static HashSet<string>? GetNames(string year)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(year, out var cached))
                    return cached;

                HashSet<string>? names = null;

                try
                {
                    using var stream = Resource.GetStream($"FluxerFlags.{year}.txt.gz");
                    using var gzip = new GZipStream(stream, CompressionMode.Decompress);
                    using var reader = new StreamReader(gzip);

                    names = new HashSet<string>(reader.ReadToEnd().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
                }
                catch (InvalidOperationException)
                {
                    // no list for this client (studio, the 2026 client)
                }

                Cache[year] = names;
                return names;
            }
        }

        public static string StripPrefix(string flag)
        {
            foreach (string prefix in TypePrefixes)
            {
                if (flag.StartsWith(prefix, StringComparison.Ordinal) && flag.Length > prefix.Length)
                    return flag[prefix.Length..];
            }

            return flag;
        }

        /// <summary>
        /// Null when it's not known either way (no list for that client)
        /// </summary>
        public static bool? IsSupported(string year, string flag)
        {
            var names = GetNames(year);

            if (names is null)
                return null;

            return names.Contains(StripPrefix(flag));
        }

        /// <summary>
        /// Years of the player clients we have lists for
        /// </summary>
        public static readonly string[] KnownYears = { "2019", "2020", "2021", "2022" };
    }
}
