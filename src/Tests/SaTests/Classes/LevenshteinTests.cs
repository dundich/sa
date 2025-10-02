using Sa.Classes;


namespace SaTests.Classes;


public class LevenshteinTests
{
    public class DistanceTests
    {
        [Theory]
        [InlineData(null, null, 0)]
        [InlineData(null, "", 0)]
        [InlineData(null, "test", 4)]
        [InlineData("", null, 0)]
        [InlineData("test", null, 4)]
        [InlineData("", "", 0)]
        [InlineData("", "test", 4)]
        [InlineData("test", "", 4)]
        public void Distance_WithNullOrEmpty_ReturnsExpected(string? value1, string? value2, int expected)
        {
            // Act
            var result = Levenshtein.Distance(value1, value2);

            // Assert
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData("test", "test")]
        [InlineData("hello", "hello")]
        [InlineData("same", "same")]
        public void Distance_EqualStrings_ReturnsZero(string value1, string value2)
        {
            int expected = 0;
            // Act & Assert
            Assert.Equal(expected, Levenshtein.Distance(value1, value2));
        }

        [Theory]
        [InlineData("kitten", "sitting", 3)]
        [InlineData("saturday", "sunday", 3)]
        [InlineData("flaw", "lawn", 2)]
        [InlineData("cat", "cut", 1)]
        [InlineData("book", "back", 2)]
        public void Distance_DifferentStrings_ReturnsCorrectDistance(string value1, string value2, int expected)
        {
            // Act
            var result = Levenshtein.Distance(value1, value2);

            // Assert
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData("ab", "ba")] // Transposition
        [InlineData("abc", "bac")] // Transposition
        [InlineData("abcd", "bacd")] // Transposition
        public void Distance_WithTranspositions_ReturnsCorrectDistance(string value1, string value2)
        {
            int expected = 1;

            // Act
            var result = Levenshtein.Distance(value1, value2);

            // Assert
            Assert.Equal(expected, result);
        }

        [Fact]
        public void Distance_LongStrings_ReturnsCorrectDistance()
        {
            // Arrange
            var str1 = new string('a', 100) + "test" + new string('b', 100);
            var str2 = new string('a', 100) + "text" + new string('b', 100);

            // Act
            var result = Levenshtein.Distance(str1, str2);

            // Assert
            Assert.Equal(1, result); // Only one character difference
        }

        [Theory]
        [InlineData("testé", "TESTÉ", 5)] // Case sensitive
        [InlineData("café", "cafe", 1)] // Unicode characters
        public void Distance_CaseSensitiveAndUnicode_ReturnsCorrectDistance(string value1, string value2, int e)
        {
            var expected = e;

            // Act
            var result = Levenshtein.Distance(value1, value2);

            // Assert
            Assert.Equal(expected, result);
        }
    }

    public class GetSimilarityTests
    {
        [Theory]
        [InlineData(null, null, 0.0)]
        [InlineData(null, "test", 0.0)]
        [InlineData("test", null, 0.0)]
        [InlineData("", "", 1.0)]
        [InlineData("", "test", 0.0)]
        [InlineData("test", "", 0.0)]
        public void GetSimilarity_WithNullOrEmpty_ReturnsExpected(string? value1, string? value2, double expected)
        {
            // Act
            var result = Levenshtein.GetSimilarity(value1, value2);

            // Assert
            Assert.Equal(expected, result, 3);
        }

        [Theory]
        [InlineData("test", "test", 1.0)]
        [InlineData("hello", "hello", 1.0)]
        [InlineData("identical", "identical", 1.0)]
        public void GetSimilarity_EqualStrings_ReturnsOne(string value1, string value2, double expected)
        {
            // Act & Assert
            Assert.Equal(expected, Levenshtein.GetSimilarity(value1, value2), 3);
        }

        [Theory]
        [InlineData("kitten", "sitting", 0.571)] // 1 - 3/7
        [InlineData("cat", "cut", 0.667)] // 1 - 1/3
        [InlineData("a", "b", 0.0)] // 1 - 1/1
        [InlineData("ab", "cd", 0.0)] // 1 - 2/2
        public void GetSimilarity_DifferentStrings_ReturnsCorrectSimilarity(string value1, string value2, double expected)
        {
            // Act
            var result = Levenshtein.GetSimilarity(value1, value2);

            // Assert
            Assert.Equal(expected, result, 3);
        }

