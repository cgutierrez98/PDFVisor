using PDFVisor.Interop;

namespace PDFVisor.Core;

public enum PageDiffStatus
{
    Identical,
    Modified,
    OnlyInLeft,
    OnlyInRight
}

public enum DiffLineKind
{
    Unchanged,
    Removed,
    Added
}

public sealed record DiffLine(DiffLineKind Kind, string Text);

public sealed record PageDiffItem(
    int PageNumber,
    PageDiffStatus Status,
    IReadOnlyList<string> RemovedLines,
    IReadOnlyList<string> AddedLines,
    IReadOnlyList<DiffLine>? UnifiedLines = null)
{
    public string Summary => Status switch
    {
        PageDiffStatus.Identical => $"Página {PageNumber}: Sin cambios",
        PageDiffStatus.OnlyInLeft => $"Página {PageNumber}: Solo en documento A",
        PageDiffStatus.OnlyInRight => $"Página {PageNumber}: Solo en documento B",
        _ => $"Página {PageNumber}: Modificada (+{AddedLines.Count} / -{RemovedLines.Count} líneas)"
    };
}

public static class PdfDiffService
{
    /// <summary>
    /// Compares two PDF documents page-by-page using an exact LCS sequence diff so duplicate lines and
    /// reordered paragraphs are accurately detected.
    /// </summary>
    public static IReadOnlyList<PageDiffItem> CompareDocuments(PdfDocument leftDoc, PdfDocument rightDoc)
    {
        int maxPages = Math.Max(leftDoc.PageCount, rightDoc.PageCount);
        var results = new List<PageDiffItem>(maxPages);

        for (int i = 0; i < maxPages; i++)
        {
            bool hasLeft = i < leftDoc.PageCount;
            bool hasRight = i < rightDoc.PageCount;

            if (hasLeft && !hasRight)
            {
                var lines = SplitNormalizedLines(leftDoc.ExtractPageText(i));
                var unified = lines.Select(l => new DiffLine(DiffLineKind.Removed, l)).ToList();
                results.Add(new PageDiffItem(i + 1, PageDiffStatus.OnlyInLeft, lines, Array.Empty<string>(), unified));
                continue;
            }
            if (!hasLeft && hasRight)
            {
                var lines = SplitNormalizedLines(rightDoc.ExtractPageText(i));
                var unified = lines.Select(l => new DiffLine(DiffLineKind.Added, l)).ToList();
                results.Add(new PageDiffItem(i + 1, PageDiffStatus.OnlyInRight, Array.Empty<string>(), lines, unified));
                continue;
            }

            var leftLines = SplitNormalizedLines(leftDoc.ExtractPageText(i));
            var rightLines = SplitNormalizedLines(rightDoc.ExtractPageText(i));

            var unifiedDiff = ComputeLcsDiff(leftLines, rightLines);
            var removed = unifiedDiff.Where(d => d.Kind == DiffLineKind.Removed).Select(d => d.Text).ToList();
            var added = unifiedDiff.Where(d => d.Kind == DiffLineKind.Added).Select(d => d.Text).ToList();

            var status = (removed.Count == 0 && added.Count == 0)
                ? PageDiffStatus.Identical
                : PageDiffStatus.Modified;

            results.Add(new PageDiffItem(i + 1, status, removed, added, unifiedDiff));
        }

        return results;
    }

