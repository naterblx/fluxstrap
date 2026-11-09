using System.Numerics;
using System.Runtime.InteropServices;

namespace Bloxstrap.Fluxer
{
    /// <summary>
    /// Reads the camera out of a 2019-2021 client's memory, which is what proximity voice chat goes by.
    /// Workspace is found by its name, then its Camera child, whose CFrame (where you're looking from) and
    /// Focus (around your character) are read. Offsets move between client years, so they're worked out here.
    /// </summary>
    public class CameraReader : IDisposable
    {
        /// <param name="Position">where the camera is</param>
        /// <param name="Right">the camera's right, up and backward directions (it looks along -Back)</param>
        /// <param name="FieldOfView">vertical, in radians</param>
        /// <param name="Focus">around your character</param>
        public readonly record struct CameraState(Vector3 Position, Vector3 Right, Vector3 Up, Vector3 Back, float FieldOfView, Vector3 Focus);

        private static readonly float DefaultFieldOfView = 70 * MathF.PI / 180;

        // where FieldOfView sits for each client's CFrame offset, if it isn't at its default when we find the camera
        private static readonly Dictionary<int, int> KnownFieldOfViewOffsets = new()
        {
            { 0x64, 0x1B0 }, // 2019
            { 0x60, 0x1B0 }, // 2020
            { 0x6C, 0x1BC }  // 2021
        };

        // instances keep a pointer to their name at 0x28 (0x2c on 2019); children and parent follow it
        private static readonly int[] NameOffsets = { 0x28, 0x2C };

        private const int ClassDescriptorOffset = 0x0C;
        private const int ClassNameOffset = 0x04;

        private const uint PROCESS_VM_READ = 0x0010;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint MEM_COMMIT = 0x1000;
        private const uint MEM_PRIVATE = 0x20000;
        private const uint PAGE_READWRITE = 0x04;

        private readonly IntPtr _handle;
        private readonly int _pid;
        private uint _moduleStart;
        private uint _moduleEnd;

        private uint _camera;
        private int _cframeOffset;
        private int _fieldOfViewOffset;
        private DateTime _nextSearch = DateTime.MinValue;

        public bool Supported { get; }

        public CameraReader(int pid)
        {
            _pid = pid;
            _handle = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, pid);

            // only the 32-bit clients have the layout this knows about
            Supported = _handle != IntPtr.Zero && IsWow64Process(_handle, out bool wow64) && wow64;
        }

