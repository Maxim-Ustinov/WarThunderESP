namespace WarThunderESP;

public sealed partial class GameMemoryReader
{
    public void StartBackgroundScanning()
    {
        if (_scannerThread != null)
            return;

        _scannerThread = new Thread(ScannerLoop)
        {
            IsBackground = true,
            Name = "WT object scanner"
        };

        _scannerThread.Start();
    }

    public long[] GetObjectAddresses()
    {
        lock (_sync)
            return (long[])_objects.Clone();
    }

    private byte ReadByte(long address)
    {
        byte[] buf = new byte[1];

        if (ReadProcessMemory(_handle, (IntPtr)address, buf, 1, out int read) && read == 1)
            return buf[0];

        return byte.MaxValue;
    }

    private int ReadInt32(long address)
    {
        byte[] buf = new byte[4];

        if (ReadProcessMemory(_handle, (IntPtr)address, buf, 4, out int read) && read == 4)
            return BitConverter.ToInt32(buf, 0);

        return int.MinValue;
    }
    public void Dispose()
    {
        _running = false;
        _scannerThread?.Join(2000);

        if (_handle != IntPtr.Zero)
        {
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }

    private Process? FindProcess()
    {
        foreach (var p in Process.GetProcessesByName(ProcessName))
            return p;

        return null;
    }

    private void ScannerLoop()
    {
        var sw = new Stopwatch();

        while (_running)
        {
            try
            {
                sw.Restart();
                List<long> found = ScanForVtables(_vtableAddr, _aircraftVtableAddr);
                sw.Stop();

                lock (_sync)
                {
                    _objects = found.ToArray();
                    _lastScan = DateTime.Now;
                    _scanMs = sw.ElapsedMilliseconds;
                }

                OnObjectsUpdated?.Invoke();
            }
            catch
            {
                // Не убиваем overlay из-за временной ошибки чтения памяти.
            }

            Thread.Sleep(ScanPauseMs);
        }
    }

    private long ResolvePointer(long baseAddress, int[] offsets)
    {
        long address = ReadInt64(baseAddress);
        if (!IsReasonablePointer(address))
            return 0;

        for (int i = 0; i < offsets.Length - 1; i++)
        {
            address = ReadInt64(address + offsets[i]);
            if (!IsReasonablePointer(address))
                return 0;
        }

        return address + offsets[offsets.Length - 1];
    }

    private float ReadFloat(long address)
    {
        byte[] buf = new byte[4];

        if (ReadProcessMemory(_handle, (IntPtr)address, buf, 4, out int read) && read == 4)
            return BitConverter.ToSingle(buf, 0);

        return float.NaN;
    }

    private long ReadInt64(long address)
    {
        byte[] buf = new byte[8];

        if (ReadProcessMemory(_handle, (IntPtr)address, buf, 8, out int read) && read == 8)
            return BitConverter.ToInt64(buf, 0);

        return 0;
    }

    private static float Distance3D(in Vec3 a, in Vec3 b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;

        return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static bool IsFinite(in Vec3 v)
    {
        return IsFinite(v.X) && IsFinite(v.Y) && IsFinite(v.Z);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static float Clamp(float value, float min, float max)
    {
        if (value < min)
            return min;

        if (value > max)
            return max;

        return value;
    }

    private static bool IsValidWorldPosition(in Vec3 v)
    {
        if (!IsFinite(v))
            return false;

        if (Math.Abs(v.X) < 0.001f && Math.Abs(v.Y) < 0.001f && Math.Abs(v.Z) < 0.001f)
            return false;

        if (Math.Abs(v.X) > 100000f || Math.Abs(v.Y) > 100000f || Math.Abs(v.Z) > 100000f)
            return false;

        return true;
    }

    private static bool IsReasonablePointer(long address)
    {
        return address > 0x10000 && address < 0x00007FFFFFFFFFFF;
    }

    private List<long> ScanForVtables(params long[] vtableAddrs)
    {
        var found = new List<long>();
        long address = 0;
        const long maxUserAddress = 0x00007FFFFFFFFFFF;
        int mbiSize = Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();
        byte[] buffer = new byte[ScanBufferSize];
        var nameBuf = new StringBuilder(260);

        while (address < maxUserAddress)
        {
            if (VirtualQueryEx(_handle, (IntPtr)address, out MEMORY_BASIC_INFORMATION mbi, mbiSize) == 0)
                break;

            long regionBase = mbi.BaseAddress.ToInt64();
            long regionSize = mbi.RegionSize.ToInt64();

            if (regionSize <= 0)
                break;

            bool committed = mbi.State == MEM_COMMIT;
            bool heapType = mbi.Type == MEM_PRIVATE || mbi.Type == MEM_MAPPED;
            bool readable = (mbi.Protect & PAGE_READABLE) != 0 && (mbi.Protect & PAGE_GUARD) == 0;
            bool scanThis = committed && heapType && readable;

            if (scanThis && SkipFileBackedMapped && mbi.Type == MEM_MAPPED)
            {
                uint len = GetMappedFileNameW(_handle, mbi.BaseAddress, nameBuf, (uint)nameBuf.Capacity);
                if (len > 0)
                    scanThis = false;
            }

            if (scanThis)
            {
                long offset = 0;

                while (offset < regionSize)
                {
                    int toRead = (int)Math.Min(ScanBufferSize, regionSize - offset);

                    if (ReadProcessMemory(_handle, (IntPtr)(regionBase + offset), buffer, toRead, out int read) && read >= 8)
                    {
                        int qwords = read / 8;
                        ReadOnlySpan<long> longs = MemoryMarshal.Cast<byte, long>(buffer.AsSpan(0, qwords * 8));

                        for (int i = 0; i < longs.Length; i++)
                        {
                            long value = longs[i];

                            for (int j = 0; j < vtableAddrs.Length; j++)
                            {
                                if (value == vtableAddrs[j])
                                {
                                    found.Add(regionBase + offset + (long)i * 8);
                                    break;
                                }
                            }
                        }
                    }

                    offset += toRead;
                }
            }

            long next = regionBase + regionSize;
            if (next <= address)
                break;

            address = next;
        }

        return found;
    }

    private List<long> ScanForVtable(long vtableAddr)
    {
        var found = new List<long>();
        long address = 0;
        const long maxUserAddress = 0x00007FFFFFFFFFFF;
        int mbiSize = Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();
        byte[] buffer = new byte[ScanBufferSize];
        var nameBuf = new StringBuilder(260);

        while (address < maxUserAddress)
        {
            if (VirtualQueryEx(_handle, (IntPtr)address, out MEMORY_BASIC_INFORMATION mbi, mbiSize) == 0)
                break;

            long regionBase = mbi.BaseAddress.ToInt64();
            long regionSize = mbi.RegionSize.ToInt64();

            if (regionSize <= 0)
                break;

            bool committed = mbi.State == MEM_COMMIT;
            bool heapType = mbi.Type == MEM_PRIVATE || mbi.Type == MEM_MAPPED;
            bool readable = (mbi.Protect & PAGE_READABLE) != 0 && (mbi.Protect & PAGE_GUARD) == 0;
            bool scanThis = committed && heapType && readable;

            if (scanThis && SkipFileBackedMapped && mbi.Type == MEM_MAPPED)
            {
                uint len = GetMappedFileNameW(_handle, mbi.BaseAddress, nameBuf, (uint)nameBuf.Capacity);
                if (len > 0)
                    scanThis = false;
            }

            if (scanThis)
            {
                long offset = 0;

                while (offset < regionSize)
                {
                    int toRead = (int)Math.Min(ScanBufferSize, regionSize - offset);

                    if (ReadProcessMemory(_handle, (IntPtr)(regionBase + offset), buffer, toRead, out int read) && read >= 8)
                    {
                        int qwords = read / 8;
                        ReadOnlySpan<long> longs = MemoryMarshal.Cast<byte, long>(buffer.AsSpan(0, qwords * 8));

                        for (int i = 0; i < longs.Length; i++)
                        {
                            if (longs[i] == vtableAddr)
                                found.Add(regionBase + offset + (long)i * 8);
                        }
                    }

                    offset += toRead;
                }
            }

            long next = regionBase + regionSize;
            if (next <= address)
                break;

            address = next;
        }

        return found;
    }

    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_VM_READ = 0x0010;

    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_PRIVATE = 0x20000;
    private const uint MEM_MAPPED = 0x40000;

    private const uint PAGE_GUARD = 0x100;
    private const uint PAGE_READABLE = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr handle, IntPtr address, byte[] buffer, int size, out int read);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int VirtualQueryEx(IntPtr handle, IntPtr address, out MEMORY_BASIC_INFORMATION mbi, int length);

    [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetMappedFileNameW(IntPtr handle, IntPtr address, [Out] StringBuilder name, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public uint __alignment1;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint __alignment2;
    }
}

