#nullable disable

// Ported from the official Fluxer launcher (FpsAttach): finds the client's task scheduler and
// rewrites its frame delay, which is how Fluxer clients get unlocked FPS

using System.Runtime.InteropServices;

namespace Bloxstrap.Fluxer;

public class FpsUnlocker
{
	private struct MBI
	{
		public IntPtr BaseAddress;

		public IntPtr AllocationBase;

		public uint AllocationProtect;

		public ushort PartitionId;

		public IntPtr RegionSize;

		public uint State;

		public uint Protect;

		public uint Type;
	}

	/// <summary>
	/// The caps offered in the settings, same as the official launcher (0 = unlimited).
	/// The frame delay is recognised by being 1/cap for one of these, so keep them in sync with it.
	/// </summary>
	public static readonly int[] Caps = new int[8] { 60, 75, 120, 144, 165, 240, 360, 0 };

	private const uint PROCESS_VM_OPERATION = 8u;

	private const uint PROCESS_VM_READ = 16u;

	private const uint PROCESS_VM_WRITE = 32u;

	private const uint PROCESS_QUERY_INFORMATION = 1024u;

	private const uint MEM_COMMIT = 4096u;

	private const uint PAGE_NOACCESS = 1u;

	private const uint PAGE_GUARD = 256u;

	public readonly int Pid;

	private IntPtr _h;

	private bool _is64;

	private long _tsPtr;

	private long _delayAddr;

	public string Status = "starting";

	private int _tries;

	public bool Ready => _delayAddr != 0;

	public bool Dead
	{
		get
		{
			try
			{
				return Process.GetProcessById(Pid).HasExited;
			}
			catch
			{
				return true;
			}
		}
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(IntPtr h);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr written);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr VirtualQueryEx(IntPtr h, IntPtr addr, out MBI mbi, IntPtr len);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool IsWow64Process(IntPtr h, out bool wow64);

	public FpsUnlocker(Process p)
	{
		Pid = p.Id;
		_h = OpenProcess(1080u, inherit: false, p.Id);
		if (_h == IntPtr.Zero)
		{
			Status = "cannot open process (err " + Marshal.GetLastWin32Error() + ")";
			return;
		}
		IsWow64Process(_h, out var wow);
		_is64 = Environment.Is64BitOperatingSystem && !wow;
	}

	public void Close()
	{
		if (_h != IntPtr.Zero)
		{
			CloseHandle(_h);
			_h = IntPtr.Zero;
		}
	}

	private byte[] Read(long addr, int size)
	{
		byte[] array = new byte[size];
		if (!ReadProcessMemory(_h, (IntPtr)addr, array, (IntPtr)size, out var read) || (int)read != size)
		{
			return null;
		}
		return array;
	}

	private static long Find(byte[] data, byte[] pat, string mask, int from = 0)
	{
		for (int i = from; i <= data.Length - pat.Length; i++)
		{
			bool flag = true;
			for (int j = 0; j < pat.Length; j++)
			{
				if (mask[j] == 'x' && data[i + j] != pat[j])
				{
					flag = false;
					break;
				}
			}
			if (flag)
			{
				return i;
			}
		}
		return -1L;
	}

	private long ScanProcess(byte[] pat, string mask, long start, long end)
	{
		long num = start;
		MBI mbi;
		while (num < end && !(VirtualQueryEx(_h, (IntPtr)num, out mbi, (IntPtr)Marshal.SizeOf(typeof(MBI))) == IntPtr.Zero))
		{
			long num2 = (long)mbi.BaseAddress;
			long num3 = (long)mbi.RegionSize;
			if (mbi.State == 4096 && (mbi.Protect & 1) == 0 && (mbi.Protect & 0x100) == 0 && num3 > 0)
			{
				long num4 = Math.Max(num2, num);
				long num5 = Math.Min(num2 + num3, end);
				for (long num6 = num4; num6 < num5; num6 += 1048576 - pat.Length)
				{
					int size = (int)Math.Min(1048576L, num5 - num6);
					byte[] array = Read(num6, size);
					if (array == null)
					{
						break;
					}
					long num7 = Find(array, pat, mask);
					if (num7 >= 0)
					{
						return num6 + num7;
					}
				}
			}
			num = num2 + num3;
		}
		return 0L;
	}

	private static byte[] Sig(params int[] bytes)
	{
		return bytes.Select((int x) => (byte)x).ToArray();
	}

