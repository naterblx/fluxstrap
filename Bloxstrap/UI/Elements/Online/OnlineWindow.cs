#if DEV_BUILD
using System.Net;
using System.Net.Http.Headers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

using Bloxstrap.Fluxer;
using Bloxstrap.Integrations;

namespace Bloxstrap.UI.Elements.Online
{
    /// <summary>
    /// Who's in game with FluxStrap right now, from the voice server's /stats (dev keys only), with usernames and
    /// game names from Fluxer. Your own server goes at the top when it's opened from the tray during a game.
    /// </summary>
    public class OnlineWindow : Window
    {
        private class Stats
        {
            [JsonPropertyName("online")]
            public int Online { get; set; }

            [JsonPropertyName("servers")]
            public List<GameServer> Servers { get; set; } = new();
        }

        private class GameServer
        {
            [JsonPropertyName("placeId")]
            public long PlaceId { get; set; }

            [JsonPropertyName("jobId")]
            public string JobId { get; set; } = "";

            [JsonPropertyName("users")]
            public List<User> Users { get; set; } = new();
        }

        private class User
        {
            [JsonPropertyName("userId")]
            public long UserId { get; set; }

            [JsonPropertyName("speaker")]
            public bool Speaker { get; set; }

            [JsonPropertyName("talking")]
            public bool Talking { get; set; }
        }

        private static readonly Brush Background1 = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C));
        private static readonly Brush Card = new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x28));
        private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0));
        private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x43, 0xD1, 0x7A));
        private static readonly Brush Orange = new SolidColorBrush(Color.FromRgb(0xF0, 0xA0, 0x40));

        private readonly string? _myJobId;
        private readonly StackPanel _content = new() { Margin = new Thickness(16) };
        private readonly TextBlock _status = new() { Foreground = Muted, FontSize = 11, Margin = new Thickness(16, 0, 16, 10) };
        private readonly DispatcherTimer _timer;

        private bool _refreshing = false;

        /// <param name="myJobId">the server you're in, if you're in a game</param>
        public OnlineWindow(string? myJobId)
        {
            _myJobId = myJobId;

            Title = "Who's online - FluxStrap";
            Width = 400;
            Height = 560;
            Background = Background1;
            Foreground = Brushes.White;
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;

            var layout = new DockPanel();
            DockPanel.SetDock(_status, Dock.Bottom);
            layout.Children.Add(_status);
            layout.Children.Add(new ScrollViewer { Content = _content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Content = layout;

            _status.Text = "Loading...";

            _timer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Normal, async (_, _) => await Refresh(), Dispatcher);
            Loaded += async (_, _) => await Refresh();
            Closed += (_, _) => _timer.Stop();
        }

        private async Task Refresh()
        {
            if (_refreshing)
                return;

            _refreshing = true;

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, VoiceChat.StatsUrl);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", App.Settings.Prop.VoiceChatKey.Trim());

                using var response = await App.HttpClient.SendAsync(request);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    _status.Text = "Your voice key wasn't accepted. Check it in Settings > Integrations.";
                    return;
                }

                response.EnsureSuccessStatusCode();

                var stats = JsonSerializer.Deserialize<Stats>(await response.Content.ReadAsStringAsync()) ?? new();
                var names = await FluxerApi.GetUsernames(stats.Servers.SelectMany(x => x.Users).Select(x => x.UserId));
                var games = await FluxerApi.GetPlaceNames(stats.Servers.Select(x => x.PlaceId));

                Draw(stats, names, games);
                _status.Text = $"Updated {DateTime.Now:T}. Counts people in game on FluxStrap 1.3.0 or newer with voice chat on.";
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("OnlineWindow::Refresh", ex);
                _status.Text = "Couldn't reach the voice server. If nobody's used it for a while it takes up to a minute to wake up.";
            }
            finally
            {
                _refreshing = false;
            }
        }

        private void Draw(Stats stats, Dictionary<long, string> names, Dictionary<long, string> games)
        {
            _content.Children.Clear();

            _content.Children.Add(new TextBlock { Text = stats.Online.ToString(), FontSize = 40, FontWeight = FontWeights.SemiBold });
            _content.Children.Add(new TextBlock
            {
                Text = $"FluxStrap {(stats.Online == 1 ? "user" : "users")} in game right now, across {stats.Servers.Count} {(stats.Servers.Count == 1 ? "server" : "servers")}",
                Foreground = Muted,
                Margin = new Thickness(0, 0, 0, 12)
            });

            var mine = _myJobId is null ? null : stats.Servers.FirstOrDefault(x => x.JobId == _myJobId);

            if (mine is not null)
            {
                AddHeading("Your server");
                AddServer(mine, names, games, true);
            }
            else if (_myJobId is not null)
            {
                AddHeading("Your server");
                _content.Children.Add(new TextBlock { Text = "Not connected to voice chat in this server yet.", Foreground = Muted, Margin = new Thickness(0, 0, 0, 12) });
            }

            var others = stats.Servers.Where(x => x != mine).ToList();

            if (others.Any())
                AddHeading(mine is null ? "Servers" : "Other servers");

            foreach (var server in others)
                AddServer(server, names, games, false);
        }

        private void AddHeading(string text) =>
            _content.Children.Add(new TextBlock { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 6) });

        private void AddServer(GameServer server, Dictionary<long, string> names, Dictionary<long, string> games, bool highlight)
        {
            var panel = new StackPanel();

            string game = games.GetValueOrDefault(server.PlaceId) ?? $"Place {server.PlaceId}";
            panel.Children.Add(new TextBlock { Text = $"{game}  ·  {server.Users.Count} {(server.Users.Count == 1 ? "user" : "users")}", FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });

            foreach (var user in server.Users.OrderByDescending(x => x.Speaker).ThenBy(x => names.GetValueOrDefault(x.UserId)))
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };

                // green while talking, orange for people who can talk, grey for everyone else
                row.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Fill = user.Talking ? Green : user.Speaker ? Orange : Muted,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                });

                string name = user.UserId == 0 ? "Unknown" : names.GetValueOrDefault(user.UserId) ?? $"User {user.UserId}";
                row.Children.Add(new TextBlock { Text = name });

                if (user.Speaker || user.Talking)
                    row.Children.Add(new TextBlock { Text = user.Talking ? "  talking" : "  can talk", Foreground = Muted });

                panel.Children.Add(row);
            }

            _content.Children.Add(new Border
            {
                Background = Card,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 8),
                BorderBrush = highlight ? Green : Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Child = panel
            });
        }
    }
}
#endif
