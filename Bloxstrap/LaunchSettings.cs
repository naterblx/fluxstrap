using Bloxstrap.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using System.Windows;

namespace Bloxstrap
{
    public class LaunchSettings
    {
        public LaunchFlag MenuFlag                  { get; } = new("preferences,menu,settings");

        public LaunchFlag WatcherFlag               { get; } = new("watcher");

        public LaunchFlag BackgroundUpdaterFlag     { get; } = new("backgroundupdater");

        public LaunchFlag QuietFlag                 { get; } = new("quiet");

        public LaunchFlag UninstallFlag             { get; } = new("uninstall");

        public LaunchFlag NoLaunchFlag              { get; } = new("nolaunch");
        
        public LaunchFlag TestModeFlag              { get; } = new("testmode");

        public LaunchFlag NoGPUFlag                 { get; } = new("nogpu");

        public LaunchFlag UpgradeFlag               { get; } = new("upgrade");
        
        public LaunchFlag PlayerFlag                { get; } = new("player");
        
        public LaunchFlag StudioFlag                { get; } = new("studio");

        public LaunchFlag VersionFlag               { get; } = new("version");

        public LaunchFlag ChannelFlag               { get; } = new("channel");

        public LaunchFlag ForceFlag                 { get; } = new("force");

        public LaunchFlag BloxshadeFlag { get; } = new("bloxshade");

#if DEBUG
        public bool BypassUpdateCheck => true;
#else
        public bool BypassUpdateCheck => UninstallFlag.Active || WatcherFlag.Active;
#endif

        public LaunchMode RobloxLaunchMode { get; set; } = LaunchMode.None;

        public string RobloxLaunchArgs { get; set; } = "";

        /// <summary>
        /// Fluxer Studio year picked on the command line, if any
        /// </summary>
        public string? StudioYear { get; set; }

        /// <summary>
        /// Original launch arguments
        /// </summary>
        public string[] Args { get; private set; }

        public LaunchSettings(string[] args)
        {
            const string LOG_IDENT = "LaunchSettings::LaunchSettings";

#if DEBUG
            App.Logger.WriteLine(LOG_IDENT, $"Launched with arguments: {string.Join(' ', args)}");
#endif

            Args = args;

            Dictionary<string, LaunchFlag> flagMap = new();

            // build flag map
            foreach (var prop in this.GetType().GetProperties())
            {
                if (prop.PropertyType != typeof(LaunchFlag))
                    continue;

                if (prop.GetValue(this) is not LaunchFlag flag)
                    continue;

                foreach (string identifier in flag.Identifiers.Split(','))
                    flagMap.Add(identifier, flag);
            }

            int startIdx = 0;

            // infer roblox launch uris
            if (Args.Length >= 1)
            {
                string arg = Args[0];

                if (Fluxer.JoinRequest.IsFluxerUri(arg))
                {
                    App.Logger.WriteLine(LOG_IDENT, "Got Fluxer player argument");
                    RobloxLaunchMode = LaunchMode.Player;
                    RobloxLaunchArgs = arg;
                    startIdx = 1;
                }
            }

            // parse
            for (int i = startIdx; i < Args.Length; i++)
            {
                string arg = Args[i];

                if (!arg.StartsWith('-'))
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Invalid argument: {arg}");
                    continue;
                }

                string identifier = arg[1..];

                if (!flagMap.TryGetValue(identifier, out LaunchFlag? flag) || flag is null)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Unknown argument: {identifier}");
                    continue;
                }

                if (flag.Active)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Tried to set {identifier} flag twice");
                    continue;
                }

                flag.Active = true;

                if (i < Args.Length - 1 && Args[i+1] is string nextArg && !nextArg.StartsWith('-'))
                {
                    flag.Data = nextArg;
                    i++;
                    App.Logger.WriteLine(LOG_IDENT, $"Identifier '{identifier}' is active with data");
                }
                else
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Identifier '{identifier}' is active");
                }
            }

            if (PlayerFlag.Active)
                ParsePlayer(PlayerFlag.Data);
            else if (StudioFlag.Active)
                ParseStudio(StudioFlag.Data);
        }

        private void ParsePlayer(string? data)
        {
            const string LOG_IDENT = "LaunchSettings::ParsePlayer";

            RobloxLaunchMode = LaunchMode.Player;

            if (!String.IsNullOrEmpty(data))
            {
                App.Logger.WriteLine(LOG_IDENT, "Got Roblox launch arguments");
                RobloxLaunchArgs = data;
            }
            else
            {
                App.Logger.WriteLine(LOG_IDENT, "No Roblox launch arguments were provided");
            }
        }

        private void ParseStudio(string? data)
        {
            const string LOG_IDENT = "LaunchSettings::ParseStudio";

            RobloxLaunchMode = LaunchMode.Studio;

            if (String.IsNullOrEmpty(data))
            {
                App.Logger.WriteLine(LOG_IDENT, "No Fluxer Studio launch arguments were provided");
                return;
            }

            if (Fluxer.JoinRequest.IsFluxerUri(data))
            {
                App.Logger.WriteLine(LOG_IDENT, "Got Fluxer Studio launch arguments");
                RobloxLaunchArgs = data;
            }
            else if (Regex.IsMatch(data, @"^\d{4}$"))
            {
                // "-studio 2021", as used by the official launcher's studio shortcuts
                App.Logger.WriteLine(LOG_IDENT, $"Got Fluxer Studio year {data}");
                StudioYear = data;
            }
            else
            {
                App.Logger.WriteLine(LOG_IDENT, $"Unknown Fluxer Studio argument: {data}");
            }
        }
    }
}
