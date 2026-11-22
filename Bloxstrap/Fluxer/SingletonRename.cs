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
			// the official launcher renames every client's singleton to the same FLUXER_ name, so a second client
			// still closes the first. naming it after the new client's process id gives each one its own
			string tag = (pi.dwProcessId & 0xFFFFF).ToString("x5");
			Dbg("patched=" + Patch(pi.hProcess, tag) + " name=F" + tag);
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

	// the named objects to rename, as their first 8 chars. wide names are matched on the first 6:
	// ROBLOX_singletonMutex / ROBLOX_singletonEvent, and RBXMAINWND-..., shared memory that holds the newest
	// client's window, which a starting client closes
	private static readonly string[] RenamedPrefixes = { "ROBLOX_s", "RBXMAINW" };

	// imports that take an object name, and which argument it is (1 based)
	private static readonly Dictionary<string, (int Argument, bool Wide)> NamedFunctions = new Dictionary<string, (int, bool)>
	{
		["CreateMutexA"] = (3, false),
		["CreateMutexW"] = (3, true),
		["OpenMutexA"] = (3, false),
		["OpenMutexW"] = (3, true),
		["CreateEventA"] = (4, false),
		["CreateEventW"] = (4, true),
		["OpenEventA"] = (3, false),
		["OpenEventW"] = (3, true),
		["CreateFileMappingA"] = (6, false),
		["CreateFileMappingW"] = (6, true),
		["OpenFileMappingA"] = (3, false),
		["OpenFileMappingW"] = (3, true)
	};

	private const int SlotSize = 512;

	private static uint[] Dwords(byte[] bytes)
	{
		uint[] result = new uint[bytes.Length / 4];
		for (int i = 0; i < result.Length; i++)
		{
			result[i] = BitConverter.ToUInt32(bytes, i * 4);
		}
		return result;
	}

	private static uint[] AnsiDwords(string text) => Dwords(Encoding.ASCII.GetBytes(text));

	private static uint[] WideDwords(string text) => Dwords(Encoding.Unicode.GetBytes(text));

	/// <summary>
	/// The hook for one import: if the name starts with one of RenamedPrefixes, the call gets a copy of it with
	/// "F" + tag in front instead, then it carries on into the real function
	/// </summary>
	private static byte[] BuildStub(int argument, bool wide, long original, long buffer, string tag)
	{
		List<byte> code = new List<byte>();
		List<int> toOriginal = new List<int>();
		List<int> toMatched = new List<int>();
		byte nameOffset = (byte)(argument * 4);
		byte nameOffsetPushed = (byte)(nameOffset + 12);

		void Jump(byte opcode, List<int> fixups)
		{
			code.Add(opcode);
			fixups.Add(code.Count);
			code.Add(0);
		}

		void Resolve(List<int> fixups)
		{
			foreach (int position in fixups)
			{
				int distance = code.Count - (position + 1);
				if (distance > 127)
				{
					throw new InvalidOperationException("stub jump too far");
				}
				code[position] = (byte)distance;
			}
			fixups.Clear();
		}

		code.AddRange(new byte[] { 0x8B, 0x44, 0x24, nameOffset }); // mov eax, [esp + name]
		code.AddRange(new byte[] { 0x85, 0xC0 });                   // test eax, eax
		Jump(0x74, toOriginal);                                     // jz original

		for (int i = 0; i < RenamedPrefixes.Length; i++)
		{
			bool last = i == RenamedPrefixes.Length - 1;
			List<int> toNext = new List<int>();
			uint[] prefix = wide ? WideDwords(RenamedPrefixes[i].Substring(0, 6)) : AnsiDwords(RenamedPrefixes[i]);

			for (int d = 0; d < prefix.Length; d++)
			{
				code.AddRange(d == 0 ? new byte[] { 0x81, 0x38 } : new byte[] { 0x81, 0x78, (byte)(d * 4) }); // cmp dword [eax + d*4], prefix
				code.AddRange(BitConverter.GetBytes(prefix[d]));
				Jump(0x75, last ? toOriginal : toNext);                                                    // jne next prefix
			}

			Jump(0xEB, toMatched); // jmp matched
			Resolve(toNext);
		}

		Resolve(toMatched);

		code.AddRange(new byte[] { 0x56, 0x57, 0x51 });                   // push esi; push edi; push ecx
		code.AddRange(new byte[] { 0x8B, 0x74, 0x24, nameOffsetPushed }); // mov esi, [esp + name]
		code.Add(0xBF);                                                   // mov edi, buffer
		code.AddRange(BitConverter.GetBytes((uint)buffer));
		code.Add(0xB9);                                                   // mov ecx, longest name
		code.AddRange(BitConverter.GetBytes(wide ? 62u : 63u));

		int loop = code.Count;
		code.AddRange(wide
			? new byte[] { 0x66, 0xAD, 0x66, 0xAB, 0x66, 0x85, 0xC0 } // lodsw; stosw; test ax, ax
			: new byte[] { 0xAC, 0xAA, 0x84, 0xC0 });                 // lodsb; stosb; test al, al
		List<int> toDone = new List<int>();
		Jump(0x74, toDone);                                           // jz done
		code.Add(0x49);                                               // dec ecx
		code.Add(0x75);                                               // jnz loop
		code.Add((byte)(loop - (code.Count + 1)));
		Resolve(toDone);

		uint[] replacement = wide ? WideDwords("F" + tag) : AnsiDwords("F" + tag + "_s");
		for (int d = 0; d < replacement.Length; d++)
		{
			code.AddRange(new byte[] { 0xC7, 0x05 });                     // mov dword [buffer + d*4], replacement
			code.AddRange(BitConverter.GetBytes((uint)(buffer + d * 4)));
			code.AddRange(BitConverter.GetBytes(replacement[d]));
		}

		code.AddRange(new byte[] { 0xC7, 0x44, 0x24, nameOffsetPushed }); // mov dword [esp + name], buffer
		code.AddRange(BitConverter.GetBytes((uint)buffer));
		code.AddRange(new byte[] { 0x59, 0x5F, 0x5E });                   // pop ecx; pop edi; pop esi

		Resolve(toOriginal);
		code.AddRange(new byte[] { 0xFF, 0x25 });                         // jmp [original]
		code.AddRange(BitConverter.GetBytes((uint)original));

		return code.ToArray();
	}

	private static bool Patch(IntPtr p, string tag)
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
						if (NamedFunctions.ContainsKey(text))
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
		// a slot per hook (stub, then the real function's address at +256, then the renamed name at +320),
		// followed by the code that installs them
		int allocationSize = (list.Count * SlotSize + 512 + 4095) / 4096 * 4096;
		IntPtr intPtr = VirtualAllocEx(p, IntPtr.Zero, new IntPtr(allocationSize), 12288u, 64u);
		if (intPtr == IntPtr.Zero)
		{
			return false;
		}
		long num11 = intPtr.ToInt64();
		List<long> list2 = new List<long>();
		List<long> list3 = new List<long>();
		for (int k = 0; k < list.Count; k++)
		{
			var (argument, wide) = NamedFunctions[list[k].Value];
			long slot = num11 + k * SlotSize;
			byte[] stub = BuildStub(argument, wide, slot + 256, slot + 320, tag);
			if (stub.Length > 256)
			{
				return false;
			}
			byte[] array2 = new byte[SlotSize];
			for (int i = 0; i < 256; i++)
			{
				array2[i] = 204;
			}
			stub.CopyTo(array2, 0);
			Write(p, slot, array2, keepWritable: true);
			list2.Add(slot);
			list3.Add(slot + 256);
		}
		long num16 = num11 + list.Count * SlotSize;
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
		FlushInstructionCache(p, intPtr, new IntPtr(allocationSize));
		FlushInstructionCache(p, new IntPtr(flag2 ? num18 : num21), new IntPtr(16));
		return true;
	}
}
