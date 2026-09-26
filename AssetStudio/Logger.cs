using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace AssetStudio
{
    public static class Logger
    {
        private static bool _fileLogging;

        public static ILogger Default = new DummyLogger();
        public static ILogger File;

        public static bool Silent { get; set; }
        public static LoggerEvent Flags { get; set; }

        public static bool FileLogging
        {
            get => _fileLogging;
            set
            {
                _fileLogging = value;
                if (_fileLogging)
                {
                    try
                    {
                        File = new FileLogger();
                    }
                    catch
                    {
                        _fileLogging = false;
                        Error("log file is already in use, disabling...");
                        return;
                    }
                }
                else
                {
                    ((FileLogger)File)?.Dispose();
                    File = null;
                }
            }
        }

        public static bool VerboseEnabled => Flags.HasFlag(LoggerEvent.Verbose) && !Silent;

        /// <summary>
        /// Verbose($"...") with this overload doesn't format the message when verbose logging is off
        /// (it is called for every object while loading).
        /// </summary>
        public static void Verbose([InterpolatedStringHandlerArgument] ref VerboseInterpolatedStringHandler message)
        {
            if (message.IsEnabled)
            {
                Verbose(message.ToStringAndClear());
            }
        }

        public static void Verbose(string message)
        {
            if (!Flags.HasFlag(LoggerEvent.Verbose) || Silent)
                return;

            try
            {
                var stackTrace = new StackTrace();
                var frame = 1;
                while (frame < stackTrace.FrameCount - 1 && stackTrace.GetFrame(frame).GetMethod()?.DeclaringType == typeof(Logger))
                {
                    frame++;
                }
                var callerMethod = stackTrace.GetFrame(frame).GetMethod();
                var callerMethodClass = callerMethod.ReflectedType.Name;
                if (!string.IsNullOrEmpty(callerMethodClass))
                {
                    message = $"[{callerMethodClass}] {message}";
                }
            }
            catch (Exception) { }
            if (FileLogging) File.Log(LoggerEvent.Verbose, message);
            Default.Log(LoggerEvent.Verbose, message);
        }
        public static void Debug(string message)
        {
            if (!Flags.HasFlag(LoggerEvent.Debug) || Silent)
                return;

            if (FileLogging) File.Log(LoggerEvent.Debug, message);
            Default.Log(LoggerEvent.Debug, message);
        }
        public static void Info(string message)
        {
            if (!Flags.HasFlag(LoggerEvent.Info) || Silent)
                return;

            if (FileLogging) File.Log(LoggerEvent.Info, message);
            Default.Log(LoggerEvent.Info, message);
        }
        public static void Warning(string message)
        {
            if (!Flags.HasFlag(LoggerEvent.Warning) || Silent)
                return;

            if (FileLogging) File.Log(LoggerEvent.Warning, message);
            Default.Log(LoggerEvent.Warning, message);
        }
        public static void Error(string message)
        {
            if (!Flags.HasFlag(LoggerEvent.Error) || Silent)
                return;

            if (FileLogging) File.Log(LoggerEvent.Error, message);
            Default.Log(LoggerEvent.Error, message);
        }

        public static void Error(string message, Exception e)
        {
            if (!Flags.HasFlag(LoggerEvent.Error) || Silent)
                return;

            var sb = new StringBuilder();
            sb.AppendLine(message);
            sb.AppendLine(e.ToString());

            message = sb.ToString();
            if (FileLogging) File.Log(LoggerEvent.Error, message);
            Default.Log(LoggerEvent.Error, message);
        }
    }

    [InterpolatedStringHandler]
    public ref struct VerboseInterpolatedStringHandler
    {
        private DefaultInterpolatedStringHandler inner;
        public readonly bool IsEnabled;

        public VerboseInterpolatedStringHandler(int literalLength, int formattedCount, out bool isEnabled)
        {
            IsEnabled = isEnabled = Logger.VerboseEnabled;
            inner = isEnabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
        }

        public void AppendLiteral(string value) => inner.AppendLiteral(value);
        public void AppendFormatted<T>(T value) => inner.AppendFormatted(value);
        public void AppendFormatted<T>(T value, string format) => inner.AppendFormatted(value, format);
        public void AppendFormatted<T>(T value, int alignment) => inner.AppendFormatted(value, alignment);
        public void AppendFormatted<T>(T value, int alignment, string format) => inner.AppendFormatted(value, alignment, format);
        public void AppendFormatted(ReadOnlySpan<char> value) => inner.AppendFormatted(value);
        public void AppendFormatted(string value) => inner.AppendFormatted(value);
        public string ToStringAndClear() => inner.ToStringAndClear();
    }
}
