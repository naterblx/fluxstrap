#if DEV_BUILD
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Path = System.Windows.Shapes.Path;

using Bloxstrap.Integrations;

namespace Bloxstrap.UI.Elements.Overlay
{
    /// <summary>
    /// A mic button over the top left of the game window, like Roblox's voice chat one. Click it to mute.
    /// The mic fills up green with how loud you are while talking, and goes red with a slash when muted.
    /// It only shows while you can talk.
    /// </summary>
    public class VoiceMuteButton : OverlayWindow
    {
        // where it sits in the game window, in pixels at 100% scale, clear of the menu and chat buttons
        private const int OffsetX = 104;
        private const int OffsetY = 2;

        // the capsule of the mic runs from y 3 to 14 in the 24 x 24 icon
        private const double CapsuleTop = 3;
        private const double CapsuleHeight = 11;

        private static readonly Geometry CapsuleGeometry = Geometry.Parse("M12,3 A3,3 0 0 0 9,6 V11 A3,3 0 0 0 15,11 V6 A3,3 0 0 0 12,3 Z");
        private static readonly Geometry MicGeometry = Geometry.Parse("M12,3 A3,3 0 0 0 9,6 V11 A3,3 0 0 0 15,11 V6 A3,3 0 0 0 12,3 Z M18,11 A6,6 0 0 1 6,11 M12,17 V21 M9,21 H15");
        private static readonly Geometry SlashGeometry = Geometry.Parse("M5,4 L19,20");

        private static readonly Brush Idle = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
        private static readonly Brush Talking = new SolidColorBrush(Color.FromRgb(0x43, 0xD1, 0x7A));
        private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x5A));

        private readonly VoiceChat _voiceChat;

        private readonly Border _border;
        private readonly Path _mic;
        private readonly Path _fill;
        private readonly Path _slash;

        private double _shownLevel = 0;

        public VoiceMuteButton(VoiceChat voiceChat, int gamePid) : base(gamePid, false, TimeSpan.FromMilliseconds(50))
        {
            _voiceChat = voiceChat;

            Title = "FluxStrap voice chat";
            Width = 32;
            Height = 32;

            _fill = new Path { Data = CapsuleGeometry, Fill = Talking };
            _mic = new Path { Data = MicGeometry, StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            _slash = new Path { Data = SlashGeometry, StrokeThickness = 2.4, Stroke = MutedBrush, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };

            var icon = new Grid { Width = 24, Height = 24 };
            icon.Children.Add(_fill);
            icon.Children.Add(_mic);
            icon.Children.Add(_slash);

            _border = new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x14, 0x14, 0x14)),
                BorderThickness = new Thickness(1.5),
                Child = new Viewbox { Child = icon, Margin = new Thickness(5) },
                Cursor = System.Windows.Input.Cursors.Hand
            };

            _border.MouseLeftButtonUp += (_, _) =>
            {
                _voiceChat.Muted = !_voiceChat.Muted;
                App.Logger.WriteLine("VoiceMuteButton", _voiceChat.Muted ? "Muted" : "Unmuted");
                Refresh();
            };

            Content = _border;

            Refresh();
        }

        protected override bool HasContent => _voiceChat.CanTalk;

        protected override void Update(Int32Rect client, double scale)
        {
            Refresh();
            Place(client.X + (int)(OffsetX * scale), client.Y + (int)(OffsetY * scale));
        }

        private void Refresh()
        {
            bool muted = _voiceChat.Muted;
            bool talking = !muted && _voiceChat.Transmitting;

            // rise quickly, fall a bit slower, so it reads as a level rather than flickering
            double level = talking ? _voiceChat.OwnLevel : 0;
            _shownLevel = level > _shownLevel ? level : _shownLevel * 0.7;

            double height = CapsuleHeight * _shownLevel;
            _fill.Clip = new RectangleGeometry(new Rect(0, CapsuleTop + CapsuleHeight - height, 24, height));

            _mic.Stroke = muted ? MutedBrush : talking ? Talking : Idle;
            _slash.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
            _border.BorderBrush = talking ? Talking : Brushes.Transparent;
            ToolTip = $"{(muted ? "Unmute" : "Mute")}\nMic: {_voiceChat.MicrophoneName ?? VoiceChat.DefaultMicrophone}";
        }
    }
}
#endif
