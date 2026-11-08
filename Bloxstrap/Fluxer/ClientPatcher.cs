#nullable disable

// Ported from the official Fluxer launcher so FluxStrap launches clients exactly the same way

using System.Runtime.InteropServices;

namespace Bloxstrap.Fluxer;

public static class ClientPatcher
{
	private struct STARTUPINFO
	{
		public int cb;

		public string lpReserved;

		public string lpDesktop;

		public string lpTitle;

		public int dwX;

		public int dwY;

		public int dwXSize;

		public int dwYSize;

		public int dwXCountChars;

		public int dwYCountChars;

		public int dwFillAttribute;

		public int dwFlags;

		public short wShowWindow;

		public short cbReserved2;

		public IntPtr lpReserved2;

		public IntPtr hStdInput;

		public IntPtr hStdOutput;

		public IntPtr hStdError;
	}

	private struct PROCESS_INFORMATION
	{
		public IntPtr hProcess;

		public IntPtr hThread;

		public int dwProcessId;

		public int dwThreadId;
	}

	private struct MEMORY_BASIC_INFORMATION
	{
		public IntPtr BaseAddress;

		public IntPtr AllocationBase;

		public uint AllocationProtect;

		public IntPtr RegionSize;

		public uint State;

		public uint Protect;

		public uint Type;
	}

	public class Result
	{
		public int Patched;

		public List<string> Samples = new List<string>();
	}

	private const uint CREATE_SUSPENDED = 4u;

	private const uint MEM_COMMIT = 4096u;

	private const uint MEM_IMAGE = 16777216u;

	private const uint MEM_PRIVATE = 131072u;

	private const uint PAGE_NOACCESS = 1u;

	private const uint PAGE_GUARD = 256u;

	private const uint PAGE_READWRITE = 4u;

	private const uint PAGE_EXECUTE_READWRITE = 64u;

	private const uint PAGE_WRITECOPY = 8u;

	private const uint PAGE_EXECUTE_WRITECOPY = 128u;

	private const uint PROCESS_ALL = 2035711u;

	public static string[][] BrandPairs;

	private static readonly List<Thread> PatchThreads = new List<Thread>();

	/// <summary>
	/// Blocks until every client started by this process has finished being patched, or has exited.
	/// FluxStrap has to stay alive for this, otherwise the patching stops early.
	/// </summary>
	public static void WaitForPatching()
	{
		Thread[] threads;
		lock (PatchThreads)
		{
			threads = PatchThreads.ToArray();
		}
		foreach (Thread thread in threads)
		{
			thread.Join();
		}
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool CreateProcessW(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

	[DllImport("kernel32.dll")]
	private static extern uint ResumeThread(IntPtr h);

	[DllImport("kernel32.dll")]
	private static extern bool CloseHandle(IntPtr h);

	[DllImport("kernel32.dll")]
	private static extern IntPtr VirtualQueryEx(IntPtr h, IntPtr addr, out MEMORY_BASIC_INFORMATION mbi, IntPtr len);

	[DllImport("kernel32.dll")]
	private static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);

	[DllImport("kernel32.dll")]
	private static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr written);

	[DllImport("kernel32.dll")]
	private static extern bool VirtualProtectEx(IntPtr h, IntPtr addr, IntPtr size, uint newProt, out uint oldProt);

	[DllImport("kernel32.dll")]
	private static extern bool GetExitCodeProcess(IntPtr h, out uint code);

	public static int Launch(string exe, string args, string workingDir, string from, string to, int keepPatchingSeconds, Result result)
	{
		return Launch(exe, args, workingDir, new string[1] { from }, to, keepPatchingSeconds, result, hidden: false);
	}

	public static int Launch(string exe, string args, string workingDir, string[] froms, string to, int keepPatchingSeconds, Result result, bool hidden)
	{
		STARTUPINFO si = default(STARTUPINFO);
		si.cb = Marshal.SizeOf(si);
		if (hidden)
		{
			si.dwFlags = 1;
			si.wShowWindow = 0;
		}
		StringBuilder cmd = new StringBuilder("\"" + exe + "\" " + args);
		if (!CreateProcessW(exe, cmd, IntPtr.Zero, IntPtr.Zero, inherit: false, (uint)(4 | (hidden ? 134217728 : 0)), IntPtr.Zero, workingDir, ref si, out var pi))
		{
			return 0;
		}
		try
		{
			string[] array = froms;
			foreach (string text in array)
			{
				Patch(pi.hProcess, text, to, result);
			}
			PatchBrand(pi.hProcess, result);
			ResumeThread(pi.hThread);
		}
		finally
		{
			CloseHandle(pi.hThread);
		}
		IntPtr h = pi.hProcess;
		int dwProcessId = pi.dwProcessId;
		if (keepPatchingSeconds > 0)
		{
			Thread thread = new Thread((ThreadStart)delegate
			{
				try
				{
					DateTime dateTime = DateTime.UtcNow.AddSeconds(keepPatchingSeconds);
					while (DateTime.UtcNow < dateTime)
					{
						Thread.Sleep(400);
						if (GetExitCodeProcess(h, out var code) && code != 259)
						{
							break;
						}
						string[] array2 = froms;
						foreach (string text2 in array2)
						{
							Patch(h, text2, to, result);
						}
						PatchBrand(h, result);
					}
				}
				catch
				{
				}
				finally
				{
					CloseHandle(h);
				}
			});
			thread.IsBackground = true;
			thread.Start();
			lock (PatchThreads)
			{
				PatchThreads.Add(thread);
			}
			return dwProcessId;
		}
		CloseHandle(h);
		return dwProcessId;
	}

