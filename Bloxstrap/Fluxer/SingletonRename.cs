#nullable disable

// Ported from the official Fluxer launcher so FluxStrap launches clients exactly the same way

using System.Runtime.InteropServices;

namespace Bloxstrap.Fluxer;

internal static class SingletonRename
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

	private const uint CREATE_SUSPENDED = 4u;

	private const uint MEM_COMMIT_RESERVE = 12288u;

	private const uint PAGE_EXECUTE_READWRITE = 64u;

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool CreateProcessW(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

	[DllImport("kernel32.dll")]
	private static extern uint ResumeThread(IntPtr t);

	[DllImport("kernel32.dll")]
	private static extern bool ReadProcessMemory(IntPtr p, IntPtr addr, byte[] buf, int size, out IntPtr read);

	[DllImport("kernel32.dll")]
	private static extern bool WriteProcessMemory(IntPtr p, IntPtr addr, byte[] buf, int size, out IntPtr written);

	[DllImport("kernel32.dll")]
	private static extern IntPtr VirtualAllocEx(IntPtr p, IntPtr addr, IntPtr size, uint type, uint protect);

	[DllImport("kernel32.dll")]
	private static extern bool VirtualProtectEx(IntPtr p, IntPtr addr, IntPtr size, uint newProtect, out uint oldProtect);

	[DllImport("kernel32.dll")]
	private static extern bool FlushInstructionCache(IntPtr p, IntPtr addr, IntPtr size);

	[DllImport("kernel32.dll")]
	private static extern bool CloseHandle(IntPtr h);

	[DllImport("ntdll.dll")]
	private static extern int NtQueryInformationProcess(IntPtr p, int cls, out IntPtr info, int size, out int ret);

	private static void Dbg(string s) => App.Logger.WriteLine("SingletonRename", s);

	public static Process Start(string exe, string args, string workDir)
	{
		STARTUPINFO si = new STARTUPINFO
		{
			cb = Marshal.SizeOf(typeof(STARTUPINFO))
		};
		StringBuilder cmd = new StringBuilder("\"" + exe + "\" " + args);
		if (!CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, inherit: false, 4u, IntPtr.Zero, workDir, ref si, out var pi))
		{
			throw new InvalidOperationException("CreateProcess failed: " + Marshal.GetLastWin32Error());
		}
		try
		{
			Dbg("patched=" + Patch(pi.hProcess));
		}
		catch (Exception ex)
		{
			Dbg("error: " + ex.Message);
		}
		ResumeThread(pi.hThread);
		CloseHandle(pi.hThread);
		Process processById = Process.GetProcessById(pi.dwProcessId);
		CloseHandle(pi.hProcess);
		return processById;
	}

	private static byte[] Read(IntPtr p, long addr, int size)
	{
		byte[] array = new byte[size];
		if (!ReadProcessMemory(p, new IntPtr(addr), array, size, out var read) || read.ToInt64() != size)
		{
			throw new InvalidOperationException("read " + addr.ToString("x"));
		}
		return array;
	}

	private static void Write(IntPtr p, long addr, byte[] data, bool keepWritable)
	{
		VirtualProtectEx(p, new IntPtr(addr), new IntPtr(data.Length), 64u, out var oldProtect);
		if (!WriteProcessMemory(p, new IntPtr(addr), data, data.Length, out var _))
		{
			throw new InvalidOperationException("write " + addr.ToString("x"));
		}
		if (!keepWritable)
		{
			VirtualProtectEx(p, new IntPtr(addr), new IntPtr(data.Length), oldProtect, out oldProtect);
		}
	}

	private static string CString(IntPtr p, long addr)
	{
		StringBuilder stringBuilder = new StringBuilder();
		for (int i = 0; i < 80; i++)
		{
			byte b = Read(p, addr + i, 1)[0];
			if (b == 0)
			{
				break;
			}
			stringBuilder.Append((char)b);
		}
		return stringBuilder.ToString();
	}

	private static bool Patch(IntPtr p)
	{
		if (NtQueryInformationProcess(p, 26, out var info, IntPtr.Size, out var _) != 0 || info == IntPtr.Zero)
		{
			return false;
		}
		long num = BitConverter.ToUInt32(Read(p, info.ToInt64() + 8, 4), 0);
		int num2 = BitConverter.ToInt32(Read(p, num, 64), 60);
		byte[] value = Read(p, num + num2, 256);
		if (BitConverter.ToUInt16(value, 24) != 267)
		{
			return false;
		}
		uint num3 = BitConverter.ToUInt32(value, 40);
		uint num4 = BitConverter.ToUInt32(value, 128);
		if (num4 == 0 || num3 == 0)
		{
			return false;
		}
		Dictionary<string, int[]> dictionary = new Dictionary<string, int[]>();
		dictionary["CreateMutexA"] = new int[2] { 3, 0 };
		dictionary["CreateMutexW"] = new int[2] { 3, 1 };
		dictionary["OpenMutexA"] = new int[2] { 3, 0 };
		dictionary["OpenMutexW"] = new int[2] { 3, 1 };
		dictionary["CreateEventA"] = new int[2] { 4, 0 };
		dictionary["CreateEventW"] = new int[2] { 4, 1 };
		dictionary["OpenEventA"] = new int[2] { 3, 0 };
		dictionary["OpenEventW"] = new int[2] { 3, 1 };
		List<KeyValuePair<long, string>> list = new List<KeyValuePair<long, string>>();
		long num5 = num + num4;
		while (true)
		{
			byte[] value2 = Read(p, num5, 20);
			uint num6 = BitConverter.ToUInt32(value2, 0);
			uint num7 = BitConverter.ToUInt32(value2, 12);
			uint num8 = BitConverter.ToUInt32(value2, 16);
			if (num7 == 0)
			{
				break;
			}
			if (num6 == 0)
			{
				num6 = num8;
			}
			if (CString(p, num + num7).StartsWith("KERNEL32", StringComparison.OrdinalIgnoreCase))
			{
				int num9 = 0;
				while (true)
				{
					uint num10 = BitConverter.ToUInt32(Read(p, num + num6 + num9 * 4, 4), 0);
					if (num10 == 0)
					{
						break;
					}
					if ((num10 & 0x80000000u) == 0)
					{
						string text = CString(p, num + num10 + 2);
						if (dictionary.ContainsKey(text))
						{
							list.Add(new KeyValuePair<long, string>(num + num8 + num9 * 4, text));
						}
					}
					num9++;
				}
			}
			num5 += 20;
		}
		Dbg("base=" + num.ToString("x") + " slots=" + list.Count);
		if (list.Count == 0)
		{
			return false;
		}
		IntPtr intPtr = VirtualAllocEx(p, IntPtr.Zero, new IntPtr(4096), 12288u, 64u);
		if (intPtr == IntPtr.Zero)
		{
			return false;
		}
		long num11 = intPtr.ToInt64();
		if (list.Count * 512 > 3584)
		{
			return false;
		}
		long num12 = num11;
		List<long> list2 = new List<long>();
		List<long> list3 = new List<long>();
		foreach (KeyValuePair<long, string> item in list)
		{
			int[] array = dictionary[item.Value];
			bool flag = array[1] == 1;
			long num13 = num12;
			long num14 = num13 + 256;
			long num15 = num13 + 320;
			byte b = (byte)(array[0] * 4);
			byte b2 = (byte)(array[0] * 4 + 12);
			List<byte> list4 = new List<byte>();
			List<int> list5 = new List<int>();
			byte[] obj = new byte[4] { 139, 68, 36, 0 };
			obj[3] = b;
			list4.AddRange(obj);
			list4.AddRange(new byte[2] { 133, 192 });
			list5.Add(list4.Count);
			list4.AddRange(new byte[2] { 116, 0 });
			if (!flag)
			{
				list4.AddRange(new byte[2] { 129, 56 });
				list4.AddRange(BitConverter.GetBytes(1279414098u));
				list5.Add(list4.Count);
				list4.AddRange(new byte[2] { 117, 0 });
				list4.AddRange(new byte[3] { 129, 120, 4 });
				list4.AddRange(BitConverter.GetBytes(1935628367u));
				list5.Add(list4.Count);
				list4.AddRange(new byte[2] { 117, 0 });
			}
			else
			{
				list4.AddRange(new byte[2] { 129, 56 });
				list4.AddRange(BitConverter.GetBytes(5177426u));
				list5.Add(list4.Count);
				list4.AddRange(new byte[2] { 117, 0 });
				list4.AddRange(new byte[3] { 129, 120, 4 });
				list4.AddRange(BitConverter.GetBytes(4980802u));
				list5.Add(list4.Count);
				list4.AddRange(new byte[2] { 117, 0 });
				list4.AddRange(new byte[3] { 129, 120, 8 });
				list4.AddRange(BitConverter.GetBytes(5767247u));
				list5.Add(list4.Count);
				list4.AddRange(new byte[2] { 117, 0 });
			}
			list4.AddRange(new byte[3] { 86, 87, 81 });
			byte[] obj2 = new byte[4] { 139, 116, 36, 0 };
			obj2[3] = b2;
			list4.AddRange(obj2);
			list4.Add(191);
			list4.AddRange(BitConverter.GetBytes((uint)num15));
			list4.Add(185);
			list4.AddRange(BitConverter.GetBytes(flag ? 62u : 63u));
			int count = list4.Count;
			if (!flag)
			{
				list4.AddRange(new byte[4] { 172, 170, 132, 192 });
			}
			else
			{
				list4.AddRange(new byte[7] { 102, 173, 102, 171, 102, 133, 192 });
			}
			int count2 = list4.Count;
			list4.AddRange(new byte[2] { 116, 0 });
			list4.Add(73);
			list4.AddRange(new byte[2]
			{
				117,
				(byte)(count - (list4.Count + 2))
			});
			list4[count2 + 1] = (byte)(list4.Count - (count2 + 2));
			uint value3 = (flag ? 4980806u : 1481985094u);
			uint value4 = (flag ? 5767253u : 1935626821u);
			list4.AddRange(new byte[2] { 199, 5 });
			list4.AddRange(BitConverter.GetBytes((uint)num15));
			list4.AddRange(BitConverter.GetBytes(value3));
			list4.AddRange(new byte[2] { 199, 5 });
			list4.AddRange(BitConverter.GetBytes((uint)((int)num15 + 4)));
			list4.AddRange(BitConverter.GetBytes(value4));
			if (flag)
			{
				list4.AddRange(new byte[2] { 199, 5 });
				list4.AddRange(BitConverter.GetBytes((uint)((int)num15 + 8)));
				list4.AddRange(BitConverter.GetBytes(5374021u));
			}
			byte[] obj3 = new byte[4] { 199, 68, 36, 0 };
			obj3[3] = b2;
			list4.AddRange(obj3);
			list4.AddRange(BitConverter.GetBytes((uint)num15));
			list4.AddRange(new byte[3] { 89, 95, 94 });
			foreach (int item2 in list5)
			{
				list4[item2 + 1] = (byte)(list4.Count - (item2 + 2));
			}
			list4.AddRange(new byte[2] { 255, 37 });
			list4.AddRange(BitConverter.GetBytes((uint)num14));
			if (list4.Count > 256)
			{
				return false;
			}
			byte[] array2 = new byte[512];
			for (int i = 0; i < 256; i++)
			{
				array2[i] = 204;
			}
			list4.CopyTo(array2);
			Write(p, num13, array2, keepWritable: true);
			list2.Add(num13);
			list3.Add(num14);
			num12 += 512;
		}
		long num16 = num11 + 3584;
		long num17 = 0L;
		long num18 = 0L;
		long num19 = 0L;
		uint num20 = BitConverter.ToUInt32(value, 192);
		if (num20 != 0)
		{
			num17 = num + num20;
			num18 = BitConverter.ToUInt32(Read(p, num17 + 12, 4), 0);
			if (num18 != 0L)
			{
				num19 = BitConverter.ToUInt32(Read(p, num18, 4), 0);
			}
		}
		bool flag2 = num18 != 0L && num19 != 0;
		long num21 = num + num3;
		byte[] array3 = (flag2 ? new byte[5] : Read(p, num21, 5));
		List<byte> list6 = new List<byte>();
		list6.AddRange(new byte[2] { 156, 96 });
		for (int j = 0; j < list.Count; j++)
		{
			list6.Add(161);
			list6.AddRange(BitConverter.GetBytes((uint)list[j].Key));
			list6.Add(163);
			list6.AddRange(BitConverter.GetBytes((uint)list3[j]));
			list6.AddRange(new byte[2] { 199, 5 });
			list6.AddRange(BitConverter.GetBytes((uint)list[j].Key));
			list6.AddRange(BitConverter.GetBytes((uint)list2[j]));
		}
		long num22;
		if (flag2)
		{
			list6.AddRange(new byte[2] { 199, 5 });
			list6.AddRange(BitConverter.GetBytes((uint)num18));
			list6.AddRange(BitConverter.GetBytes((uint)num19));
			num22 = num19;
		}
		else
		{
			list6.AddRange(new byte[2] { 199, 5 });
			list6.AddRange(BitConverter.GetBytes((uint)num21));
			list6.AddRange(new byte[4]
			{
				array3[0],
				array3[1],
				array3[2],
				array3[3]
			});
			list6.AddRange(new byte[2] { 198, 5 });
			list6.AddRange(BitConverter.GetBytes((uint)((int)num21 + 4)));
			list6.Add(array3[4]);
			num22 = num21;
		}
		list6.AddRange(new byte[2] { 97, 157 });
		list6.Add(233);
		list6.AddRange(BitConverter.GetBytes((int)(num22 - (num16 + list6.Count + 4))));
		if (list6.Count > 512)
		{
			return false;
		}
		Write(p, num16, list6.ToArray(), keepWritable: true);
		foreach (KeyValuePair<long, string> item3 in list)
		{
			VirtualProtectEx(p, new IntPtr(item3.Key), new IntPtr(4), 64u, out var _);
		}
		if (flag2)
		{
			Write(p, num18, BitConverter.GetBytes((uint)num16), keepWritable: true);
		}
		else
		{
			List<byte> list7 = new List<byte> { 233 };
			list7.AddRange(BitConverter.GetBytes((int)(num16 - (num21 + 5))));
			Write(p, num21, list7.ToArray(), keepWritable: true);
		}
		Dbg("hook via " + (flag2 ? ("tls callback " + num19.ToString("x")) : ("entry point " + num21.ToString("x"))) + " inst=" + num16.ToString("x"));
		FlushInstructionCache(p, intPtr, new IntPtr(4096));
		FlushInstructionCache(p, new IntPtr(flag2 ? num18 : num21), new IntPtr(16));
		return true;
	}
}