        [Fact]
        public void GetSimilarity_PerfectMatchDifferentLengths_ReturnsOne()
        {
            // Arrange
            var longer = "prefix" + "test";
            var shorter = "test";

            // Act
            var result = Levenshtein.GetSimilarity(longer, shorter);

            // Assert - Should not be 1.0 since they are different
            Assert.NotEqual(1.0, result, 3);
        }
    }

    public class IsSimilarTests
    {
        [Theory]
        [InlineData("test", "test", 0.8, true)]
        [InlineData("kitten", "sitting", 0.5, true)]
        [InlineData("kitten", "sitting", 0.6, false)]
        [InlineData("cat", "dog", 0.8, false)]
        [InlineData("hello", "hello", 1.0, true)]
        public void IsSimilar_WithThreshold_ReturnsExpected(string value1, string value2, double threshold, bool expected)
        {
            // Act
            var result = Levenshtein.IsSimilar(value1, value2, threshold);

            // Assert
            Assert.Equal(expected, result);
        }

        [Fact]
        public void IsSimilar_DefaultThreshold_ReturnsExpected()
        {
            // Arrange
            var similar = "hello world";
            var slightlyDifferent = "hello world!";
            var veryDifferent = "goodbye world";

            // Act & Assert
            Assert.True(Levenshtein.IsSimilar(similar, slightlyDifferent)); // Should be above 0.8
            Assert.False(Levenshtein.IsSimilar(similar, veryDifferent)); // Should be below 0.8
        }
    }

    public class MatcherTests
    {
        public record TestItem1(string Name, int Id);

        [Fact]
        public void FindMatches_WithNormalization_FindsMatches()
        {
            // Arrange
            var source = "  Hello   World  ";
            var items = new List<TestItem1>
            {
                new("hello world", 1),
                new("goodbye world", 2),
                new("HELLO WORLD", 3),
                new("hello  world", 4)
            };

            // Act
            var matches = Levenshtein.Matcher.FindMatches(
                source,
                items,
                x => x.Name,
                similarityThreshold: 0.8,
                isNormalize: true)
                .ToList();

            // Assert
            Assert.All(matches, m => Assert.True(m.Similarity >= 0.8));
            Assert.Contains(matches, m => m.TargetObject.Id == 1);
            Assert.Contains(matches, m => m.TargetObject.Id == 3);
            Assert.Contains(matches, m => m.TargetObject.Id == 4);
        }

        [Fact]
        public void FindMatches_WithoutNormalization_RespectsCase()
        {
            // Arrange
            var source = "Hello World";
            var items = new List<TestItem1>
            {
                new("hello world", 1),
                new("HELLO WORLD", 2),
                new("Hello World", 3)
            };

            // Act
            var matches = Levenshtein.Matcher.FindMatches(
                source,
                items,
                x => x.Name,
                similarityThreshold: 0.9,
                isNormalize: false)
                .ToList();

            // Assert - Only exact case match should have high similarity
            var exactMatch = matches.First(m => m.TargetObject.Id == 3);
            Assert.True(exactMatch.Similarity >= 0.9);
        }

        [Fact]
        public void FindMatches_NoMatches_ReturnsEmpty()
        {
            // Arrange
            var source = "completely different";
            var items = new List<TestItem1>
            {
                new("apple", 1),
                new("banana", 2),
                new("cherry", 3)
            };

            // Act
            var matches = Levenshtein.Matcher.FindMatches(
                source,
                items,
                x => x.Name,
                similarityThreshold: 0.8)
                .ToList();

            // Assert
            Assert.Empty(matches);
        }

        [Fact]
        public void FindBestMatch_FromMatches_ReturnsHighestSimilarity()
        {
            // Arrange
            var matches = new List<Levenshtein.Matcher.MatchResult<TestItem1>>
            {
                new(new TestItem1("test1", 1), 0.5, "test1"),
                new(new TestItem1("test2", 2), 0.9, "test2"),
                new(new TestItem1("test3", 3), 0.7, "test3")
            };

            // Act
            var bestMatch = Levenshtein.Matcher.FindBestMatch(matches);

            // Assert
            Assert.NotNull(bestMatch);
            Assert.Equal(2, bestMatch.TargetObject.Id);
            Assert.Equal(0.9, bestMatch.Similarity, 3);
        }

