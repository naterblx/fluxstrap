using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Bloxstrap.UI.Elements.Overlay
{
    /// <summary>
    /// A borderless window drawn over the game. It follows the game window around, never takes focus from it,
    /// and only shows while the game (or one of these) is the window in front.
    /// </summary>
    public abstract class OverlayWindow : Window
    {
        private static readonly HashSet<IntPtr> Overlays = new();

        private readonly int _gamePid;
        private readonly bool _clickThrough;
        private readonly DispatcherTimer _timer;

        private IntPtr _gameWindow = IntPtr.Zero;

        protected IntPtr Handle { get; private set; } = IntPtr.Zero;

        /// <param name="clickThrough">whether clicks go straight through to the game</param>
        protected OverlayWindow(int gamePid, bool clickThrough, TimeSpan interval)
        {
            _gamePid = gamePid;
            _clickThrough = clickThrough;

            Title = "FluxStrap overlay";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;

            SourceInitialized += (_, _) =>
            {
                Handle = new WindowInteropHelper(this).Handle;

                lock (Overlays)
                    Overlays.Add(Handle);

                // never take focus from the game, and stay out of alt-tab
                int style = GetWindowLong(Handle, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;

                if (_clickThrough)
                    style |= WS_EX_TRANSPARENT | WS_EX_LAYERED;

                SetWindowLong(Handle, GWL_EXSTYLE, style);
            };

            _timer = new DispatcherTimer(interval, DispatcherPriority.Normal, (_, _) => Tick(), Dispatcher);
            _timer.Start();
        }

        /// <summary>
        /// Whether there's anything to show right now
        /// </summary>
        protected abstract bool HasContent { get; }

        /// <summary>
        /// Places and redraws the overlay, given the game's client area in screen pixels and the display scale
        /// </summary>
        protected abstract void Update(Int32Rect client, double scale);

        protected void Place(int x, int y, int width = 0, int height = 0)
        {
            uint flags = SWP_NOACTIVATE | (width == 0 ? SWP_NOSIZE : 0);
            SetWindowPos(Handle, HWND_TOPMOST, x, y, width, height, flags);
        }

        private void Tick()
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
            bool overlayInFront;

            lock (Overlays)
                overlayInFront = Overlays.Contains(foreground);

            bool show = HasContent
                && _gameWindow != IntPtr.Zero
                && !IsIconic(_gameWindow)
                && (foreground == _gameWindow || overlayInFront);

            if (!show)
            {
                if (IsVisible)
                    Hide();

                return;
            }

            if (!IsVisible)
                Show();

            var corner = new POINT();
            ClientToScreen(_gameWindow, ref corner);
            GetClientRect(_gameWindow, out var rect);

            Update(new Int32Rect(corner.X, corner.Y, rect.Right - rect.Left, rect.Bottom - rect.Top), VisualTreeHelper.GetDpi(this).DpiScaleX);
        }

        protected override void OnClosed(EventArgs e)
        {
            _timer.Stop();

            lock (Overlays)
                Overlays.Remove(Handle);

            base.OnClosed(e);
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOACTIVATE = 0x0010;
        private static readonly IntPtr HWND_TOPMOST = new(-1);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
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
        private static extern bool GetClientRect(IntPtr window, out RECT rect);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr window);
    }
}