	private static void PatchBrand(IntPtr process, Result result)
	{
		if (BrandPairs == null)
		{
			return;
		}
		long num = 0L;
		MEMORY_BASIC_INFORMATION mbi;
		while (num < 140737488289792L && !(VirtualQueryEx(process, (IntPtr)num, out mbi, (IntPtr)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION))) == IntPtr.Zero))
		{
			long num2 = (long)mbi.RegionSize;
			long num3 = (long)mbi.BaseAddress + num2;
			bool flag = (mbi.Protect & 0x101) == 0 && mbi.Protect != 0;
			bool writable = (mbi.Protect & 0xCC) != 0;
			if (mbi.State == 4096 && flag && (mbi.Type == 16777216 || mbi.Type == 131072) && num2 > 0 && num2 < 629145600)
			{
				byte[] array = new byte[num2];
				if (ReadProcessMemory(process, mbi.BaseAddress, array, (IntPtr)num2, out var read) && (long)read > 0)
				{
					int num4 = (int)read;
					string[][] brandPairs = BrandPairs;
					foreach (string[] array2 in brandPairs)
					{
						bool flag2 = array2.Length > 2 && array2[2] == "A";
						Encoding obj = (flag2 ? Encoding.ASCII : Encoding.Unicode);
						byte[] bytes = obj.GetBytes(array2[0]);
						byte[] bytes2 = obj.GetBytes(array2[1]);
						int num5 = (flag2 ? 1 : 2);
						if (!flag2 && bytes.Length != bytes2.Length)
						{
							continue;
						}
						int start = 0;
						while (true)
						{
							int num6 = IndexOf(array, bytes, start, num4);
							if (num6 < 0)
							{
								break;
							}
							start = num6 + bytes.Length;
							if (num6 % num5 != 0)
							{
								continue;
							}
							bool flag3 = num6 == 0 || (flag2 ? (array[num6 - 1] == 0) : (num6 >= 2 && array[num6 - 1] == 0 && array[num6 - 2] == 0));
							bool flag4 = ((!flag2) ? (num6 + bytes.Length + 1 < num4 && array[num6 + bytes.Length] == 0 && array[num6 + bytes.Length + 1] == 0) : (num6 + bytes.Length < num4 && array[num6 + bytes.Length] == 0));
							if (!flag3 || !flag4)
							{
								continue;
							}
							if (flag2 && bytes2.Length > bytes.Length)
							{
								bool flag5 = num6 + bytes2.Length + 1 <= num4;
								int num7 = bytes.Length;
								while (flag5 && num7 <= bytes2.Length)
								{
									if (array[num6 + num7] != 0)
									{
										flag5 = false;
									}
									num7++;
								}
								if (!flag5)
								{
									continue;
								}
							}
							if (WriteBytes(process, (long)mbi.BaseAddress + num6, bytes2, writable))
							{
								Array.Copy(bytes2, 0, array, num6, bytes2.Length);
								result.Patched++;
								if (result.Samples.Count < 60)
								{
									result.Samples.Add("B " + array2[1]);
								}
							}
						}
					}
				}
			}
			if (num3 > num)
			{
				num = num3;
				continue;
			}
			break;
		}
	}

	public static void Patch(IntPtr process, string from, string to, Result result)
	{
		byte[] bytes = Encoding.ASCII.GetBytes(from);
		byte[] bytes2 = Encoding.ASCII.GetBytes(to);
		byte[] bytes3 = Encoding.Unicode.GetBytes(from);
		byte[] bytes4 = Encoding.Unicode.GetBytes(to);
		long num = 0L;
		MEMORY_BASIC_INFORMATION mbi;
		while (num < 140737488289792L && !(VirtualQueryEx(process, (IntPtr)num, out mbi, (IntPtr)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION))) == IntPtr.Zero))
		{
			long num2 = (long)mbi.RegionSize;
			long num3 = (long)mbi.BaseAddress + num2;
			bool writable = (mbi.Protect & 0xCC) != 0;
			bool flag = (mbi.Protect & 0x101) == 0 && mbi.Protect != 0;
			if (mbi.State == 4096 && flag && (mbi.Type == 16777216 || mbi.Type == 131072) && num2 > 0 && num2 < 629145600)
			{
				PatchRegion(process, (long)mbi.BaseAddress, (int)num2, writable, bytes, bytes2, wide: false, result);
			}
			if (mbi.State == 4096 && flag && (mbi.Type == 16777216 || mbi.Type == 131072) && num2 > 0 && num2 < 629145600)
			{
				PatchRegion(process, (long)mbi.BaseAddress, (int)num2, writable, bytes3, bytes4, wide: true, result);
			}
			if (num3 > num)
			{
				num = num3;
				continue;
			}
			break;
		}
	}

	private static int IndexOf(byte[] data, byte[] needle, int start, int end)
	{
		byte b = needle[0];
		for (int i = start; i <= end - needle.Length; i++)
		{
			if (data[i] == b)
			{
				int j;
				for (j = 1; j < needle.Length && data[i + j] == needle[j]; j++)
				{
				}
				if (j == needle.Length)
				{
					return i;
				}
			}
		}
		return -1;
	}

	private static void PatchRegion(IntPtr process, long baseAddr, int size, bool writable, byte[] from, byte[] to, bool wide, Result result)
	{
		byte[] array = new byte[size];
		if (!ReadProcessMemory(process, (IntPtr)baseAddr, array, (IntPtr)size, out var read) || (long)read == 0L)
		{
			return;
		}
		int num = (int)read;
		int num2 = ((!wide) ? 1 : 2);
		int start = 0;
		while (true)
		{
			int num3 = IndexOf(array, from, start, num);
			if (num3 < 0)
			{
				break;
			}
			start = num3 + from.Length;
			if (wide && num3 % 2 != 0)
			{
				continue;
			}
			int num4 = num3;
			int i = num3 + from.Length;
			int num5 = 600 * num2;
			while (num4 - num2 >= 0 && num3 - num4 < num5 && Printable(array, num4 - num2, wide))
			{
				num4 -= num2;
			}
			for (; i + num2 <= num && i - num3 < num5 && Printable(array, i, wide); i += num2)
			{
			}
			if (i + num2 > num)
			{
				continue;
			}
			byte[] array2 = new byte[i - num4];
			Array.Copy(array, num4, array2, 0, array2.Length);
			byte[] array3 = Replace(array2, from, to, num2);
			if (array3.Length == array2.Length)
			{
				continue;
			}
			byte[] array4 = new byte[array2.Length];
			Array.Copy(array3, array4, array3.Length);
			if (WriteBytes(process, baseAddr + num4, array4, writable))
			{
				Array.Copy(array4, 0, array, num4, array4.Length);
				result.Patched++;
				if (result.Samples.Count < 60)
				{
					result.Samples.Add((wide ? "W " : "A ") + Encoding.ASCII.GetString(wide ? Encoding.Convert(Encoding.Unicode, Encoding.ASCII, array3) : array3));
				}
			}
		}
	}

	private static bool Printable(byte[] b, int i, bool wide)
	{
		if (wide)
		{
			if (i + 1 < b.Length && b[i + 1] == 0 && b[i] >= 32)
			{
				return b[i] < 127;
			}
			return false;
		}
		if (b[i] >= 32)
		{
			return b[i] < 127;
		}
		return false;
	}

	private static byte[] Replace(byte[] data, byte[] from, byte[] to, int step)
	{
		List<byte> list = new List<byte>(data.Length);
		int num = 0;
		while (num < data.Length)
		{
			bool flag = num + from.Length <= data.Length;
			int num2 = 0;
			while (flag && num2 < from.Length)
			{
				if (data[num + num2] != from[num2])
				{
					flag = false;
				}
				num2++;
			}
			if (flag)
			{
				list.AddRange(to);
				num += from.Length;
			}
			else
			{
				list.Add(data[num++]);
			}
		}
		return list.ToArray();
	}

	private static bool WriteBytes(IntPtr process, long addr, byte[] data, bool writable)
	{
		uint oldProt = 0u;
		if (!VirtualProtectEx(process, (IntPtr)addr, (IntPtr)data.Length, 64u, out oldProt))
		{
			return false;
		}
		IntPtr written;
		bool result = WriteProcessMemory(process, (IntPtr)addr, data, (IntPtr)data.Length, out written);
		VirtualProtectEx(process, (IntPtr)addr, (IntPtr)data.Length, oldProt, out var _);
		return result;
	}
}
