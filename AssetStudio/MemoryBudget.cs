using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace AssetStudio
{
    /// <summary>
    /// How much memory the system has left: when to move unpacked data to disk, and when to stop loading.
    /// </summary>
    public static class MemoryBudget
    {
        /// <summary>Unpacked data goes to disk once less than this share of the RAM is free.</summary>
        public static double SpillBelow = 0.5;

        private static long total;
        private static long available;
        private static long readTicks;
        private static long collectTicks;
        private static readonly object collectLock = new object();

        public static long Total { get { Refresh(); return total; } }
        public static long Available { get { Refresh(); return available; } }

        /// <summary>Free memory to keep, in bytes; 0: 1/12 of the RAM, 1 to 4 GB.</summary>
        public static long Reserve = 0;

        /// <summary>Below this much free memory loading stops.</summary>
        public static long Critical => Reserve > 0 ? Reserve : Math.Clamp(Total / 12, 1L << 30, 4L << 30);

        public static bool ShouldSpill()
        {
            Refresh();
            return total > 0 && available < total * SpillBelow;
        }

        public static bool IsCritical()
        {
            Refresh();
            if (total <= 0 || available >= Critical)
                return false;
            //garbage may still hold the memory: collect it and look again. Going on only when that
            //gives a good margin back, and not more than every 10 s: a program living on the line
            //would spend its time in full collections
            lock (collectLock)
            {
                //another thread may have just collected
                Refresh(true);
                if (available >= Critical)
                    return false;
                if (collectTicks != 0 && Environment.TickCount64 - collectTicks < 10000)
                    return true;
                GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                GC.WaitForPendingFinalizers();
                collectTicks = Environment.TickCount64;
                Refresh(true);
                return available < Critical * 3 / 2;
            }
        }

        public static string Format(long bytes) => $"{bytes / (double)(1L << 30):0.0} GB";

        private static void Refresh(bool force = false)
        {
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref readTicks);
            if (!force && last != 0 && now - last < 50)
                return;
            if (OperatingSystem.IsLinux() && ReadMemInfo(out var memTotal, out var memAvailable)
                || OperatingSystem.IsWindows() && ReadGlobalMemoryStatus(out memTotal, out memAvailable))
            {
                total = memTotal;
                available = memAvailable;
            }
            else
            {
                var info = GC.GetGCMemoryInfo();
                total = info.TotalAvailableMemoryBytes;
                available = info.TotalAvailableMemoryBytes - info.MemoryLoadBytes;
            }
            Interlocked.Exchange(ref readTicks, now);
        }

        private static bool ReadMemInfo(out long memTotal, out long memAvailable)
        {
            memTotal = memAvailable = -1;
            try
            {
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    if (line.StartsWith("MemTotal:"))
                        memTotal = ParseKiB(line);
                    else if (line.StartsWith("MemAvailable:"))
                        memAvailable = ParseKiB(line);
                    if (memTotal >= 0 && memAvailable >= 0)
                        return true;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return false;
        }

        //"MemAvailable:   43954420 kB"
        private static long ParseKiB(string line)
        {
            var value = line.AsSpan(line.IndexOf(':') + 1).Trim();
            var space = value.IndexOf(' ');
            return long.Parse(space < 0 ? value : value[..space]) * 1024;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        private static bool ReadGlobalMemoryStatus(out long memTotal, out long memAvailable)
        {
            var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            var ok = GlobalMemoryStatusEx(ref status);
            memTotal = (long)status.ullTotalPhys;
            memAvailable = (long)status.ullAvailPhys;
            return ok;
        }
    }
}
