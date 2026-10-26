using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Docnet.Core;
using Docnet.Core.Models;
using Docnet.Core.Readers;
using PewDF.Services;

namespace PewDF.Features
{
    // ============================================================
    // Headless CLI render benchmark
    // ============================================================
    //
    // PewDF.exe --batch-render <input.pdf|inputDir> <outputDir> [--size <px>] [--pages <n>] [--log <file.csv>] [--quiet]
    //
    // Renders the first N pages of one PDF (or every *.pdf under a folder tree)
    // through the same page-render path the viewer, print, flatten, and image
    // export use, scaled to fit inside a --size square (default 1024), and
    // writes one PNG per page mirroring the input tree. The CSV log records
    // milliseconds per page so two builds can be compared on the same input.
    //
    // Exit codes: 0 = every page rendered or the file was skipped with a
    // reason, 1 = at least one render failed, 2 = bad usage or bad paths.
    internal static class BatchRenderRunner
    {
        private const int DefaultSize = 1024;
        private const int MaximumSize = 8192;

        internal static bool TryRunBatchRender(string[] args, out int exitCode)
        {
            exitCode = 0;
            int flagIdx = Array.FindIndex(args,
                a => string.Equals(a, "--batch-render", StringComparison.OrdinalIgnoreCase));
            if (flagIdx < 0) return false;

            var con = BatchRunner.OpenBatchConsole();
            string? input = null, output = null, logPath = null;
            int size = DefaultSize, pages = 1;
            bool quiet = false, badUsage = false;
            for (int i = flagIdx + 1; i < args.Length; i++)
            {
                var a = args[i];
                if (string.Equals(a, "--log", StringComparison.OrdinalIgnoreCase))
                {
                    // INVARIANT: Value-taking CLI options consume a path, never another option.
                    // Keep this validation aligned with the batch-render CLI parser.
                    if (i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1])
                        && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        logPath = args[++i];
                    else badUsage = true;
                }
                else if (string.Equals(a, "--size", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length && int.TryParse(args[++i], NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out size) && size >= 16 && size <= MaximumSize)
                        continue;
                    badUsage = true;
                }
                else if (string.Equals(a, "--pages", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length && int.TryParse(args[++i], NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out pages) && pages >= 1)
                        continue;
                    badUsage = true;
                }
                else if (string.Equals(a, "--quiet", StringComparison.OrdinalIgnoreCase))
                {
                    quiet = true;
                }
                // SEMANTIC ANCHOR: BATCH_CLI_ARGUMENT_CONTRACT
                // INVARIANT: Both batch entry points reject unknown long options and
                // require explicit values for value-taking options before any file I/O.
                // SSOT: This parser owns its mode's grammar; keep shared switches and
                // exit-code meanings consistent with the other batch entry point.
                // INVARIANT: Unknown long options are invalid, never positional file paths.
                // Keep the batch-render and batch-resave parsers consistent.
                else if (a.StartsWith("--", StringComparison.Ordinal)) badUsage = true;
                else if (input is null) input = a;
                else if (output is null) output = a;
                else badUsage = true;
            }

            if (badUsage || string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output))
            {
                con.WriteLine("Usage: PewDF.exe --batch-render <input.pdf|inputDir> <outputDir> [--size <px>] [--pages <n>] [--log <file.csv>] [--quiet]");
                exitCode = 2;
                return true;
            }

