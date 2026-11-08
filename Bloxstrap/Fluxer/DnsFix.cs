using System.Net.Sockets;
using System.Windows;

namespace Bloxstrap.Fluxer
{
    /// <summary>
    /// Some routers can't resolve Fluxer's client settings host, which stops the clients from starting.
    /// Like the official launcher, we resolve it over DNS-over-HTTPS and pin it in the hosts file.
    /// </summary>
    public static class DnsFix
    {
        public const string FlagName = "-fixdns";

        private static readonly string[] Hosts = { $"clientsettingscdn.{FluxerSite.Domain}" };

        private static string HostsFile => Path.Combine(Paths.System, "drivers", "etc", "hosts");

        public static async Task EnsureResolvable()
        {
            const string LOG_IDENT = "DnsFix::EnsureResolvable";

            foreach (string host in Hosts)
            {
                try
                {
                    var addresses = await Dns.GetHostAddressesAsync(host);

                    if (addresses.Any(x => x.AddressFamily == AddressFamily.InterNetwork))
                        continue;
                }
                catch (Exception) { }

                App.Logger.WriteLine(LOG_IDENT, $"{host} does not resolve locally");

                string? address = await ResolveOverHttps(host);

                if (address is null)
                    continue;

                try
                {
                    Pin(host, address);
                    App.Logger.WriteLine(LOG_IDENT, $"Pinned {host} to {address}");
                    continue;
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LOG_IDENT, "Could not write the hosts file, asking for elevation");
                    App.Logger.WriteException(LOG_IDENT, ex);
                }

                try
                {
                    using var process = Process.Start(new ProcessStartInfo
                    {
                        FileName = Paths.Process,
                        Arguments = $"{FlagName} {host} {address}",
                        Verb = "runas",
                        UseShellExecute = true
                    });

                    process?.WaitForExit();

                    if ((await Dns.GetHostAddressesAsync(host)).Any())
                        continue;
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException(LOG_IDENT, ex);
                }

                Frontend.ShowMessageBox(
                    $"Your router can't resolve {host}.\n\nEither run {App.ProjectName} once as administrator, or set your DNS server to 1.1.1.1 / 1.0.0.1 (Settings > Network > adapter > DNS).",
                    MessageBoxImage.Warning
                );
            }
        }

        /// <summary>
        /// Handles the elevated "-fixdns host address" relaunch. Returns true if it was one.
        /// </summary>
        public static bool HandleElevatedLaunch(string[] args)
        {
            if (args.Length != 3 || args[0] != FlagName)
                return false;

            // only ever touch our own hosts, and only with a plain IPv4 address
            if (Hosts.Contains(args[1]) && IPAddress.TryParse(args[2], out var address) && address.AddressFamily == AddressFamily.InterNetwork)
            {
                try
                {
                    Pin(args[1], address.ToString());
                }
                catch (Exception) { }
            }

            return true;
        }

        private static void Pin(string host, string address)
        {
            var lines = File.ReadAllLines(HostsFile).Where(x => !x.Contains(host)).ToList();
            lines.Add($"{address} {host} # {App.ProjectName}");
            File.WriteAllLines(HostsFile, lines);
        }

        private static async Task<string?> ResolveOverHttps(string host)
        {
            const string LOG_IDENT = "DnsFix::ResolveOverHttps";

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, $"https://1.1.1.1/dns-query?name={host}&type=A");
                request.Headers.Add("Accept", "application/dns-json");

                var response = await App.HttpClient.SendAsync(request);
                string body = await response.Content.ReadAsStringAsync();

                var match = Regex.Match(body, @"""data""\s*:\s*""(\d+\.\d+\.\d+\.\d+)""");

                if (match.Success)
                    return match.Groups[1].Value;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "DNS-over-HTTPS lookup failed");
                App.Logger.WriteException(LOG_IDENT, ex);
            }

            return null;
        }
    }
}
