namespace PDFVisor.Core;

public static class PageRangeParser
{
    /// <summary>
    /// Parses 1-based range strings like "1-3, 5, 8-10" into 0-based page index groups.
    /// </summary>
    public static List<int[]> ParseGroups(string expression, int totalPages)
    {
        if (totalPages <= 0) throw new ArgumentOutOfRangeException(nameof(totalPages));
        if (string.IsNullOrWhiteSpace(expression))
            throw new ArgumentException("Introduce al menos un rango de páginas (ej. 1-3, 5).", nameof(expression));

        var groups = new List<int[]>();
        foreach (string part in expression.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Contains('-'))
            {
                var bounds = part.Split('-', StringSplitOptions.TrimEntries);
                if (bounds.Length != 2 || !int.TryParse(bounds[0], out int start) || !int.TryParse(bounds[1], out int end))
                    throw new FormatException($"Rango inválido: '{part}'.");
                if (start < 1 || end < start || end > totalPages)
                    throw new ArgumentOutOfRangeException(nameof(expression), $"Rango fuera de límites (1..{totalPages}): '{part}'.");

                var indices = new int[end - start + 1];
                for (int i = start; i <= end; i++) indices[i - start] = i - 1;
                groups.Add(indices);
            }
            else
            {
                if (!int.TryParse(part, out int page) || page < 1 || page > totalPages)
                    throw new FormatException($"Número de página inválido (1..{totalPages}): '{part}'.");
                groups.Add([page - 1]);
            }
        }
        return groups;
    }

    public static List<int[]> SplitEveryN(int totalPages, int pagesPerFile)
    {
        if (pagesPerFile <= 0) throw new ArgumentOutOfRangeException(nameof(pagesPerFile));
        var groups = new List<int[]>();
        for (int start = 0; start < totalPages; start += pagesPerFile)
        {
            int len = Math.Min(pagesPerFile, totalPages - start);
            var arr = new int[len];
            for (int i = 0; i < len; i++) arr[i] = start + i;
            groups.Add(arr);
        }
        return groups;
    }
}
