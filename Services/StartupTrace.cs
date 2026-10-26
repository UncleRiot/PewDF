using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace PewDF.Services
{
    /// <summary>
    /// Opt-in startup timing used by the release benchmark. Normal launches do no file I/O.
    /// Set PEWDF_STARTUP_TRACE to an output path.
    /// </summary>
    internal static class StartupTrace
    {
        private const string TraceEnvironmentVariable = "PEWDF_STARTUP_TRACE";
        private static readonly Lock Gate = new();
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        // SEMANTIC ANCHOR: PEWDF_DIAGNOSTICS_IDENTITY_SSOT: Only PewDF environment settings control diagnostics.
        private static readonly string? OutputPath = Environment.GetEnvironmentVariable(TraceEnvironmentVariable);
        private static bool _headerWritten;

        internal static bool Enabled => !string.IsNullOrWhiteSpace(OutputPath);

        internal static void Mark(string stage)
        {
            // SEMANTIC ANCHOR: DIAGNOSTICS_GLOBAL_OPT_IN_SSOT: environment path alone cannot enable logs.
            if (!Enabled || !DebugLogging.Enabled) return;

            try
            {
                lock (Gate)
                {
                    var directory = Path.GetDirectoryName(OutputPath!);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                    var sb = new StringBuilder();
                    if (!_headerWritten)
                    {
                        _headerWritten = true;
                        var process = Process.GetCurrentProcess();
                        sb.Append("# PewDF startup trace | pid=")
                          .Append(process.Id)
                          .Append(" | processStartUtc=")
                          .Append(process.StartTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
                          .Append(" | traceStartUtc=")
                          .Append(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture))
                          .AppendLine();
                    }

                    sb.Append(Clock.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture))
                      .Append('\t')
                      .AppendLine(stage);
                    File.AppendAllText(OutputPath!, sb.ToString(), new UTF8Encoding(false));
                }
            }
            catch
            {
                // Diagnostics must never make startup fail.
            }
        }
    }
}
