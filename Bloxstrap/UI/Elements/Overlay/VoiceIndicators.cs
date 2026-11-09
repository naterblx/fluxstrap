using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Path = System.Windows.Shapes.Path;
using Rectangle = System.Windows.Shapes.Rectangle;

using Bloxstrap.Integrations;

namespace Bloxstrap.UI.Elements.Overlay
{
    /// <summary>
    /// Voice chat bubbles over the heads of whoever's talking, like Roblox's, with bars showing how loud they are.
    /// Positions come from the voice server and our camera (see Fluxer/CameraReader.cs), projected onto the game window.
    /// Clicks go straight through to the game.
    /// </summary>
    public class VoiceIndicators : OverlayWindow
    {
        // how far above the speaker's camera focus to put the bubble, clear of their head and name
        private const float HeadHeight = 4.5f;

        private static readonly Geometry MicGeometry = Geometry.Parse("M12,3 A3,3 0 0 0 9,6 V11 A3,3 0 0 0 15,11 V6 A3,3 0 0 0 12,3 Z M18,11 A6,6 0 0 1 6,11 M12,17 V21 M9,21 H15");
        private static readonly Brush BubbleBrush = new SolidColorBrush(Color.FromArgb(0xC8, 0x14, 0x14, 0x14));
        private static readonly Brush LevelBrush = new SolidColorBrush(Color.FromRgb(0x43, 0xD1, 0x7A));
        private static readonly Brush QuietBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF));

        private readonly VoiceChat _voiceChat;
        private readonly Canvas _canvas = new();
        private readonly Dictionary<string, Bubble> _bubbles = new();

        private List<VoiceChat.TalkingSpeaker> _talking = new();

        private class Bubble
        {
            public readonly Border Root;
            public readonly Rectangle[] Bars = new Rectangle[3];
            public double Level;

            public Bubble(string name)
            {
                var bars = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) };

                for (int i = 0; i < Bars.Length; i++)
                {
                    Bars[i] = new Rectangle { Width = 3, Height = 4, RadiusX = 1.5, RadiusY = 1.5, Margin = new Thickness(1, 0, 1, 0), VerticalAlignment = VerticalAlignment.Center };
                    bars.Children.Add(Bars[i]);
                }

                var mic = new Viewbox
                {
                    Width = 14,
                    Height = 14,
                    Child = new Path { Data = MicGeometry, Stroke = Brushes.White, StrokeThickness = 2.2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Width = 24, Height = 24 }
                };

                var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                row.Children.Add(mic);
                row.Children.Add(bars);

                var stack = new StackPanel();
                stack.Children.Add(row);
                stack.Children.Add(new TextBlock { Text = name, Foreground = Brushes.White, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 0) });

                Root = new Border { Background = BubbleBrush, CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 3, 7, 3), Child = stack };
            }
        }

        public VoiceIndicators(VoiceChat voiceChat, int gamePid) : base(gamePid, true, TimeSpan.FromMilliseconds(33))
        {
            _voiceChat = voiceChat;
            Title = "FluxStrap voice indicators";
            Content = _canvas;
        }

        protected override bool HasContent
        {
            get
            {
                _talking = _voiceChat.GetTalkingSpeakers();
                return _talking.Count > 0 || _bubbles.Count > 0;
            }
        }

        protected override void Update(Int32Rect client, double scale)
        {
            Place(client.X, client.Y, client.Width, client.Height);

            double width = client.Width / scale, height = client.Height / scale;
            var camera = _voiceChat.Camera;
            var seen = new HashSet<string>();

            foreach (var speaker in _talking)
            {
                if (camera is null || Project(camera.Value, speaker.Position + new Vector3(0, HeadHeight, 0), width, height) is not Point point)
                    continue;

                seen.Add(speaker.Name);

                if (!_bubbles.TryGetValue(speaker.Name, out var bubble))
                {
                    bubble = new Bubble(speaker.Name);
                    _bubbles[speaker.Name] = bubble;
                    _canvas.Children.Add(bubble.Root);
                }

                // rise quickly, fall a bit slower
                bubble.Level = speaker.Level > bubble.Level ? speaker.Level : bubble.Level * 0.75;

                for (int i = 0; i < bubble.Bars.Length; i++)
                {
                    // the middle bar is the tallest, like a little level meter
                    double reach = Math.Clamp(bubble.Level * 1.4 - i * 0.2, 0, 1) * (i == 1 ? 1 : 0.7);
                    bubble.Bars[i].Height = 4 + 10 * reach;
                    bubble.Bars[i].Fill = reach > 0.05 ? LevelBrush : QuietBrush;
                }

                bubble.Root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(bubble.Root, point.X - bubble.Root.DesiredSize.Width / 2);
                Canvas.SetTop(bubble.Root, point.Y - bubble.Root.DesiredSize.Height);
            }

            foreach (string name in _bubbles.Keys.Where(x => !seen.Contains(x)).ToList())
            {
                _canvas.Children.Remove(_bubbles[name].Root);
                _bubbles.Remove(name);
            }
        }

        /// <summary>
        /// Where a point in the world lands on the game window, or null if it's behind the camera or off screen
        /// </summary>
        public static Point? Project(Fluxer.CameraReader.CameraState camera, Vector3 world, double width, double height)
        {
            var offset = world - camera.Position;

            float x = Vector3.Dot(offset, camera.Right);
            float y = Vector3.Dot(offset, camera.Up);
            float depth = -Vector3.Dot(offset, camera.Back);

            if (depth < 0.5f || width <= 0 || height <= 0)
                return null;

            double tanHalf = Math.Tan(camera.FieldOfView / 2);
            double ndcX = x / (depth * tanHalf * (width / height));
            double ndcY = y / (depth * tanHalf);

            if (Math.Abs(ndcX) > 1.1 || Math.Abs(ndcY) > 1.1)
                return null;

            return new Point((ndcX + 1) / 2 * width, (1 - ndcY) / 2 * height);
        }
    }
}
