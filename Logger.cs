using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IGMediaDownloaderV2
{
    internal static class Logger
    {
        private static readonly object _lock = new object();

        private enum Level { Debug, Info, Success, Warn, Error }

        public static void Debug(string message, int indent = 0) => Write(Level.Debug, message, indent);
        public static void Info(string message, int indent = 0) => Write(Level.Info, message, indent);
        public static void Success(string message, int indent = 0) => Write(Level.Success, message, indent);
        public static void Warn(string message, int indent = 0) => Write(Level.Warn, message, indent);
        public static void Error(string message, int indent = 0) => Write(Level.Error, message, indent);

        private static void Write(Level level, string message, int indent)
        {
            indent = Math.Max(0, indent);
            string pad = new string(' ', indent * 2);

            string ts = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
            string prefix = level.ToString().ToUpperInvariant();

            lock (_lock)
            {
                var old = Console.ForegroundColor;

                Console.ForegroundColor = level switch
                {
                    Level.Debug => ConsoleColor.DarkGray,
                    Level.Info => ConsoleColor.Cyan,
                    Level.Success => ConsoleColor.Green,
                    Level.Warn => ConsoleColor.Yellow,
                    Level.Error => ConsoleColor.Red,
                    _ => ConsoleColor.White
                };

                string line = $"[{ts}Z] [{prefix}] {pad}{message}";

                // Docker captures stdout/stderr automatically
                if (level == Level.Warn || level == Level.Error)
                    Console.Error.WriteLine(line);
                else
                    Console.Out.WriteLine(line);

                Console.ForegroundColor = old;
            }
        }
    }

}
