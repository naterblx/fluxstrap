using System.Runtime.InteropServices;

namespace Bloxstrap.Fluxer
{
    /// <summary>
    /// The 2019-2021 clients only start VR if they find a Vive, Index or Mixed Reality headset plugged in over USB,
    /// so headsets that reach SteamVR some other way (Quest over Steam Link, Air Link or Virtual Desktop) never get used.
    /// This patches that USB check to always pass, after which the client connects to SteamVR as normal.
    /// </summary>
    public static class VrEnabler
    {
        // start of the USB check: push ebp / mov ebp, esp / sub esp, 138h / push esi / push 12h / push 0 / push 0,
        // then GUID_DEVINTERFACE_USB_DEVICE {A5DCBF10-6530-11D2-901F-00C04FB951ED} being put on the stack
        private static readonly byte[] Prologue = { 0x55, 0x8B, 0xEC, 0x81, 0xEC, 0x38, 0x01, 0x00, 0x00, 0x56, 0x6A, 0x12, 0x6A, 0x00, 0x6A, 0x00 };

        private static readonly byte[] UsbGuid = { 0x10, 0xBF, 0xDC, 0xA5 };

        // mov al, 1 / ret
        private static readonly byte[] Patch = { 0xB0, 0x01, 0xC3 };

        private const uint PROCESS_VM_OPERATION = 0x0008;
        private const uint PROCESS_VM_READ = 0x0010;
        private const uint PROCESS_VM_WRITE = 0x0020;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint PAGE_EXECUTE_READWRITE = 0x40;

        private static readonly List<Task> Pending = new();

        public static bool IsVrEnabled => App.GlobalSettings.GetPreset("User.VREnabled")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;

        /// <summary>
        /// Starts patching a freshly launched client in the background. It has to land before the client
        /// creates its D3D11 device (a few seconds in), which is when it looks for a headset.
        /// </summary>
        public static void Start(int pid)
        {
            var task = Task.Run(() => PatchProcess(pid));

            lock (Pending)
                Pending.Add(task);
        }

        public static void WaitForPatching()
        {
            Task[] tasks;

            lock (Pending)
                tasks = Pending.ToArray();

            Task.WaitAll(tasks, TimeSpan.FromSeconds(20));
        }

        private static void PatchProcess(int pid)
        {
            const string LOG_IDENT = "VrEnabler::PatchProcess";

            IntPtr handle = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_QUERY_INFORMATION, false, pid);

            if (handle == IntPtr.Zero)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Could not open PID {pid} ({Marshal.GetLastWin32Error()})");
                return;
            }

            try
            {
                var deadline = DateTime.Now.AddSeconds(15);

                while (DateTime.Now < deadline)
                {
                    using var process = Process.GetProcessById(pid);

                    if (process.HasExited)
                        return;

                    ProcessModule? module = null;

                    try
                    {
                        module = process.MainModule;
                    }
                    catch (Exception)
                    {
                        // modules aren't listed until the process has properly started
                    }

                    if (module is not null)
                    {
                        long address = FindCheck(handle, module.BaseAddress, module.ModuleMemorySize);

                        if (address != 0)
                        {
                            bool patched = WriteCode(handle, (IntPtr)address, Patch);
                            App.Logger.WriteLine(LOG_IDENT, patched ? $"Enabled VR headset detection for PID {pid}" : $"Could not patch PID {pid}");
                            return;
                        }
                    }

                    Thread.Sleep(50);
                }

                App.Logger.WriteLine(LOG_IDENT, $"Did not find the headset check in PID {pid}, this client might not support VR");
            }
            catch (ArgumentException)
            {
                // exited
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private static long FindCheck(IntPtr handle, IntPtr baseAddress, int size)
        {
            const int chunkSize = 0x10000;
            const int overlap = 0x40;

            var buffer = new byte[chunkSize + overlap];

            for (int offset = 0; offset < size; offset += chunkSize)
            {
                int length = Math.Min(chunkSize + overlap, size - offset);

                if (!ReadProcessMemory(handle, baseAddress + offset, buffer, length, out IntPtr read) || read == IntPtr.Zero)
                    continue;

                int found = Search(buffer, (int)read);

                if (found >= 0)
                    return (long)baseAddress + offset + found;
            }

            return 0;
        }

        private static int Search(byte[] data, int length)
        {
            var span = data.AsSpan(0, length);
            int start = 0;

            while (true)
            {
                int index = span[start..].IndexOf(Prologue);

                if (index < 0)
                    return -1;

                index += start;

                // the GUID comes a few bytes later, after the lea that points at it
                int after = index + Prologue.Length;
                int window = Math.Min(16, length - after);

                if (window > 0 && span.Slice(after, window).IndexOf(UsbGuid) >= 0)
                    return index;

                start = index + 1;
            }
        }

        private static bool WriteCode(IntPtr handle, IntPtr address, byte[] code)
        {
            if (!VirtualProtectEx(handle, address, (IntPtr)code.Length, PAGE_EXECUTE_READWRITE, out uint oldProtect))
                return false;

            bool written = WriteProcessMemory(handle, address, code, code.Length, out _);

            VirtualProtectEx(handle, address, (IntPtr)code.Length, oldProtect, out _);
            FlushInstructionCache(handle, address, (IntPtr)code.Length);

            return written;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern bool ReadProcessMemory(IntPtr handle, IntPtr address, byte[] buffer, int size, out IntPtr read);

        [DllImport("kernel32.dll")]
        private static extern bool WriteProcessMemory(IntPtr handle, IntPtr address, byte[] buffer, int size, out IntPtr written);

        [DllImport("kernel32.dll")]
        private static extern bool VirtualProtectEx(IntPtr handle, IntPtr address, IntPtr size, uint protect, out uint oldProtect);

        [DllImport("kernel32.dll")]
        private static extern bool FlushInstructionCache(IntPtr handle, IntPtr address, IntPtr size);
    }
}
