using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace AssetStudio
{
    /// <summary>
    /// A stream over a piece of a memory mapped file on disk: its pages belong to the file, not to the process,
    /// the kernel writes them out and frees them when the memory runs short.
    /// </summary>
    public sealed class SpillStream : UnmanagedMemoryStream
    {
        private readonly SafeBuffer buffer;
        private readonly long offset;

        internal SpillStream(SafeBuffer buffer, long offset, long length) : base(buffer, offset, length, FileAccess.ReadWrite)
        {
            this.buffer = buffer;
            this.offset = offset;
        }

        /// <summary>A stream over a part of this one, with no copy.</summary>
        public SpillStream Slice(long start, long length)
        {
            if (start < 0 || length < 0 || start + length > Length)
                throw new ArgumentOutOfRangeException(nameof(start));
            return new SpillStream(buffer, offset + start, length);
        }
    }

    /// <summary>
    /// Keeps unpacked bundle data in memory mapped files on disk instead of the RAM, once the RAM runs short.
    /// The files are in the user's cache folder (not /tmp: it is often tmpfs, in the RAM),
    /// removed at once on Linux and on close on Windows, so nothing stays behind after a crash.
    /// </summary>
    public static class SpillStorage
    {
        private const long ChunkSize = 1L << 30;
        //free disk space to leave to the rest of the system
        private const long DiskReserve = 4L << 30;

        private sealed class Chunk
        {
            public FileStream File;
            public MemoryMappedFile Map;
            public MemoryMappedViewAccessor View;
            public long Capacity;
            public long Used;
        }

        private static readonly object chunksLock = new object();
        private static readonly List<Chunk> chunks = new List<Chunk>();
        private static int fileCount;
        private static bool warned;

        public static string Directory { get; set; } = DefaultDirectory();

        /// <summary>Bytes handed out since the last reset.</summary>
        public static long Used
        {
            get
            {
                lock (chunksLock)
                {
                    long used = 0;
                    foreach (var chunk in chunks)
                        used += chunk.Used;
                    return used;
                }
            }
        }

        private static string DefaultDirectory()
        {
            if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
                return Path.Combine(Path.GetTempPath(), "AssetStudio", "spill");
            var cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            if (string.IsNullOrEmpty(cache))
                cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
            return Path.Combine(cache, "AssetStudio", "spill");
        }

        /// <summary>
        /// A stream of <paramref name="size"/> bytes for unpacked data: on disk when the RAM runs short
        /// (or when <paramref name="force"/>), otherwise, or when the disk can't take it, null.
        /// </summary>
        public static SpillStream TryCreate(long size, bool force = false)
        {
            if (size <= 0 || !force && !MemoryBudget.ShouldSpill())
                return null;
            //8 byte aligned pieces
            var reserve = (size + 7) & ~7L;
            lock (chunksLock)
            {
                var chunk = chunks.Find(x => x.Capacity - x.Used >= reserve) ?? AddChunk(Math.Max(ChunkSize, reserve));
                if (chunk == null)
                    return null;
                var stream = new SpillStream(chunk.View.SafeMemoryMappedViewHandle, chunk.Used, size);
                chunk.Used += reserve;
                return stream;
            }
        }

        /// <summary>
        /// Hands the disk space out again from the start. The files stay: streams left from before
        /// then read other data, but never memory that is gone.
        /// </summary>
        public static void Reset()
        {
            lock (chunksLock)
            {
                foreach (var chunk in chunks)
                    chunk.Used = 0;
            }
        }

        private static Chunk AddChunk(long capacity)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                var free = new DriveInfo(Path.GetFullPath(Directory)).AvailableFreeSpace;
                if (free < capacity + DiskReserve)
                {
                    WarnOnce($"Not enough free disk space in {Directory} ({MemoryBudget.Format(free)}) to move unpacked data out of the RAM");
                    return null;
                }
                var path = Path.Combine(Directory, $"spill-{Environment.ProcessId}-{fileCount++}.bin");
                var file = new FileStream(path, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.ReadWrite | FileShare.Delete,
                    Options = FileOptions.DeleteOnClose,
                    //allocated at once: a full disk fails here, not later as a crash inside the mapping
                    PreallocationSize = capacity,
                });
                var map = MemoryMappedFile.CreateFromFile(file, null, capacity, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, true);
                var view = map.CreateViewAccessor(0, capacity, MemoryMappedFileAccess.ReadWrite);
                if (!OperatingSystem.IsWindows())
                {
                    //the mapping keeps the data, the name can go now
                    File.Delete(path);
                }
                var chunk = new Chunk { File = file, Map = map, View = view, Capacity = capacity };
                chunks.Add(chunk);
                Logger.Verbose($"Spilling unpacked data to disk, {MemoryBudget.Format(capacity)} more in {Directory}");
                return chunk;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                WarnOnce($"Unable to move unpacked data out of the RAM to {Directory}: {e.Message}");
                return null;
            }
        }

        private static void WarnOnce(string message)
        {
            if (warned)
                return;
            warned = true;
            Logger.Warning(message);
        }
    }
}
