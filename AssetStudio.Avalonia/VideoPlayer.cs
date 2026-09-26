using System;
using System.Runtime.InteropServices;

namespace AssetStudio.Avalonia
{
    /// <summary>
    /// In-process video preview (VideoClip, MovieTexture) with GStreamer: playbin decodes the file (and plays its sound),
    /// an appsink hands the frames over as BGRA. <see cref="TryGetFrame"/> is polled by the UI.
    /// </summary>
    internal sealed class VideoPlayer : IDisposable
    {
        private const string Gst = "libgstreamer-1.0.so.0";
        private const string GstApp = "libgstapp-1.0.so.0";
        private const string GObject = "libgobject-2.0.so.0";
        private const string GLib = "libglib-2.0.so.0";

        private const int GST_STATE_NULL = 1, GST_STATE_PAUSED = 3, GST_STATE_PLAYING = 4;
        private const int GST_FORMAT_TIME = 3;
        private const int GST_SEEK_FLAG_FLUSH = 1, GST_SEEK_FLAG_ACCURATE = 2;
        private const int GST_MESSAGE_EOS = 1, GST_MESSAGE_ERROR = 2;
        private const int GST_MAP_READ = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct GstMapInfo
        {
            public IntPtr memory;
            public int flags;
            public IntPtr data;
            public UIntPtr size;
            public UIntPtr maxsize;
            public IntPtr user0, user1, user2, user3;
            public IntPtr reserved0, reserved1, reserved2, reserved3;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GError
        {
            public uint domain;
            public int code;
            public IntPtr message;
        }

        [DllImport(Gst)] private static extern bool gst_init_check(IntPtr argc, IntPtr argv, out IntPtr error);
        [DllImport(Gst)] private static extern IntPtr gst_element_factory_make(string factory, string name);
        [DllImport(Gst)] private static extern IntPtr gst_parse_bin_from_description(string description, bool ghostUnlinkedPads, out IntPtr error);
        [DllImport(Gst)] private static extern IntPtr gst_bin_get_by_name(IntPtr bin, string name);
        [DllImport(Gst)] private static extern int gst_element_set_state(IntPtr element, int state);
        [DllImport(Gst)] private static extern bool gst_element_query_position(IntPtr element, int format, out long position);
        [DllImport(Gst)] private static extern bool gst_element_query_duration(IntPtr element, int format, out long duration);
        [DllImport(Gst)] private static extern bool gst_element_seek_simple(IntPtr element, int format, int flags, long position);
        [DllImport(Gst)] private static extern IntPtr gst_element_get_bus(IntPtr element);
        [DllImport(Gst)] private static extern IntPtr gst_bus_pop_filtered(IntPtr bus, int types);
        [DllImport(Gst)] private static extern void gst_message_parse_error(IntPtr message, out IntPtr error, out IntPtr debug);
        [DllImport(Gst)] private static extern void gst_mini_object_unref(IntPtr obj);
        [DllImport(Gst)] private static extern IntPtr gst_object_ref_sink(IntPtr obj);
        [DllImport(Gst)] private static extern void gst_object_unref(IntPtr obj);
        [DllImport(Gst)] private static extern IntPtr gst_sample_get_buffer(IntPtr sample);
        [DllImport(Gst)] private static extern IntPtr gst_sample_get_caps(IntPtr sample);
        [DllImport(Gst)] private static extern IntPtr gst_caps_get_structure(IntPtr caps, uint index);
        [DllImport(Gst)] private static extern bool gst_structure_get_int(IntPtr structure, string field, out int value);
        [DllImport(Gst)] private static extern bool gst_buffer_map(IntPtr buffer, out GstMapInfo info, int flags);
        [DllImport(Gst)] private static extern void gst_buffer_unmap(IntPtr buffer, ref GstMapInfo info);
        [DllImport(Gst)] private static extern void gst_util_set_object_arg(IntPtr obj, string name, string value);
        [DllImport(GstApp)] private static extern IntPtr gst_app_sink_try_pull_sample(IntPtr sink, ulong timeout);
        [DllImport(GstApp)] private static extern IntPtr gst_app_sink_try_pull_preroll(IntPtr sink, ulong timeout);
        // variadic: pointer arguments only, NULL terminated (no floating point, so the x86-64 calling convention matches)
        [DllImport(GObject)] private static extern void g_object_set(IntPtr obj, string name, IntPtr value, IntPtr terminator);
        [DllImport(GLib)] private static extern void g_error_free(IntPtr error);
        [DllImport(GLib)] private static extern void g_free(IntPtr memory);

        private static readonly Lazy<bool> available = new Lazy<bool>(() =>
        {
            try
            {
                if (!NativeLibrary.TryLoad(Gst, out _) || !NativeLibrary.TryLoad(GstApp, out _))
                    return false;
                if (!gst_init_check(IntPtr.Zero, IntPtr.Zero, out var error))
                {
                    FreeError(error);
                    return false;
                }
                var probe = gst_element_factory_make("playbin", null);
                if (probe == IntPtr.Zero)
                    return false;
                gst_object_unref(gst_object_ref_sink(probe));
                return true;
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
            {
                return false;
            }
        });

        /// <summary>GStreamer (with playbin) is installed.</summary>
        public static bool IsAvailable => available.Value;

        private IntPtr playbin;
        private IntPtr sink;
        private IntPtr bus;
        private bool playing;
        private IntPtr lastPreroll;

        public bool IsPlaying => playing;
        public bool IsFinished { get; private set; }
        public bool Loop { get; set; }
        public string Error { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }

        private VideoPlayer() { }

        /// <summary>A paused player of a file (the first frame comes as soon as it is decoded); null without GStreamer.</summary>
        public static VideoPlayer Open(string path, bool muted = false)
        {
            if (!IsAvailable)
                return null;
            var player = new VideoPlayer();
            player.playbin = gst_element_factory_make("playbin", "preview");
            if (player.playbin == IntPtr.Zero)
                return null;
            gst_object_ref_sink(player.playbin);
            var videoBin = gst_parse_bin_from_description("videoconvert ! video/x-raw,format=BGRA ! appsink name=frames max-buffers=2 drop=true", true, out var error);
            if (videoBin == IntPtr.Zero)
            {
                player.Error = FreeError(error);
                player.Dispose();
                return player;
            }
            player.sink = gst_bin_get_by_name(videoBin, "frames");
            g_object_set(player.playbin, "video-sink", videoBin, IntPtr.Zero);
            gst_util_set_object_arg(player.playbin, "uri", new Uri(System.IO.Path.GetFullPath(path)).AbsoluteUri);
            if (muted)
                gst_util_set_object_arg(player.playbin, "mute", "true");
            player.bus = gst_element_get_bus(player.playbin);
            gst_element_set_state(player.playbin, GST_STATE_PAUSED);
            return player;
        }

        private static string FreeError(IntPtr error)
        {
            if (error == IntPtr.Zero)
                return null;
            var message = Marshal.PtrToStructure<GError>(error).message;
            var text = Marshal.PtrToStringUTF8(message);
            g_error_free(error);
            return text;
        }

        public TimeSpan Position => playbin != IntPtr.Zero && gst_element_query_position(playbin, GST_FORMAT_TIME, out var position) ? TimeSpan.FromTicks(position / 100) : TimeSpan.Zero;
        public TimeSpan Duration => playbin != IntPtr.Zero && gst_element_query_duration(playbin, GST_FORMAT_TIME, out var duration) && duration > 0 ? TimeSpan.FromTicks(duration / 100) : TimeSpan.Zero;

        public void Play()
        {
            if (playbin == IntPtr.Zero)
                return;
            if (IsFinished)
            {
                Seek(TimeSpan.Zero);
                IsFinished = false;
            }
            gst_element_set_state(playbin, GST_STATE_PLAYING);
            playing = true;
        }

        public void Pause()
        {
            if (playbin == IntPtr.Zero)
                return;
            gst_element_set_state(playbin, GST_STATE_PAUSED);
            playing = false;
        }

        public void Seek(TimeSpan position)
        {
            if (playbin == IntPtr.Zero)
                return;
            IsFinished = false;
            lastPreroll = IntPtr.Zero;
            gst_element_seek_simple(playbin, GST_FORMAT_TIME, GST_SEEK_FLAG_FLUSH | GST_SEEK_FLAG_ACCURATE, position.Ticks * 100);
        }

        /// <param name="volume">0 to 1</param>
        public void SetVolume(double volume)
        {
            if (playbin != IntPtr.Zero)
                gst_util_set_object_arg(playbin, "volume", Math.Clamp(volume, 0, 1).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>Handles the end of the stream (loop) and errors; call regularly.</summary>
        public void Poll()
        {
            if (bus == IntPtr.Zero)
                return;
            //pop_filtered drops the messages of other types: ask for both at once
            IntPtr message;
            while ((message = gst_bus_pop_filtered(bus, GST_MESSAGE_ERROR | GST_MESSAGE_EOS)) != IntPtr.Zero)
            {
                //GstMessage.type follows the GstMiniObject header (64 bytes on 64-bit)
                var type = Marshal.ReadInt32(message, 64);
                if (type == GST_MESSAGE_ERROR)
                {
                    gst_message_parse_error(message, out var error, out var debug);
                    Error = FreeError(error);
                    if (debug != IntPtr.Zero)
                        g_free(debug);
                    gst_mini_object_unref(message);
                    Pause();
                    return;
                }
                gst_mini_object_unref(message);
                if (Loop)
                {
                    Seek(TimeSpan.Zero);
                }
                else
                {
                    IsFinished = true;
                    Pause();
                }
            }
        }

        /// <summary>The newest decoded frame (tightly packed BGRA, top row first), false when there is none since the last call.</summary>
        public bool TryGetFrame(ref byte[] pixels, out int width, out int height)
        {
            width = height = 0;
            if (sink == IntPtr.Zero)
                return false;
            //paused (or before playing): the frame the sink prerolled, after a seek too
            var sample = playing ? gst_app_sink_try_pull_sample(sink, 0) : gst_app_sink_try_pull_preroll(sink, 0);
            if (sample == IntPtr.Zero)
                return false;
            if (!playing)
            {
                if (sample == lastPreroll)
                {
                    gst_mini_object_unref(sample);
                    return false;
                }
                lastPreroll = sample;
            }
            try
            {
                var structure = gst_caps_get_structure(gst_sample_get_caps(sample), 0);
                if (!gst_structure_get_int(structure, "width", out width) || !gst_structure_get_int(structure, "height", out height) || width <= 0 || height <= 0)
                    return false;
                var buffer = gst_sample_get_buffer(sample);
                if (!gst_buffer_map(buffer, out var map, GST_MAP_READ))
                    return false;
                try
                {
                    var rowBytes = width * 4;
                    var size = (long)map.size;
                    //BGRA rows are 4 byte aligned: the stride is the width
                    var stride = height > 0 ? (int)(size / height) : rowBytes;
                    if (stride < rowBytes)
                        return false;
                    if (pixels == null || pixels.Length != rowBytes * height)
                        pixels = new byte[rowBytes * height];
                    for (int y = 0; y < height; y++)
                        Marshal.Copy(map.data + y * stride, pixels, y * rowBytes, rowBytes);
                    Width = width;
                    Height = height;
                    return true;
                }
                finally
                {
                    gst_buffer_unmap(buffer, ref map);
                }
            }
            finally
            {
                gst_mini_object_unref(sample);
            }
        }

        public void Dispose()
        {
            if (playbin != IntPtr.Zero)
            {
                gst_element_set_state(playbin, GST_STATE_NULL);
                if (sink != IntPtr.Zero)
                    gst_object_unref(sink);
                if (bus != IntPtr.Zero)
                    gst_object_unref(bus);
                gst_object_unref(playbin);
            }
            playbin = sink = bus = IntPtr.Zero;
            playing = false;
        }
    }
}
