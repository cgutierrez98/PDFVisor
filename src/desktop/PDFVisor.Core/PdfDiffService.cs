using PDFVisor.Interop;

namespace PDFVisor.Core;

public enum PageDiffStatus
{
    Identical,
    Modified,
    OnlyInLeft,
    OnlyInRight
}

public sealed record PageDiffItem(
    int PageNumber,
    PageDiffStatus Status,
    IReadOnlyList<string> RemovedLines,
    IReadOnlyList<string> AddedLines)
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
                results.Add(new PageDiffItem(i + 1, PageDiffStatus.OnlyInLeft, lines, Array.Empty<string>()));
                continue;
            }
            if (!hasLeft && hasRight)
            {
                var lines = SplitNormalizedLines(rightDoc.ExtractPageText(i));
                results.Add(new PageDiffItem(i + 1, PageDiffStatus.OnlyInRight, Array.Empty<string>(), lines));
                continue;
            }

            var leftLines = SplitNormalizedLines(leftDoc.ExtractPageText(i));
            var rightLines = SplitNormalizedLines(rightDoc.ExtractPageText(i));

            var leftSet = new HashSet<string>(leftLines, StringComparer.Ordinal);
            var rightSet = new HashSet<string>(rightLines, StringComparer.Ordinal);

            var removed = leftLines.Where(l => !rightSet.Contains(l)).ToList();
            var added = rightLines.Where(l => !leftSet.Contains(l)).ToList();

            var status = (removed.Count == 0 && added.Count == 0)
                ? PageDiffStatus.Identical
                : PageDiffStatus.Modified;

            results.Add(new PageDiffItem(i + 1, status, removed, added));
        }

        return results;
    }

    private static List<string> SplitNormalizedLines(string text)
    {
        return text
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }
}