        [Fact]
        public void FindBestMatch_FromObjects_ReturnsBestMatch()
        {
            // Arrange
            var source = "hello world";
            var items = new List<TestItem1>
            {
                new("hello world", 1),      // Perfect match
                new("hello world!", 2),     // Close match
                new("goodbye", 3)           // Poor match
            };

            // Act
            var bestMatch = Levenshtein.Matcher.FindBestMatch(
                source,
                items,
                x => x.Name,
                similarityThreshold: 0.5);

            // Assert
            Assert.NotNull(bestMatch);
            Assert.Equal(1, bestMatch.TargetObject.Id); // Should pick perfect match
            Assert.Equal(1.0, bestMatch.Similarity, 3);
        }

        [Fact]
        public void FindBestMatch_StringArray_ReturnsBestMatch()
        {
            // Arrange
            var source = "kitten";
            var candidates = new[] { "sitting", "kitchen", "kitten", "mitten" };

            // Act
            var (bestMatch, distance) = Levenshtein.Matcher.FindBestMatch(source, candidates);

            // Assert
            Assert.Equal("kitten", bestMatch);
            Assert.Equal(0, distance);
        }

        [Fact]
        public void FindBestMatch_NoCandidates_ReturnsNull()
        {
            // Arrange
            var source = "test";
            string?[]? candidates = null;

            // Act
            var (bestMatch, distance) = Levenshtein.Matcher.FindBestMatch(source, candidates);

            // Assert
            Assert.Null(bestMatch);
            Assert.Equal(int.MaxValue, distance);
        }

        [Fact]
        public void FindBestMatch_EmptyCandidates_ReturnsNull()
        {
            // Arrange
            var source = "test";
            var candidates = Array.Empty<string>();

            // Act
            var (bestMatch, distance) = Levenshtein.Matcher.FindBestMatch(source, candidates);

            // Assert
            Assert.Null(bestMatch);
            Assert.Equal(int.MaxValue, distance);
        }

        [Fact]
        public void FindBestMatch_EarlyExitOnPerfectMatch()
        {
            // Arrange
            var source = "perfect";
            var candidates = new[] { "good", "better", "perfect", "best" };

            // Act
            var (bestMatch, distance) = Levenshtein.Matcher.FindBestMatch(source, candidates);

            // Assert
            Assert.Equal("perfect", bestMatch);
            Assert.Equal(0, distance);
        }
    }

    public class EdgeCaseTests
    {
        [Fact]
        public void Distance_WithWhitespaceDifferences_CalculatesCorrectly()
        {
            // Arrange
            var str1 = "hello world";
            var str2 = "hello  world"; // Extra space

            // Act
            var distance = Levenshtein.Distance(str1, str2);

            // Assert
            Assert.Equal(1, distance);
        }

        [Fact]
        public void GetSimilarity_SameLengthDifferentContent_ReturnsCorrectValue()
        {
            // Arrange
            var str1 = "abcd";
            var str2 = "wxyz";

            // Act
            var similarity = Levenshtein.GetSimilarity(str1, str2);

            // Assert
            Assert.Equal(0.0, similarity, 3); // Completely different
        }

        [Fact]
        public void Matcher_FindBestMatch_WithNullSource_ReturnsNull()
        {
            // Arrange
            var items = new List<TestItem>
            {
                new("test", 1)
            };

            // Act
            var result = Levenshtein.Matcher.FindBestMatch(
                null,
                items,
                x => x.Name);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void Matcher_FindBestMatch_WithEmptyCollection_ReturnsNull()
        {
            // Arrange
            var source = "test";
            var items = new List<TestItem>();

            // Act
            var result = Levenshtein.Matcher.FindBestMatch(
                source,
                items,
                x => x.Name);

            // Assert
            Assert.Null(result);
        }
    }

    // Helper record for tests
    public record TestItem(string Name, int Id);
}