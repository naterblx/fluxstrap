#nullable disable

// Ported from the official Fluxer launcher so FluxStrap launches clients exactly the same way

using System.Drawing;
using System.Runtime.InteropServices;

namespace Bloxstrap.Fluxer;

public static class ClientIcon
{
	private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

	private const uint WM_SETICON = 128u;

	private const int GCLP_HICON = -14;

	private const int GCLP_HICONSM = -34;

	[DllImport("user32.dll")]
	private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll", EntryPoint = "SetClassLongPtrW")]
	private static extern IntPtr SetClassLongPtr(IntPtr hWnd, int index, IntPtr value);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern bool SetWindowText(IntPtr hWnd, string text);

	public static void Apply(int pid)
	{
		if (pid <= 0)
		{
			return;
		}
		Thread thread = new Thread((ThreadStart)delegate
		{
			try
			{
				Icon big = Icon.ExtractAssociatedIcon(Paths.Process);
				if (big != null)
				{
					Icon small = new Icon(big, 16, 16);
					HashSet<IntPtr> done = new HashSet<IntPtr>();
					DateTime dateTime = DateTime.UtcNow.AddMinutes(15.0);
					while (DateTime.UtcNow < dateTime)
					{
						try
						{
							if (Process.GetProcessById(pid).HasExited)
							{
								break;
							}
						}
						catch
						{
							break;
						}
						EnumWindows(delegate(IntPtr h, IntPtr l)
						{
							GetWindowThreadProcessId(h, out var pid2);
							if (pid2 == (uint)pid && IsWindowVisible(h))
							{
								StringBuilder stringBuilder = new StringBuilder(64);
								if (GetWindowText(h, stringBuilder, 64) > 0 && stringBuilder.ToString() == "Roblox")
								{
									SetWindowText(h, "Fluxer");
								}
							}
							if (pid2 == (uint)pid && IsWindowVisible(h) && done.Add(h))
							{
								SendMessage(h, 128u, (IntPtr)1, big.Handle);
								SendMessage(h, 128u, (IntPtr)0, small.Handle);
								SetClassLongPtr(h, -14, big.Handle);
								SetClassLongPtr(h, -34, small.Handle);
							}
							return true;
						}, IntPtr.Zero);
						Thread.Sleep(1000);
					}
				}
			}
			catch
			{
			}
		});
		thread.IsBackground = true;
		thread.Start();
	}
}
