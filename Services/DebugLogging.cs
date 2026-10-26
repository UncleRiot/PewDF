using System;
using System.IO;
using System.Text;

namespace PewDF.Services
{
    // SEMANTIC ANCHOR: DIAGNOSTICS_GLOBAL_OPT_IN_SSOT
    // One user preference controls application-owned file diagnostics. Default OFF.
    // Logging is best effort, thread safe, and must never change application behavior.
    internal static class DebugLogging
    {
        private static readonly object Gate = new();
        private static readonly string DirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PewDF");
        private static readonly string PreferencePath = Path.Combine(DirectoryPath, "debug-logging.enabled");
        private static bool _enabled = File.Exists(PreferencePath);

        internal static bool Enabled
        {
            get { lock (Gate) return _enabled; }
            set
            {
                lock (Gate)
                {
                    _enabled = value;
                    try
                    {
                        if (value)
                        {
                            Directory.CreateDirectory(DirectoryPath);
                            File.WriteAllText(PreferencePath, "enabled", Encoding.UTF8);
                        }
                        else if (File.Exists(PreferencePath)) File.Delete(PreferencePath);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        internal static void Write(string fileName, string message)
        {
            lock (Gate)
            {
                if (!_enabled) return;
                try
                {
                    Directory.CreateDirectory(DirectoryPath);
                    File.AppendAllText(Path.Combine(DirectoryPath, fileName),
                        $"{DateTimeOffset.Now:O} | {message}{Environment.NewLine}", Encoding.UTF8);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
