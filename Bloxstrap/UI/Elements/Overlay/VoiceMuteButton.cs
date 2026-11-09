#if DEV_BUILD
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Path = System.Windows.Shapes.Path;
using System.Windows.Threading;

using Bloxstrap.Integrations;

namespace Bloxstrap.UI.Elements.Overlay
{
    /// <summary>
    /// A mic button over the top left of the game window, like Roblox's voice chat one. Click it to mute.
    /// It turns green while you're talking and red with a slash when muted, and only shows while you can talk
    /// and the game is the window in front.
    /// </summary>
    public class VoiceMuteButton : Window
    {
        // where it sits in the game window, in pixels at 100% scale, clear of the menu and chat buttons
        private const int OffsetX = 104;
        private const int OffsetY = 2;

        private static readonly Geometry MicGeometry = Geometry.Parse("M12,3 A3,3 0 0 0 9,6 V11 A3,3 0 0 0 15,11 V6 A3,3 0 0 0 12,3 Z M18,11 A6,6 0 0 1 6,11 M12,17 V21 M9,21 H15");
        private static readonly Geometry SlashGeometry = Geometry.Parse("M5,4 L19,20");

        private static readonly Brush Idle = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
        private static readonly Brush Talking = new SolidColorBrush(Color.FromRgb(0x43, 0xD1, 0x7A));
        private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x5A));

        private readonly VoiceChat _voiceChat;
        private readonly int _gamePid;
        private readonly DispatcherTimer _timer;

        private readonly Border _border;
        private readonly Path _mic;
        private readonly Path _slash;

        private IntPtr _gameWindow = IntPtr.Zero;
        private IntPtr _handle = IntPtr.Zero;

        public VoiceMuteButton(VoiceChat voiceChat, int gamePid)
        {
            _voiceChat = voiceChat;
            _gamePid = gamePid;

            Title = "FluxStrap voice chat";
            Width = 32;
            Height = 32;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;

            _mic = new Path { Data = MicGeometry, StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            _slash = new Path { Data = SlashGeometry, StrokeThickness = 2.4, Stroke = MutedBrush, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };

            var icon = new Grid { Width = 24, Height = 24 };
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

            SourceInitialized += (_, _) =>
            {
                _handle = new WindowInteropHelper(this).Handle;

                // never take focus from the game, and stay out of alt-tab
                int style = GetWindowLong(_handle, GWL_EXSTYLE);
                SetWindowLong(_handle, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            };

            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Normal, (_, _) => Update(), Dispatcher);
            _timer.Start();

            Refresh();
        }

        private void Refresh()
        {
            bool muted = _voiceChat.Muted;

            _mic.Stroke = muted ? MutedBrush : _voiceChat.Transmitting ? Talking : Idle;
            _slash.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
            _border.BorderBrush = !muted && _voiceChat.Transmitting ? Talking : Brushes.Transparent;
            ToolTip = muted ? "Unmute" : "Mute";
        }

        private void Update()
        {
            if (_gameWindow == IntPtr.Zero || !IsWindow(_gameWindow))
            {
                try
                {
                    using var process = Process.GetProcessById(_gamePid);
                    _gameWindow = process.MainWindowHandle;
                }
                catch (Exception)
                {
                    _gameWindow = IntPtr.Zero;
                }
            }

            IntPtr foreground = GetForegroundWindow();

            bool show = _voiceChat.CanTalk
                && _gameWindow != IntPtr.Zero
                && !IsIconic(_gameWindow)
                && (foreground == _gameWindow || (foreground == _handle && _handle != IntPtr.Zero));

            if (!show)
            {
                if (IsVisible)
                    Hide();

                return;
            }

            if (!IsVisible)
                Show();

            Refresh();

            var corner = new POINT();
            ClientToScreen(_gameWindow, ref corner);

            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            SetWindowPos(_handle, HWND_TOPMOST, corner.X + (int)(OffsetX * scale), corner.Y + (int)(OffsetY * scale), 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        }

        protected override void OnClosed(EventArgs e)
        {
            _timer.Stop();
            base.OnClosed(e);
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOACTIVATE = 0x0010;
        private static readonly IntPtr HWND_TOPMOST = new(-1);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr window, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr window, int index, int value);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr window, ref POINT point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr window);
    }
}
#endif
