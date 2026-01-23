using System.Runtime.CompilerServices;
using Sa.Extensions;

namespace Sa.Classes;


/// <summary>
/// Provides methods for calculating string similarity using Damerau-Levenshtein distance algorithm.
/// Includes optimizations for performance and memory efficiency.
/// https://programm.top/c-sharp/algorithm/damerau-levenshtein-distance/
/// </summary>
internal static class Levenshtein
{
    /// <summary>
    /// Compares the two values to find the minimum Damerau-Levenshtein distance. 
    /// Thread safe and memory efficient.
    /// </summary>
    /// <param name="value1">First string to compare</param>
    /// <param name="value2">Second string to compare</param>
    /// <returns>Difference. 0 indicates complete match.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Distance(string? value1, string? value2)
    {
        if (value1 == null) return value2?.Length ?? 0;
        if (value2 == null) return value1.Length;

        // Quick checks for common cases
        if (value1 == value2) return 0;
        if (value1.Length == 0) return value2.Length;
        if (value2.Length == 0) return value1.Length;


        return CalculateDistance(value1, value2);
    }

    private static int CalculateDistance(ReadOnlySpan<char> firstText, ReadOnlySpan<char> secondText)
    {
        var n = firstText.Length + 1;
        var m = secondText.Length + 1;

        if (n == 1) return m - 1;
        if (m == 1) return n - 1;

        // Для хранения двух предыдущих строк
        Span<int> previousPreviousRow = stackalloc int[m];
        Span<int> previousRow = stackalloc int[m];
        Span<int> currentRow = stackalloc int[m];

        // Инициализация
        for (var j = 0; j < m; j++)
        {
            previousRow[j] = j;
        }

        for (var i = 1; i < n; i++)
        {
            currentRow[0] = i;

            for (var j = 1; j < m; j++)
            {
                var cost = firstText[i - 1] == secondText[j - 1] ? 0 : 1;

                currentRow[j] = Minimum(
                    previousRow[j] + 1,          // удаление
                    currentRow[j - 1] + 1,       // вставка
                    previousRow[j - 1] + cost);  // замена

                // Исправленное условие перестановки
                if (i > 1 && j > 1
                    && firstText[i - 1] == secondText[j - 2]
                    && firstText[i - 2] == secondText[j - 1])
                {
                    currentRow[j] = Math.Min(currentRow[j], previousPreviousRow[j - 2] + 1);
                }
            }

            // Сдвигаем строки для следующей итерации
            var tmp = previousPreviousRow;
            previousPreviousRow = previousRow;
            previousRow = currentRow;
            currentRow = tmp;
        }

        return previousRow[m - 1];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Minimum(int a, int b, int c)
    {
        var min = a;
        if (b < min) min = b;
        if (c < min) min = c;
        return min;
    }


    /// <summary>
    /// Calculates similarity percentage between two strings (0.0 - 1.0)
    /// </summary>
    /// <param name="value1">First string to compare</param>
    /// <param name="value2">Second string to compare</param>
    /// <returns>Similarity percentage where 1.0 is exact match</returns>
    public static double GetSimilarity(string? value1, string? value2)
    {
        if (value1 == null || value2 == null)
            return 0.0;
        if (value1.Length == 0 && value2.Length == 0)
            return 1.0;
        if (value1.Length == 0 || value2.Length == 0)
            return 0.0;

        int distance = Distance(value1, value2);
        int maxLength = Math.Max(value1.Length, value2.Length);

        return maxLength == 0 ? 1.0 : 1.0 - (double)distance / maxLength;
    }

    /// <summary>
    /// Checks if two strings are similar within the specified threshold
    /// </summary>
    /// <param name="value1">First string to compare</param>
    /// <param name="value2">Second string to compare</param>
    /// <param name="threshold">Similarity threshold (0.0 - 1.0)</param>
    /// <returns>True if strings are similar within threshold</returns>
    public static bool IsSimilar(string? value1, string? value2, double threshold = 0.8)
    {
        return GetSimilarity(value1, value2) >= threshold;
    }




    public static class Matcher
    {
        public record MatchResult<T>(T TargetObject, double Similarity, string? TargetString);

        public static IEnumerable<MatchResult<T>> FindMatches<T>(
            string? source,
            IEnumerable<T> targetObjects,
            Func<T, string?> targetStringSelector,
            double similarityThreshold = 0.8,
            bool isNormalize = true)
        {

            string? sourceString = isNormalize && source is not null
                ? source.Trim().NormalizeWhiteSpace().ToLower()
                : source;

            foreach (T? target in targetObjects)
            {
                string? targetString = targetStringSelector(target);

                if (isNormalize && targetString is not null)
                    targetString = targetString.Trim().NormalizeWhiteSpace().ToLower();

                var similarity = GetSimilarity(sourceString, targetString);

                if (similarity >= similarityThreshold)
                {
                    yield return (new MatchResult<T>(target, similarity, targetString));
                }
            }
        }


        /// <summary>
        /// Finds the best matching object with the highest similarity score
        /// </summary>
        /// <param name="source">Source string to match</param>
        /// <param name="targetObjects">Collection of target objects to match against</param>
        /// <param name="targetStringSelector">Function to extract string from target objects</param>
        /// <param name="similarityThreshold">Minimum similarity threshold (0.0 - 1.0)</param>
        /// <returns>Best match result or null if no matches found above threshold</returns>
        public static MatchResult<T>? FindBestMatch<T>(
            string? source,
            IEnumerable<T> targetObjects,
            Func<T, string?> targetStringSelector,
            double similarityThreshold = 0.8,
            bool isNormalize = true)
        {

            return FindBestMatch(FindMatches(source, targetObjects, targetStringSelector, similarityThreshold, isNormalize));
        }

        /// <summary>
        /// Finds the best matching object with the highest similarity score from pre-filtered matches
        /// </summary>
        /// <param name="matches">Pre-filtered collection of match results</param>
        /// <returns>Best match result or null if collection is empty</returns>
        public static MatchResult<T>? FindBestMatch<T>(IEnumerable<MatchResult<T>> matches)
        {
            if (matches == null)
                return null;

            MatchResult<T>? bestMatch = null;
            double bestSimilarity = -1.0;

            foreach (var match in matches)
            {
                if (match.Similarity > bestSimilarity)
                {
                    bestSimilarity = match.Similarity;
                    bestMatch = match;

                    // Early exit if perfect match found
                    if (Math.Abs(match.Similarity - 1.0) < double.Epsilon)
                        break;
                }
            }

            return bestSimilarity >= 0 ? bestMatch : null;
        }

        /// <summary>
        /// Finds the best match from a collection of strings
        /// </summary>
        /// <param name="value">Source string to match</param>
        /// <param name="candidates">Candidate strings to match against</param>
        /// <returns>Best matching string and its distance</returns>
        public static (string? bestMatch, int distance) FindBestMatch(string? value, params string?[]? candidates)
        {
            if (string.IsNullOrEmpty(value) || candidates == null || candidates.Length == 0)
                return (null, int.MaxValue);

            string? bestMatch = null;
            int bestDistance = int.MaxValue;
            int valueLength = value.Length;

            var candidatesSpan = candidates.AsSpan();

            for (int i = 0; i < candidatesSpan.Length; i++)
            {
                string? candidate = candidatesSpan[i];
                if (candidate is not { Length: > 0 }) continue;

                // Быстрая предварительная фильтрация
                int lengthDiff = Math.Abs(valueLength - candidate.Length);
                if (lengthDiff > bestDistance) continue;

                int distance = Distance(value, candidate);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestMatch = candidate;

                    if (bestDistance == 0)
                        break;
                }
            }

            return (bestMatch, bestDistance);
        }
    }
}
