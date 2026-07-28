using System;
using System.IO;

namespace SolidworksExecution.Infrastructure
{
    // Minimal file logger (P0.6-mini). Writes next to the exe as execution.log.
    // Must NEVER throw — logging failures are swallowed.
    public static class ExecLog
    {
        private static readonly object _lock = new object();

        public static readonly string FilePath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "execution.log");

        public static void Write(string message)
        {
            try
            {
                lock (_lock)
                {
                    File.AppendAllText(FilePath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine);
                }
            }
            catch
            {
                // logging must never break execution
            }
        }
    }
}
