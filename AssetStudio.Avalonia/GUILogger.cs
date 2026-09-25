using System;

namespace AssetStudio.Avalonia
{
    /// <summary>
    /// Forwards log messages to the main window (log panel / status bar) and optionally to stdout.
    /// </summary>
    internal class GUILogger : ILogger
    {
        private readonly Action<LoggerEvent, string> action;

        public bool ShowErrorMessage = true;
        public bool WriteToConsole = true;

        public GUILogger(Action<LoggerEvent, string> action)
        {
            this.action = action;
        }

        public void Log(LoggerEvent loggerEvent, string message)
        {
            if (WriteToConsole)
            {
                Console.WriteLine("[{0}] {1}", loggerEvent, message);
            }
            action(loggerEvent, message);
        }
    }
}
