using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace AssetStudio.Avalonia
{
    /// <summary>
    /// In-process audio preview: decoded WAV / Ogg Vorbis played through libpulse-simple
    /// (PulseAudio or PipeWire's pulse server), with pause, seek, loop and volume.
    /// </summary>
    internal sealed class AudioPlayer : IDisposable
    {
        private const int BlockMilliseconds = 20;
        private const int BufferMilliseconds = 80;

        private readonly short[] samples; // interleaved
        private readonly object sync = new object();
        private readonly Thread thread;
        private IntPtr stream;
        private long position; // next frame to write
        private bool seekPending;
        private volatile bool stopping;

        public int Channels { get; }
        public int SampleRate { get; }
        public long FrameCount { get; }
        public TimeSpan Length => TimeSpan.FromSeconds((double)FrameCount / SampleRate);
        public bool Loop { get; set; }
        public float Volume { get; set; } = 1f;
        public bool IsPaused { get; private set; }
        public bool IsFinished { get; private set; }
        public string Error { get; private set; }

        private static readonly Lazy<bool> available = new Lazy<bool>(() =>
            NativeLibrary.TryLoad("libpulse-simple.so.0", out _) && NativeLibrary.TryLoad("libpulse.so.0", out _));

        /// <summary>libpulse-simple is installed (the server itself is only checked by <see cref="Start"/>).</summary>
        public static bool IsAvailable => available.Value;

        private AudioPlayer(short[] samples, int channels, int sampleRate)
        {
            this.samples = samples;
            Channels = channels;
            SampleRate = sampleRate;
            FrameCount = samples.Length / channels;
            thread = new Thread(Run) { IsBackground = true, Name = "Audio preview" };
        }

        /// <summary>Decodes a .wav or .ogg file image; returns null for other formats.</summary>
        public static AudioPlayer FromFile(byte[] data, string extension)
        {
            var decoded = extension?.ToLowerInvariant() switch
            {
                ".wav" => DecodeWav(data),
                ".ogg" => DecodeOgg(data),
                _ => default
            };
            return decoded.samples == null || decoded.channels <= 0 || decoded.rate <= 0 ? null : new AudioPlayer(decoded.samples, decoded.channels, decoded.rate);
        }

        /// <summary>Opens the playback stream and starts playing. Returns false (see <see cref="Error"/>) when no server is reachable.</summary>
        public bool Start()
        {
            var spec = new pa_sample_spec { format = PA_SAMPLE_S16LE, rate = (uint)SampleRate, channels = (byte)Channels };
            var bytesPerMs = SampleRate * Channels * 2 / 1000;
            // small target buffer: pause, seek and volume changes are heard within ~BufferMilliseconds
            var attr = new pa_buffer_attr { maxlength = uint.MaxValue, tlength = (uint)(bytesPerMs * BufferMilliseconds), prebuf = uint.MaxValue, minreq = uint.MaxValue, fragsize = uint.MaxValue };
            stream = pa_simple_new(null, "AssetStudio", PA_STREAM_PLAYBACK, null, "Audio preview", ref spec, IntPtr.Zero, ref attr, out var error);
            if (stream == IntPtr.Zero)
            {
                Error = Marshal.PtrToStringAnsi(pa_strerror(error));
                return false;
            }
            thread.Start();
            return true;
        }

        /// <summary>Current playback position (what is heard, not what was written).</summary>
        public TimeSpan Position
        {
            get
            {
                long frames;
                lock (sync)
                {
                    frames = position;
                }
                if (!IsPaused && !IsFinished && stream != IntPtr.Zero)
                {
                    var latency = (long)pa_simple_get_latency(stream, out _);
                    frames -= latency * SampleRate / 1_000_000;
                    if (frames < 0)
                        frames = Loop ? frames + FrameCount : 0; // just wrapped around
                }
                return TimeSpan.FromSeconds((double)Math.Clamp(frames, 0, FrameCount) / SampleRate);
            }
        }

        public void Pause() => IsPaused = true;

        /// <summary>Resumes, or restarts from the beginning once the end was reached.</summary>
        public void Play()
        {
            lock (sync)
            {
                if (IsFinished)
                {
                    position = 0;
                    IsFinished = false;
                }
                IsPaused = false;
            }
        }

        public void Seek(TimeSpan time)
        {
            lock (sync)
            {
                position = Math.Clamp((long)(time.TotalSeconds * SampleRate), 0, FrameCount);
                seekPending = true;
                IsFinished = false;
            }
        }

        private void Run()
        {
            var blockFrames = Math.Max(1, SampleRate * BlockMilliseconds / 1000);
            var buffer = new short[blockFrames * Channels];
            while (!stopping)
            {
                if (IsPaused)
                {
                    Thread.Sleep(10);
                    continue;
                }
                int frames;
                bool ended;
                lock (sync)
                {
                    if (seekPending)
                    {
                        pa_simple_flush(stream, out _);
                        seekPending = false;
                    }
                    if (position >= FrameCount && Loop)
                    {
                        position = 0;
                    }
                    ended = position >= FrameCount;
                }
                if (ended)
                {
                    // let the buffered audio play out, then wait for Play / Seek
                    pa_simple_drain(stream, out _);
                    lock (sync)
                    {
                        if (position >= FrameCount && !seekPending)
                        {
                            IsFinished = true;
                            IsPaused = true;
                        }
                    }
                    continue;
                }
                lock (sync)
                {
                    frames = (int)Math.Min(blockFrames, FrameCount - position);
                    var volume = Math.Clamp(Volume, 0f, 1f);
                    var offset = position * Channels;
                    for (int i = 0; i < frames * Channels; i++)
                    {
                        buffer[i] = (short)(samples[offset + i] * volume);
                    }
                    position += frames;
                }
                if (pa_simple_write(stream, buffer, (UIntPtr)(frames * Channels * 2), out var error) < 0)
                {
                    Error = Marshal.PtrToStringAnsi(pa_strerror(error));
                    IsFinished = true;
                    IsPaused = true;
                    break;
                }
            }
        }

        public void Dispose()
        {
            stopping = true;
            IsPaused = false;
            if (thread.IsAlive)
            {
                thread.Join();
            }
            if (stream != IntPtr.Zero)
            {
                pa_simple_flush(stream, out _);
                pa_simple_free(stream);
                stream = IntPtr.Zero;
            }
        }

        #region Decoders

        private static (short[] samples, int channels, int rate) DecodeWav(byte[] data)
        {
            using var reader = new BinaryReader(new MemoryStream(data));
            if (data.Length < 12 || reader.ReadUInt32() != 0x46464952 /* RIFF */ || reader.ReadUInt32() == 0 || reader.ReadUInt32() != 0x45564157 /* WAVE */)
                return default;
            int format = 0, channels = 0, rate = 0, bits = 0;
            while (reader.BaseStream.Position + 8 <= data.Length)
            {
                var id = reader.ReadUInt32();
                var size = reader.ReadUInt32();
                var start = reader.BaseStream.Position;
                if (id == 0x20746D66) // "fmt "
                {
                    format = reader.ReadUInt16();
                    channels = reader.ReadUInt16();
                    rate = reader.ReadInt32();
                    reader.ReadInt32(); // byte rate
                    reader.ReadUInt16(); // block align
                    bits = reader.ReadUInt16();
                    if (format == 0xFFFE && size >= 40) // WAVE_FORMAT_EXTENSIBLE: the sub format GUID starts with the format tag
                    {
                        reader.BaseStream.Position = start + 24;
                        format = reader.ReadUInt16();
                    }
                }
                else if (id == 0x61746164) // "data"
                {
                    var length = (int)Math.Min(size, data.Length - start);
                    var bytesPerSample = bits / 8;
                    if (channels == 0 || bytesPerSample == 0)
                        return default;
                    var count = length / bytesPerSample / channels * channels;
                    var result = new short[count];
                    var span = data.AsSpan((int)start, count * bytesPerSample);
                    for (int i = 0; i < count; i++)
                    {
                        var s = span.Slice(i * bytesPerSample, bytesPerSample);
                        result[i] = (format, bits) switch
                        {
                            (1, 8) => (short)((s[0] - 128) << 8),
                            (1, 16) => BitConverter.ToInt16(s),
                            (1, 24) => (short)(s[1] | s[2] << 8),
                            (1, 32) => (short)(BitConverter.ToInt32(s) >> 16),
                            (3, 32) => FloatToShort(BitConverter.ToSingle(s)),
                            (3, 64) => FloatToShort((float)BitConverter.ToDouble(s)),
                            _ => throw new NotSupportedException($"WAV format {format} with {bits} bits"),
                        };
                    }
                    return (result, channels, rate);
                }
                reader.BaseStream.Position = start + size + (size & 1);
            }
            return default;
        }

        private static (short[] samples, int channels, int rate) DecodeOgg(byte[] data)
        {
            using var vorbis = new NVorbis.VorbisReader(new MemoryStream(data), true);
            var channels = vorbis.Channels;
            var result = new System.Collections.Generic.List<short>((int)Math.Max(0, Math.Min(vorbis.TotalSamples * channels, int.MaxValue)));
            var buffer = new float[4096 * channels];
            int read;
            while ((read = vorbis.ReadSamples(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    result.Add(FloatToShort(buffer[i]));
                }
            }
            return (result.ToArray(), channels, vorbis.SampleRate);
        }

        private static short FloatToShort(float value) => (short)Math.Clamp(value * 32767f, -32768f, 32767f);

        #endregion

        #region libpulse-simple

        private const int PA_SAMPLE_S16LE = 3;
        private const int PA_STREAM_PLAYBACK = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct pa_sample_spec
        {
            public int format;
            public uint rate;
            public byte channels;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct pa_buffer_attr
        {
            public uint maxlength;
            public uint tlength;
            public uint prebuf;
            public uint minreq;
            public uint fragsize;
        }

        [DllImport("libpulse-simple.so.0")]
        private static extern IntPtr pa_simple_new(string server, string name, int dir, string dev, string stream_name, ref pa_sample_spec ss, IntPtr map, ref pa_buffer_attr attr, out int error);

        [DllImport("libpulse-simple.so.0")]
        private static extern int pa_simple_write(IntPtr s, short[] data, UIntPtr bytes, out int error);

        [DllImport("libpulse-simple.so.0")]
        private static extern int pa_simple_drain(IntPtr s, out int error);

        [DllImport("libpulse-simple.so.0")]
        private static extern int pa_simple_flush(IntPtr s, out int error);

        [DllImport("libpulse-simple.so.0")]
        private static extern ulong pa_simple_get_latency(IntPtr s, out int error);

        [DllImport("libpulse-simple.so.0")]
        private static extern void pa_simple_free(IntPtr s);

        [DllImport("libpulse.so.0")]
        private static extern IntPtr pa_strerror(int error);

        #endregion
    }
}