        // the module list can't always be read while the client is still starting, so this gets retried
        private bool FindModule()
        {
            try
            {
                using var process = Process.GetProcessById(_pid);

                if (process.MainModule is not ProcessModule module)
                    return false;

                _moduleStart = (uint)module.BaseAddress;
                _moduleEnd = _moduleStart + (uint)module.ModuleMemorySize;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Forgets the camera, for when a new game loads (teleports make a new Workspace)
        /// </summary>
        public void Reset()
        {
            _camera = 0;
            _nextSearch = DateTime.MinValue;
        }

        public bool TryRead(out CameraState state)
        {
            state = default;

            if (!Supported)
                return false;

            if (_camera == 0)
            {
                // searching reads all of the client's memory, so don't do it too often
                if (DateTime.Now < _nextSearch)
                    return false;

                _nextSearch = DateTime.Now.AddSeconds(10);

                if (_moduleStart == 0 && !FindModule())
                {
                    _nextSearch = DateTime.Now.AddSeconds(1);
                    return false;
                }

                if (!FindCamera())
                    return false;
            }

            var cframes = new byte[0x60];

            if (!Read(_camera + (uint)_cframeOffset, cframes) || !IsRotation(cframes, 0) || !IsRotation(cframes, 0x30))
            {
                _camera = 0;
                return false;
            }

            var floats = MemoryMarshal.Cast<byte, float>(cframes);

            float fieldOfView = DefaultFieldOfView;
            var fovBytes = new byte[4];

            if (_fieldOfViewOffset != 0 && Read(_camera + (uint)_fieldOfViewOffset, fovBytes))
            {
                float value = BitConverter.ToSingle(fovBytes, 0);

                if (value > 0.0175f && value < 2.1f)
                    fieldOfView = value;
            }

            // CFrame: row major rotation, then position. the columns are right, up and back
            state = new CameraState(
                new Vector3(floats[9], floats[10], floats[11]),
                new Vector3(floats[0], floats[3], floats[6]),
                new Vector3(floats[1], floats[4], floats[7]),
                new Vector3(floats[2], floats[5], floats[8]),
                fieldOfView,
                new Vector3(floats[21], floats[22], floats[23]));

            return true;
        }

        private bool FindCamera()
        {
            const string LOG_IDENT = "CameraReader::FindCamera";

            var regions = GetRegions();

            // the interned name string: "Workspace" in a small string buffer, size 9, capacity 15
            var pattern = new byte[24];
            Encoding.ASCII.GetBytes("Workspace").CopyTo(pattern, 0);
            BitConverter.GetBytes(9).CopyTo(pattern, 16);
            BitConverter.GetBytes(15).CopyTo(pattern, 20);

            var strings = new List<uint>();

            ScanRegions(regions, (address, span) =>
            {

                for (int index = span.IndexOf(pattern.AsSpan(0, 10)); index >= 0 && index + 24 <= span.Length; )
                {
                    if (span.Slice(index + 16, 8).SequenceEqual(pattern.AsSpan(16, 8)))
                        strings.Add(address + (uint)index);

                    int next = span[(index + 1)..].IndexOf(pattern.AsSpan(0, 10));
                    index = next < 0 ? -1 : index + 1 + next;
                }
            });

            if (strings.Count == 0)
                return false;

            // instances point at that string from their name field
            var references = new List<uint>();

            ScanRegions(regions, (address, span) =>
            {
                var values = MemoryMarshal.Cast<byte, uint>(span[..(span.Length & ~3)]);

                foreach (uint target in strings)
                {
                    int start = 0;

                    while (start < values.Length)
                    {
                        int found = values[start..].IndexOf(target);

                        if (found < 0)
                            break;

                        references.Add(address + (uint)(start + found) * 4);
                        start += found + 1;
                    }
                }
            });

            foreach (uint reference in references)
            {
                foreach (int nameOffset in NameOffsets)
                {
                    uint workspace = reference - (uint)nameOffset;

                    if (!IsInModule(ReadUInt(workspace)) || GetClassName(workspace) != "Workspace")
                        continue;

                    if (GetClassName(ReadUInt(workspace + (uint)nameOffset + 0x0C)) != "DataModel")
                        continue;

                    foreach (uint child in GetChildren(workspace + (uint)nameOffset + 4))
                    {
                        if (GetClassName(child) != "Camera" || FindCFrameOffset(child) is not int cframeOffset)
                            continue;

                        _camera = child;
                        _cframeOffset = cframeOffset;
                        _fieldOfViewOffset = FindFieldOfViewOffset(child, cframeOffset);

                        App.Logger.WriteLine(LOG_IDENT, $"Found the camera (name at 0x{nameOffset:X}, CFrame at 0x{cframeOffset:X})");
                        return true;
                    }
                }
            }

            return false;
        }

        // FieldOfView is kept in radians a bit after the CFrame, and is almost always still 70 when we find the camera
        private int FindFieldOfViewOffset(uint camera, int cframeOffset)
        {
            var data = new byte[0x240];

            if (Read(camera, data))
            {
                for (int offset = 0x180; offset + 4 <= data.Length; offset += 4)
                {
                    if (Math.Abs(BitConverter.ToSingle(data, offset) - DefaultFieldOfView) < 1e-5f)
                        return offset;
                }
            }

            return KnownFieldOfViewOffsets.GetValueOrDefault(cframeOffset);
        }

        // the CFrame is the first rotation in the camera, with Focus right after it
        private int? FindCFrameOffset(uint camera)
        {
            var data = new byte[0x140];

            if (!Read(camera, data))
                return null;

            for (int offset = 0x40; offset + 0x60 <= data.Length; offset += 4)
            {
                if (IsRotation(data, offset) && IsRotation(data, offset + 0x30))
                    return offset;
            }

            return null;
        }

        private static bool IsRotation(byte[] data, int offset)
        {
            var m = MemoryMarshal.Cast<byte, float>(data.AsSpan(offset, 48));

            for (int i = 0; i < 12; i++)
            {
                if (!float.IsFinite(m[i]) || Math.Abs(m[i]) > 1e6)
                    return false;
            }

            for (int row = 0; row < 3; row++)
            {
                var a = new Vector3(m[row * 3], m[row * 3 + 1], m[row * 3 + 2]);

                if (Math.Abs(a.LengthSquared() - 1) > 1e-3)
                    return false;

                for (int other = row + 1; other < 3; other++)
                {
                    var b = new Vector3(m[other * 3], m[other * 3 + 1], m[other * 3 + 2]);

                    if (Math.Abs(Vector3.Dot(a, b)) > 1e-3)
                        return false;
                }
            }

            return true;
        }

        private List<uint> GetChildren(uint vectorPointer)
        {
            var children = new List<uint>();
            uint vector = ReadUInt(vectorPointer);

            if (vector == 0)
                return children;

            uint begin = ReadUInt(vector), end = ReadUInt(vector + 4);

            // a vector of shared pointers, 8 bytes each
            if (begin == 0 || end < begin || end - begin > 8 * 20000 || (end - begin) % 8 != 0)
                return children;

            var data = new byte[end - begin];

            if (!Read(begin, data))
                return children;

            for (int i = 0; i < data.Length; i += 8)
                children.Add(BitConverter.ToUInt32(data, i));

            return children;
        }

        private string? GetClassName(uint instance)
        {
            if (instance == 0)
                return null;

            uint descriptor = ReadUInt(instance + ClassDescriptorOffset);
            return descriptor == 0 ? null : ReadString(ReadUInt(descriptor + ClassNameOffset));
        }

        // MSVC std::string: 16 byte buffer (or a pointer when it doesn't fit), then size and capacity
        private string? ReadString(uint address)
        {
            var data = new byte[24];

            if (address == 0 || !Read(address, data))
                return null;

            int size = BitConverter.ToInt32(data, 16), capacity = BitConverter.ToInt32(data, 20);

            if (size < 0 || size > 100 || capacity < size)
                return null;

            if (capacity < 16)
                return Encoding.ASCII.GetString(data, 0, size);

            var text = new byte[size];
            return Read(BitConverter.ToUInt32(data, 0), text) ? Encoding.ASCII.GetString(text) : null;
        }

        private bool IsInModule(uint address) => address >= _moduleStart && address < _moduleEnd;

        private List<(uint Address, uint Size)> GetRegions()
        {
            var regions = new List<(uint, uint)>();
            long address = 0;

            while (address < 0x7FFF0000 && VirtualQueryEx(_handle, (IntPtr)address, out var info, Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) != 0)
            {
                long size = (long)info.RegionSize;

                if (info.State == MEM_COMMIT && info.Type == MEM_PRIVATE && info.Protect == PAGE_READWRITE && size < 64 * 1024 * 1024)
                    regions.Add(((uint)(long)info.BaseAddress, (uint)size));

                address = (long)info.BaseAddress + size;
            }

            return regions;
        }

        private delegate void RegionScanner(uint address, ReadOnlySpan<byte> data);

        private void ScanRegions(List<(uint Address, uint Size)> regions, RegionScanner scan)
        {
            if (regions.Count == 0)
                return;

            // one buffer for every region, so a search doesn't allocate the client's whole memory
            var buffer = new byte[regions.Max(x => x.Size)];

            foreach (var (address, size) in regions)
            {
                if (ReadProcessMemory(_handle, (IntPtr)address, buffer, (int)size, out IntPtr read) && (long)read == size)
                    scan(address, buffer.AsSpan(0, (int)size));
            }
        }

        private uint ReadUInt(uint address)
        {
            var data = new byte[4];
            return address != 0 && Read(address, data) ? BitConverter.ToUInt32(data, 0) : 0;
        }

        private bool Read(uint address, byte[] buffer) =>
            ReadProcessMemory(_handle, (IntPtr)address, buffer, buffer.Length, out IntPtr read) && (int)read == buffer.Length;

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
                CloseHandle(_handle);

            GC.SuppressFinalize(this);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_BASIC_INFORMATION
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

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern bool IsWow64Process(IntPtr handle, out bool wow64);

        [DllImport("kernel32.dll")]
        private static extern bool ReadProcessMemory(IntPtr handle, IntPtr address, byte[] buffer, int size, out IntPtr read);

        [DllImport("kernel32.dll")]
        private static extern int VirtualQueryEx(IntPtr handle, IntPtr address, out MEMORY_BASIC_INFORMATION info, int length);
    }
}
