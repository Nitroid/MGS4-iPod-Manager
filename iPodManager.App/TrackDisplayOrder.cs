namespace iPodManager;

internal static class TrackDisplayOrder
{
    internal static readonly IComparer<string> NaturalTitle = Comparer<string>.Create(CompareTitles);

    internal static IOrderedEnumerable<T> Album<T>(IEnumerable<T> tracks,
        Func<T, uint> disc, Func<T, uint> number, Func<T, string> title) =>
        tracks.OrderBy(track => number(track) == 0)
            .ThenBy(track => disc(track) == 0 ? 1u : disc(track))
            .ThenBy(number)
            .ThenBy(title, NaturalTitle);

    private static int Digit(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= '０' and <= '９' => value - '０',
        _ => -1
    };

    private static bool IsDigit(char value) => Digit(value) >= 0;

    private static int CompareTitles(string? left, string? right)
    {
        if (left is null || right is null)
            return StringComparer.CurrentCultureIgnoreCase.Compare(left, right);
        int i = 0, j = 0;
        while (i < left.Length && j < right.Length)
        {
            bool leftDigit = IsDigit(left[i]), rightDigit = IsDigit(right[j]);
            if (leftDigit != rightDigit)
                return StringComparer.CurrentCultureIgnoreCase.Compare(left[i..], right[j..]);

            int leftEnd = i, rightEnd = j;
            while (leftEnd < left.Length && IsDigit(left[leftEnd]) == leftDigit) leftEnd++;
            while (rightEnd < right.Length && IsDigit(right[rightEnd]) == rightDigit) rightEnd++;
            int comparison;
            if (leftDigit)
            {
                // Compare significant digit counts first, avoiding integer overflow.
                int leftStart = i, rightStart = j;
                while (leftStart < leftEnd && Digit(left[leftStart]) == 0) leftStart++;
                while (rightStart < rightEnd && Digit(right[rightStart]) == 0) rightStart++;
                comparison = (leftEnd - leftStart).CompareTo(rightEnd - rightStart);
                if (comparison == 0)
                    for (int offset = 0; offset < leftEnd - leftStart && comparison == 0; offset++)
                        comparison = Digit(left[leftStart + offset]).CompareTo(Digit(right[rightStart + offset]));
            }
            else
                comparison = StringComparer.CurrentCultureIgnoreCase.Compare(
                    left[i..leftEnd], right[j..rightEnd]);
            if (comparison != 0) return comparison;
            i = leftEnd;
            j = rightEnd;
        }
        return (left.Length - i).CompareTo(right.Length - j);
    }
}