    /// <summary>
    /// Renders a visual pixel-by-pixel diff overlay (BGRA32) highlighting changed graphics/text/scans in crimson-magenta.
    /// </summary>
    public static (byte[] BgraPixels, int WidthPx, int HeightPx, double ChangedRatio) RenderVisualDiffBgra(
        PdfDocument leftDoc,
        PdfDocument rightDoc,
        int pageIndex,
        int widthPx = 720)
    {
        bool hasLeft = pageIndex >= 0 && pageIndex < leftDoc.PageCount;
        bool hasRight = pageIndex >= 0 && pageIndex < rightDoc.PageCount;

        PvPageSize refSize = hasLeft ? leftDoc.GetPageSize(pageIndex)
                           : hasRight ? rightDoc.GetPageSize(pageIndex)
                           : new PvPageSize { WidthPt = 595, HeightPt = 842 };

        int w = Math.Clamp(widthPx, 120, 2400);
        int h = Math.Clamp((int)Math.Round(w * (refSize.HeightPt / Math.Max(1.0, refSize.WidthPt))), 120, 3200);

        byte[]? leftBgra = hasLeft ? leftDoc.RenderPageBgra(pageIndex, w, h) : null;
        byte[]? rightBgra = hasRight ? rightDoc.RenderPageBgra(pageIndex, w, h) : null;

        byte[] outBgra = new byte[w * h * 4];
        long diffPixels = 0;
        long totalPixels = (long)w * h;

        for (int i = 0; i < outBgra.Length; i += 4)
        {
            byte lb = leftBgra is not null ? leftBgra[i] : (byte)255;
            byte lg = leftBgra is not null ? leftBgra[i + 1] : (byte)255;
            byte lr = leftBgra is not null ? leftBgra[i + 2] : (byte)255;

            byte rb = rightBgra is not null ? rightBgra[i] : (byte)255;
            byte rg = rightBgra is not null ? rightBgra[i + 1] : (byte)255;
            byte rr = rightBgra is not null ? rightBgra[i + 2] : (byte)255;

            int delta = Math.Abs(lr - rr) + Math.Abs(lg - rg) + Math.Abs(lb - rb);
            if (delta > 20)
            {
                diffPixels++;
                // Highlight changed pixels in vivid magenta/crimson while keeping luminance detail
                outBgra[i] = (byte)Math.Clamp(rb / 3 + 95, 0, 255);      // B
                outBgra[i + 1] = (byte)(rg / 5);                         // G
                outBgra[i + 2] = (byte)Math.Clamp(rr / 3 + 170, 0, 255); // R
                outBgra[i + 3] = 255;
            }
            else
            {
                // Unchanged regions rendered faded grayscale for immediate visual contrast
                byte gray = (byte)((rr * 77 + rg * 150 + rb * 29) >> 8);
                byte faded = (byte)(255 - ((255 - gray) * 45 / 100));
                outBgra[i] = faded;
                outBgra[i + 1] = faded;
                outBgra[i + 2] = faded;
                outBgra[i + 3] = 255;
            }
        }

        double ratio = totalPixels > 0 ? (double)diffPixels / totalPixels : 0.0;
        return (outBgra, w, h, ratio);
    }

    public static List<DiffLine> ComputeLcsDiff(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        int n = left.Count;
        int m = right.Count;

        // Trim common prefix and suffix for O(1) fast path on mostly-identical pages
        int prefix = 0;
        while (prefix < n && prefix < m && string.Equals(left[prefix], right[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        int suffix = 0;
        while (suffix < (n - prefix) && suffix < (m - prefix) &&
               string.Equals(left[n - 1 - suffix], right[m - 1 - suffix], StringComparison.Ordinal))
        {
            suffix++;
        }

        var output = new List<DiffLine>(n + m);
        for (int i = 0; i < prefix; i++)
        {
            output.Add(new DiffLine(DiffLineKind.Unchanged, left[i]));
        }

        int midN = n - prefix - suffix;
        int midM = m - prefix - suffix;

        if (midN > 0 && midM > 0)
        {
            if ((long)midN * midM <= 4_000_000)
            {
                int[,] dp = new int[midN + 1, midM + 1];
                for (int i = midN - 1; i >= 0; i--)
                {
                    for (int j = midM - 1; j >= 0; j--)
                    {
                        if (string.Equals(left[prefix + i], right[prefix + j], StringComparison.Ordinal))
                            dp[i, j] = dp[i + 1, j + 1] + 1;
                        else
                            dp[i, j] = Math.Max(dp[i + 1, j], dp[i, j + 1]);
                    }
                }

                int x = 0, y = 0;
                while (x < midN && y < midM)
                {
                    if (string.Equals(left[prefix + x], right[prefix + y], StringComparison.Ordinal))
                    {
                        output.Add(new DiffLine(DiffLineKind.Unchanged, left[prefix + x]));
                        x++;
                        y++;
                    }
                    else if (dp[x + 1, y] >= dp[x, y + 1])
                    {
                        output.Add(new DiffLine(DiffLineKind.Removed, left[prefix + x]));
                        x++;
                    }
                    else
                    {
                        output.Add(new DiffLine(DiffLineKind.Added, right[prefix + y]));
                        y++;
                    }
                }
                while (x < midN) output.Add(new DiffLine(DiffLineKind.Removed, left[prefix + x++]));
                while (y < midM) output.Add(new DiffLine(DiffLineKind.Added, right[prefix + y++]));
            }
            else
            {
                for (int i = 0; i < midN; i++) output.Add(new DiffLine(DiffLineKind.Removed, left[prefix + i]));
                for (int j = 0; j < midM; j++) output.Add(new DiffLine(DiffLineKind.Added, right[prefix + j]));
            }
        }
        else
        {
            for (int i = 0; i < midN; i++) output.Add(new DiffLine(DiffLineKind.Removed, left[prefix + i]));
            for (int j = 0; j < midM; j++) output.Add(new DiffLine(DiffLineKind.Added, right[prefix + j]));
        }

        for (int i = n - suffix; i < n; i++)
        {
            output.Add(new DiffLine(DiffLineKind.Unchanged, left[i]));
        }

        return output;
    }

    private static List<string> SplitNormalizedLines(string text)
    {
        return text
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }
}
