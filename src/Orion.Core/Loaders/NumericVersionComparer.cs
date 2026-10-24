namespace Orion.Core.Loaders;

/// <summary>
/// Compares version strings numerically where possible ("21.1.99" &gt; "21.1.9"), falling back
/// to a case-insensitive string comparison for non numeric segments ("21.1.264-beta").
/// Mod loader metadata is not consistently zero padded, so a plain string sort puts
/// 21.1.99 above 21.1.264 — which would make the launcher offer the wrong "latest" version.
/// </summary>
public sealed class NumericVersionComparer : IComparer<string>
{
    public static NumericVersionComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        var left = Tokenize(x);
        var right = Tokenize(y);

        var max = Math.Max(left.Count, right.Count);
        for (var i = 0; i < max; i++)
        {
            var hasLeft = i < left.Count;
            var hasRight = i < right.Count;

            if (!hasLeft) return -1;
            if (!hasRight) return 1;

            var a = left[i];
            var b = right[i];

            int comparison;
            if (long.TryParse(a, out var numberA) && long.TryParse(b, out var numberB))
                comparison = numberA.CompareTo(numberB);
            else
                comparison = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);

            if (comparison != 0) return comparison;
        }

        return 0;
    }

    private static List<string> Tokenize(string value)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (var c in value)
        {
            if (c is '.' or '-' or '+' or '_')
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }
}
