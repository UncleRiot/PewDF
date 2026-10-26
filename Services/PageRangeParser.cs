using System.Collections.Generic;

namespace PewDF.Services
{
    // SEMANTIC ANCHOR: PAGE_RANGE_PARSER_SSOT.
    // INVARIANT: User-entered page ranges are 1-based; resulting indices are
    // sorted, unique and zero-based. Reject empty, malformed and out-of-range
    // selections without changing the document or any output file.
    internal static class PageRangeParser
    {
        internal static List<int>? ParsePageRange(string spec, int pageCount, out string error)
        {
            error = string.Empty;
            var pages = new SortedSet<int>();
            foreach (var rawPart in spec.Split(','))
            {
                var part = rawPart.Trim();
                if (part.Length == 0) continue;
                int a, b;
                int dash = part.IndexOf('-');
                if (dash > 0)
                {
                    if (!int.TryParse(part[..dash].Trim(), out a) ||
                        !int.TryParse(part[(dash + 1)..].Trim(), out b))
                    { error = $"Bad page range: \"{part}\""; return null; }
                }
                else
                {
                    if (!int.TryParse(part, out a)) { error = $"Bad page number: \"{part}\""; return null; }
                    b = a;
                }
                if (a > b) (a, b) = (b, a);
                if (a < 1 || b > pageCount)
                { error = $"Pages {part} out of range - the document has {pageCount} pages"; return null; }
                for (int p = a; p <= b; p++) pages.Add(p - 1);
            }
            if (pages.Count == 0) { error = "Empty page range"; return null; }
            return [.. pages];
        }

    }
}