            try
            {
                exitCode = Run(input!, output!, size, pages, logPath, quiet, con);
            }
            catch (Exception ex)
            {
                con.WriteLine("Render batch failed: " + BatchRunner.FlattenBatchDetail(ex.Message));
                exitCode = 2;
            }
            return true;
        }

        private static int Run(string input, string output, int size, int pageLimit,
            string? logPath, bool quiet, TextWriter con)
        {
            var work = new List<(string Rel, string Src)>();
            if (File.Exists(input))
            {
                work.Add((Path.GetFileName(input), Path.GetFullPath(input)));
            }
            else if (Directory.Exists(input))
            {
                // SEMANTIC ANCHOR: CLI_DIRECTORY_ROOT_CANONICALIZATION
                // INVARIANT: Normalizing a directory path must preserve its volume/root identity.
                // SSOT: Use Path.TrimEndingDirectorySeparator rather than trimming separators manually.
                string inRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input));
                foreach (var f in Directory.GetFiles(inRoot, "*.pdf", SearchOption.AllDirectories))
                    work.Add((f[inRoot.Length..].TrimStart('\\', '/'), f));
            }
            else
            {
                con.WriteLine($"Input not found: {input}");
                return 2;
            }

            string outRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(output));
            // SEMANTIC ANCHOR: CLI_RENDER_OUTPUT_PATH_INTEGRITY
            // INVARIANT: The requested CSV log must not replace an input PDF or a rendered PNG.
            // Validate against the exact centralized output-naming convention before writing.
            if (!string.IsNullOrWhiteSpace(logPath))
            {
                string fullLogPath = Path.GetFullPath(logPath);
                foreach (var (rel, src) in work)
                {
                    if (string.Equals(fullLogPath, Path.GetFullPath(src), StringComparison.OrdinalIgnoreCase))
                    {
                        con.WriteLine($"Log conflicts with input PDF: {fullLogPath}");
                        return 2;
                    }
                    string dstBase = Path.Combine(outRoot, rel);
                    string pngPrefix = Path.GetFullPath(dstBase + "-page-");
                    const string pngSuffix = ".png";
                    if (fullLogPath.StartsWith(pngPrefix, StringComparison.OrdinalIgnoreCase)
                        && fullLogPath.EndsWith(pngSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        string pagePart = fullLogPath.Substring(pngPrefix.Length,
                            fullLogPath.Length - pngPrefix.Length - pngSuffix.Length);
                        if (int.TryParse(pagePart, NumberStyles.None, CultureInfo.InvariantCulture, out int page)
                            && page >= 1 && page <= pageLimit
                            && string.Equals(pagePart, page.ToString(CultureInfo.InvariantCulture).PadLeft(3, '0'),
                                StringComparison.Ordinal))
                        {
                            con.WriteLine($"Log conflicts with rendered output: {fullLogPath}");
                            return 2;
                        }
                    }
                }
            }
            Directory.CreateDirectory(outRoot);
            var log = new List<string> { "File,Page,Status,Milliseconds,Width,Height,Detail" };
            int ok = 0, skip = 0, fail = 0;
            long totalMs = 0;
            var total = Stopwatch.StartNew();

            foreach (var (rel, src) in work)
            {
                string dstBase = Path.Combine(outRoot, rel);
                // SEMANTIC ANCHOR: BATCH_RENDER_PER_FILE_ISOLATION
                // INVARIANT: A destination-directory failure belongs to its source file;
                // it must not suppress the remaining inputs or the requested CSV log.
                List<RenderRow> rows;
                try
                {
                    var dstDir = Path.GetDirectoryName(dstBase);
                    if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);
                    rows = RenderFile(src, dstBase, size, pageLimit);
                }
                catch (Exception ex)
                {
                    rows = new List<RenderRow> { new RenderRow { Page = 0, Status = "FAIL",
                        Detail = "destination failed: " + BatchRunner.FlattenBatchDetail(ex.Message) } };
                }
                foreach (var row in rows)
                {
                    if (row.Status == "OK") ok++;
                    else if (row.Status == "SKIP") skip++;
                    else fail++;
                    totalMs += row.Milliseconds;
                    if (!quiet)
                        con.WriteLine(row.Detail.Length > 0
                            ? $"{row.Status} {rel} p{row.Page + 1} {row.Milliseconds} ms ({row.Detail})"
                            : $"{row.Status} {rel} p{row.Page + 1} {row.Milliseconds} ms");
                    log.Add(string.Join(",", Csv(rel), (row.Page + 1).ToString(CultureInfo.InvariantCulture),
                        row.Status, row.Milliseconds.ToString(CultureInfo.InvariantCulture),
                        row.Width.ToString(CultureInfo.InvariantCulture),
                        row.Height.ToString(CultureInfo.InvariantCulture), Csv(row.Detail)));
                }
            }

            total.Stop();
            con.WriteLine($"Done. {work.Count} files, {ok} pages OK, {skip} skipped, {fail} failed, {totalMs} ms rendering, {total.ElapsedMilliseconds} ms total.");
            if (!string.IsNullOrWhiteSpace(logPath))
            {
                try
                {
                    File.WriteAllLines(logPath, log, new UTF8Encoding(false));
                    con.WriteLine($"Log written to {logPath}");
                }
                catch (Exception ex)
                {
                    con.WriteLine("Could not write log: " + BatchRunner.FlattenBatchDetail(ex.Message));
                    // INVARIANT: An explicitly requested log is part of the CLI result.
                    // Preserve file-render failure precedence; never signal success on log failure.
                    return fail > 0 ? 1 : 2;
                }
            }
            return fail > 0 ? 1 : 0;
        }

        private struct RenderRow
        {
            public int Page;
            public string Status;
            public long Milliseconds;
            public int Width;
            public int Height;
            public string Detail;
        }

        // Fits each page inside a size-by-size box through PDFium, the same as the
        // image export path (annotations and form fields painted).
        private static List<RenderRow> RenderFile(string src, string dstBase, int size, int pageLimit)
        {
            var rows = new List<RenderRow>();
            IDocReader? reader = null;
            int pageCount;
            var open = Stopwatch.StartNew();
            try
            {
                reader = DocLib.Instance.GetDocReader(src, new PageDimensions(size, size));
                pageCount = reader.GetPageCount();
            }
            catch (Exception ex)
            {
                reader?.Dispose();
                rows.Add(new RenderRow { Page = 0, Status = "SKIP", Milliseconds = open.ElapsedMilliseconds,
                    Detail = "open failed: " + BatchRunner.FlattenBatchDetail(ex.Message) });
                return rows;
            }

            using (reader)
            {
                if (pageCount <= 0)
                {
                    rows.Add(new RenderRow { Page = 0, Status = "SKIP", Milliseconds = open.ElapsedMilliseconds,
                        Detail = "no pages" });
                    return rows;
                }
                int last = Math.Min(pageCount, pageLimit);
                for (int idx = 0; idx < last; idx++)
                {
                    var row = new RenderRow { Page = idx, Status = "OK", Detail = string.Empty };
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        byte[] raw; int w, h;
                        using (var pr = reader.GetPageReader(idx))
                        {
                            w = pr.GetPageWidth();
                            h = pr.GetPageHeight();
                            raw = PdfiumInterop.RenderPageWithAnnotations(src, idx, w, h)
                                ?? pr.GetImage(new Docnet.Core.Converters.NaiveTransparencyRemover());
                        }
                        sw.Stop();
                        row.Width = w;
                        row.Height = h;
                        // SEMANTIC ANCHOR: BATCH_RENDER_ATOMIC_PAGE_OUTPUT
                        // INVARIANT: Failed page writes must not leave truncated final PNGs.
                        // SSOT: The final page filename is defined here and reused for commit.
                        string finalPath = $"{dstBase}-page-{(idx + 1).ToString(CultureInfo.InvariantCulture).PadLeft(3, '0')}.png";
                        string tempPath = finalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        try
                        {
                            File.WriteAllBytes(tempPath, BitmapHelpers.RenderToPng(raw, w, h));
                            File.Move(tempPath, finalPath, overwrite: true);
                        }
                        finally
                        {
                            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
                            catch (IOException) { /* Preserve the original write/commit outcome. */ }
                            catch (UnauthorizedAccessException) { /* Preserve the original write/commit outcome. */ }
                        }
                    }
                    catch (Exception ex)
                    {
                        sw.Stop();
                        row.Status = "FAIL";
                        row.Detail = BatchRunner.FlattenBatchDetail(ex.Message);
                    }
                    row.Milliseconds = sw.ElapsedMilliseconds;
                    rows.Add(row);
                }
            }
            return rows;
        }

        private static string Csv(string s)
        {
            if (s.IndexOfAny([',', '"', '\r', '\n']) < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }
}