	private bool FindTaskScheduler(long baseAddr, long size)
	{
		long end = baseAddr + size;
		if (_is64)
		{
			long num = ScanProcess(Sig(64, 83, 72, 131, 236, 32, 15, 182, 217, 232, 0, 0, 0, 0, 134, 88, 4, 72, 131, 196, 32, 91, 195), "xxxxxxxxxx????xxxxxxxxx", baseAddr, end);
			if (num == 0L)
			{
				return false;
			}
			byte[] array = Read(num + 10, 4);
			if (array == null)
			{
				return false;
			}
			long num2 = num + 14 + BitConverter.ToInt32(array, 0);
			byte[] array2 = Read(num2, 256);
			if (array2 == null)
			{
				return false;
			}
			long num3 = Find(array2, Sig(72, 139, 5, 0, 0, 0, 0, 72, 131, 196, 56), "xxx????xxxx");
			if (num3 < 0)
			{
				return false;
			}
			_tsPtr = num2 + num3 + 7 + BitConverter.ToInt32(array2, (int)num3 + 3);
			return true;
		}
		long num4 = ScanProcess(Sig(85, 139, 236, 232, 0, 0, 0, 0, 138, 77, 8, 131, 192, 4, 134, 8, 93, 195), "xxxx????xxxxxxxxxx", baseAddr, end);
		if (num4 == 0L)
		{
			return false;
		}
		byte[] array3 = Read(num4 + 4, 4);
		if (array3 == null)
		{
			return false;
		}
		long addr = num4 + 8 + BitConverter.ToInt32(array3, 0);
		byte[] array4 = Read(addr, 256);
		if (array4 == null)
		{
			return false;
		}
		long num5 = Find(array4, Sig(161, 0, 0, 0, 0, 139, 77, 244), "x????xxx");
		if (num5 < 0)
		{
			return false;
		}
		_tsPtr = BitConverter.ToUInt32(array4, (int)num5 + 1);
		return true;
	}

	public void Tick()
	{
		if (_h == IntPtr.Zero || Ready || _tries > 40)
		{
			return;
		}
		_tries++;
		try
		{
			using Process process = Process.GetProcessById(Pid);
			ProcessModule mainModule = process.MainModule;
			long baseAddr = (long)mainModule.BaseAddress;
			long size = mainModule.ModuleMemorySize;
			if (_tsPtr == 0L && !FindTaskScheduler(baseAddr, size))
			{
				Status = "scheduler signature not found yet";
				return;
			}
			byte[] array = Read(_tsPtr, _is64 ? 8 : 4);
			if (array == null)
			{
				Status = "scheduler pointer unreadable";
				return;
			}
			long num = (_is64 ? BitConverter.ToInt64(array, 0) : BitConverter.ToUInt32(array, 0));
			if (num == 0L)
			{
				Status = "scheduler not created yet";
				return;
			}
			byte[] array2 = Read(num + 256, 512);
			if (array2 == null)
			{
				Status = "scheduler unreadable";
				return;
			}
			for (int i = 0; i <= array2.Length - 8; i += 4)
			{
				if (IsKnownDelay(BitConverter.ToDouble(array2, i)))
				{
					_delayAddr = num + 256 + i;
					Status = "attached";
					return;
				}
			}
			Status = "frame delay not found yet";
		}
		catch (Exception ex)
		{
			Status = ex.GetType().Name + ": " + ex.Message;
		}
	}

	private static bool IsKnownDelay(double d)
	{
		if (Math.Abs(d - 1.0 / 60.0) < double.Epsilon)
		{
			return true;
		}
		if (Math.Abs(d - 0.0001) < double.Epsilon)
		{
			return true;
		}
		int[] caps = Caps;
		foreach (int num in caps)
		{
			if (num > 0 && Math.Abs(d - 1.0 / (double)num) < double.Epsilon)
			{
				return true;
			}
		}
		return false;
	}

	public bool SetCap(int cap)
	{
		if (!Ready)
		{
			return false;
		}
		double value = ((cap <= 0) ? 0.0001 : (1.0 / (double)cap));
		IntPtr written;
		return WriteProcessMemory(_h, (IntPtr)_delayAddr, BitConverter.GetBytes(value), (IntPtr)8, out written);
	}

	public double ReadDelay()
	{
		byte[] array = (Ready ? Read(_delayAddr, 8) : null);
		if (array != null)
		{
			return BitConverter.ToDouble(array, 0);
		}
		return -1.0;
	}
}
