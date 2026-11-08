using System.Security.Cryptography;

namespace Bloxstrap.Fluxer
{
    /// <summary>
    /// Starts a Fluxer client with the arguments its year expects
    /// </summary>
    public static class ClientLauncher
    {
        /// <summary>
        /// How long the domain patcher keeps scanning a freshly started client
        /// </summary>
        private const int PatchSeconds = 120;

        private const int StudioPatchSeconds = 60;

        private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

        /// <summary>
        /// Returns the process id of the started client, or 0 if it couldn't be started
        /// </summary>
        public static int LaunchPlayer(ClientPackage package, JoinRequest request)
        {
            const string LOG_IDENT = "ClientLauncher::LaunchPlayer";

            string executable = PickExecutable(package);
            string directory = package.InstallDirectory;

            App.Logger.WriteLine(LOG_IDENT, $"Starting {package.Label} from {executable}");

            if (package.IsPrivate)
            {
                try
                {
                    WriteClientLogin(request.Ticket!);
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LOG_IDENT, "Could not store the login for the 2026 client");
                    App.Logger.WriteException(LOG_IDENT, ex);
                }

                string placeId = String.IsNullOrEmpty(request.PlaceId) ? "1" : request.PlaceId;
                string args = "-play -a " + Quote($"roblox://experiences/start?placeId={placeId}");

                var result = new ClientPatcher.Result();

                ClientPatcher.BrandPairs = new[]
                {
                    new[] { "Roblox", "Fluxer" },
                    new[] { "ROBLOX", "FLUXER" },
                    new[] { "Roblox Player", "Fluxer Player" },
                    new[] { "Roblox Crash", "Fluxer Crash" }
                };

                int pid = ClientPatcher.Launch(executable, args, directory, FluxerSite.PatchedDomain, FluxerSite.Domain, PatchSeconds, result);

                App.Logger.WriteLine(LOG_IDENT, $"2026 client pid={pid} patched={result.Patched}");

                if (pid != 0)
                    ClientIcon.Apply(pid);

                return pid;
            }

            if (package.Year == "2022")
            {
                string args = $"--app -a {Quote(FluxerSite.AuthenticationUrl)} -t {Quote(request.Ticket!)} -j {Quote(request.JoinUrl!)} "
                    + $"--launchtime={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} --rloc en_us --gloc en_us -b 0";

                var result = new ClientPatcher.Result();
                ClientPatcher.BrandPairs = null;

                int pid = ClientPatcher.Launch(executable, args, directory, FluxerSite.PatchedDomain, FluxerSite.Domain, PatchSeconds, result);

                App.Logger.WriteLine(LOG_IDENT, $"2022 client pid={pid} patched={result.Patched}");

                return pid;
            }

            string playArgs = package.Year is "2019" or "2021"
                ? $"--play -a {Quote(FluxerSite.AuthenticationUrl)} -t {Quote(request.Ticket!)} -j {Quote(request.JoinUrl!)}"
                : $"--play --authenticationUrl {Quote(FluxerSite.AuthenticationUrl)} --authenticationTicket {Quote(request.Ticket!)} --joinScriptUrl {Quote(request.JoinUrl!)}";

            return StartWithSingletonRename(executable, playArgs, directory);
        }

        public static int LaunchStudio(ClientPackage package, JoinRequest? request)
        {
            const string LOG_IDENT = "ClientLauncher::LaunchStudio";

            string args = "";

            if (!String.IsNullOrEmpty(request?.PlaceId))
            {
                args = $"-task EditPlace -placeId {request.PlaceId}";

                if (!String.IsNullOrEmpty(request.UniverseId))
                    args += $" -universeId {request.UniverseId}";
            }

            App.Logger.WriteLine(LOG_IDENT, $"Starting {package.Label} ({args})");

            if (package.Year == "2019")
            {
                var result = new ClientPatcher.Result();
                ClientPatcher.BrandPairs = null;

                int pid = ClientPatcher.Launch(package.ExecutablePath, args, package.InstallDirectory, FluxerSite.PatchedDomain, FluxerSite.Domain, StudioPatchSeconds, result);

                App.Logger.WriteLine(LOG_IDENT, $"2019 Studio pid={pid} patched={result.Patched}");

                return pid;
            }

            return StartWithSingletonRename(package.ExecutablePath, args, package.InstallDirectory);
        }

        /// <summary>
        /// Starts the client with its singleton mutex renamed, so more than one can run at once
        /// </summary>
        private static int StartWithSingletonRename(string executable, string args, string directory)
        {
            const string LOG_IDENT = "ClientLauncher::StartWithSingletonRename";

            try
            {
                using var process = SingletonRename.Start(executable, args, directory);
                return process.Id;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "Singleton rename failed, starting normally");
                App.Logger.WriteException(LOG_IDENT, ex);
            }

            using var fallback = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = args,
                WorkingDirectory = directory,
                UseShellExecute = false
            });

            return fallback?.Id ?? 0;
        }

        /// <summary>
        /// Clients can ship numbered copies (FluxerPlayerBeta_multi2.exe ...) so several can run side by side
        /// </summary>
        private static string PickExecutable(ClientPackage package)
        {
            string leaf = Path.GetFileNameWithoutExtension(package.ExecutableName);

            var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var processes = Process.GetProcessesByName(leaf)
                .Concat(Enumerable.Range(2, 3).SelectMany(i => Process.GetProcessesByName($"{leaf}_multi{i}")));

            foreach (var process in processes)
            {
                try
                {
                    running.Add(process.MainModule!.FileName!);
                }
                catch (Exception) { }
                finally
                {
                    process.Dispose();
                }
            }

            var candidates = new List<string> { package.ExecutablePath };

            for (int i = 2; i <= 4; i++)
                candidates.Add(Path.Combine(package.InstallDirectory, $"{leaf}_multi{i}.exe"));

            return candidates.FirstOrDefault(x => File.Exists(x) && !running.Contains(x)) ?? candidates[0];
        }

        /// <summary>
        /// The 2026 client reads its login from the cookie store, like the modern client does
        /// </summary>
        private static void WriteClientLogin(string ticket)
        {
            if (String.IsNullOrEmpty(ticket))
                return;

            string directory = Path.Combine(Paths.LocalAppData, "Roblox", "LocalStorage");
            Directory.CreateDirectory(directory);

            long expiry = DateTimeOffset.UtcNow.AddDays(365).ToUnixTimeSeconds();
            string cookie = $"#HttpOnly_.{FluxerSite.Domain}\tTRUE\t/\tTRUE\t{expiry}\t.ROBLOSECURITY\t{ticket}";

            byte[] encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(cookie), null, DataProtectionScope.CurrentUser);
            string contents = $"{{\"CookiesVersion\":\"1\",\"CookiesData\":\"{Convert.ToBase64String(encrypted)}\"}}";

            foreach (string name in new[] { "RobloxCookies.dat", $"{FluxerSite.Domain}_RobloxCookies.dat", $"www.{FluxerSite.Domain}_RobloxCookies.dat" })
                File.WriteAllText(Path.Combine(directory, name), contents, new UTF8Encoding(false));
        }
    }
}
