using System.Text;

namespace DCx.Core.LocalModels;

public sealed record LocalBenchmarkResourceLimits(
    TimeSpan MaximumElapsed,
    long MaximumPeakWorkingSetBytes,
    long? MaximumPeakGpuBytes = null,
    bool RequireUnloadConfirmation = true);

public sealed record LocalBenchmarkResourceObservation(
    TimeSpan Elapsed,
    long? PeakWorkingSetBytes,
    long? PeakGpuBytes,
    bool UnloadConfirmed);

public sealed record LocalBenchmarkResourceResult(
    bool Passed,
    long ElapsedMilliseconds,
    long? PeakWorkingSetBytes,
    long? PeakGpuBytes,
    bool UnloadConfirmed,
    IReadOnlyList<string> FailureCodes);

public static class LocalBenchmarkResourceEvaluator
{
    public static LocalBenchmarkResourceResult Evaluate(
        LocalBenchmarkResourceObservation observation,
        LocalBenchmarkResourceLimits limits)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ValidateLimits(limits);
        ValidateObservation(observation);

        var failures = new List<string>();
        if (observation.Elapsed > limits.MaximumElapsed)
        {
            failures.Add("elapsed_limit_exceeded");
        }

        if (observation.PeakWorkingSetBytes is null)
        {
            failures.Add("working_set_measurement_missing");
        }
        else if (observation.PeakWorkingSetBytes > limits.MaximumPeakWorkingSetBytes)
        {
            failures.Add("working_set_limit_exceeded");
        }

        if (limits.MaximumPeakGpuBytes is not null)
        {
            if (observation.PeakGpuBytes is null)
            {
                failures.Add("gpu_measurement_missing");
            }
            else if (observation.PeakGpuBytes > limits.MaximumPeakGpuBytes)
            {
                failures.Add("gpu_limit_exceeded");
            }
        }

        if (limits.RequireUnloadConfirmation && !observation.UnloadConfirmed)
        {
            failures.Add("unload_confirmation_missing");
        }

        return new LocalBenchmarkResourceResult(
            failures.Count == 0,
            checked((long)observation.Elapsed.TotalMilliseconds),
            observation.PeakWorkingSetBytes,
            observation.PeakGpuBytes,
            observation.UnloadConfirmed,
            failures.AsReadOnly());
    }

    internal static void ValidateObservation(LocalBenchmarkResourceObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.Elapsed < TimeSpan.Zero || observation.Elapsed > TimeSpan.FromDays(1) ||
            observation.PeakWorkingSetBytes is <= 0 || observation.PeakGpuBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(observation), "Local benchmark resource measurements are invalid.");
        }
    }

    internal static void ValidateLimits(LocalBenchmarkResourceLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaximumElapsed <= TimeSpan.Zero || limits.MaximumElapsed > TimeSpan.FromHours(12) ||
            limits.MaximumPeakWorkingSetBytes <= 0 || limits.MaximumPeakGpuBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "Local benchmark resource limits are invalid.");
        }
    }
}

public sealed record TranscriptionAccuracySample(
    string Id,
    string ExpectedText,
    string ActualText,
    LocalBenchmarkResourceObservation Resources);

public sealed record TranscriptionAccuracyLimits(
    double MaximumWordErrorRate,
    double MaximumCharacterErrorRate,
    LocalBenchmarkResourceLimits Resources);

public sealed record TranscriptionAccuracyResult(
    bool Passed,
    int FixtureCount,
    int ReferenceWordCount,
    int Substitutions,
    int Deletions,
    int Insertions,
    double WordErrorRate,
    int ReferenceCharacterCount,
    int CharacterErrors,
    double CharacterErrorRate,
    LocalBenchmarkResourceResult Resources,
    IReadOnlyList<string> FailureCodes);

public static class TranscriptionAccuracyEvaluator
{
    private const int MaximumFixtures = 32;
    private const int MaximumTextCharacters = 16 * 1024;
    private const long MaximumEditDistanceCells = 16_000_000;

    public static TranscriptionAccuracyResult Evaluate(
        IReadOnlyList<TranscriptionAccuracySample> samples,
        TranscriptionAccuracyLimits limits)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(limits);
        if (samples.Count is 0 or > MaximumFixtures)
        {
            throw new ArgumentException("A transcription benchmark requires 1 to 32 fixtures.", nameof(samples));
        }

        if (!double.IsFinite(limits.MaximumWordErrorRate) || limits.MaximumWordErrorRate is < 0 or > 1 ||
            !double.IsFinite(limits.MaximumCharacterErrorRate) || limits.MaximumCharacterErrorRate is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "Transcription accuracy limits must be finite ratios from zero through one.");
        }
        LocalBenchmarkResourceEvaluator.ValidateLimits(limits.Resources);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var wordEdits = default(EditCounts);
        var characterErrors = 0;
        var referenceWords = 0;
        var referenceCharacters = 0;
        long editDistanceCells = 0;
        foreach (var sample in samples)
        {
            ArgumentNullException.ThrowIfNull(sample);
            ValidateId(sample.Id, nameof(samples));
            if (!ids.Add(sample.Id))
            {
                throw new ArgumentException("Transcription fixture identifiers must be unique.", nameof(samples));
            }
            ValidateText(sample.ExpectedText, allowEmpty: false, nameof(samples));
            ValidateText(sample.ActualText, allowEmpty: true, nameof(samples));
            ArgumentNullException.ThrowIfNull(sample.Resources);
            LocalBenchmarkResourceEvaluator.ValidateObservation(sample.Resources);

            var expectedWords = NormalizeWords(sample.ExpectedText);
            var actualWords = NormalizeWords(sample.ActualText);
            if (expectedWords.Count == 0)
            {
                throw new ArgumentException("Each transcription reference must contain a letter or digit.", nameof(samples));
            }
            editDistanceCells = AddEditDistanceCells(editDistanceCells, expectedWords.Count, actualWords.Count, nameof(samples));
            var edits = ComputeEdits(expectedWords, actualWords);
            wordEdits += edits;
            referenceWords = checked(referenceWords + expectedWords.Count);

            var expectedCharacters = NormalizeCharacters(sample.ExpectedText);
            var actualCharacters = NormalizeCharacters(sample.ActualText);
            editDistanceCells = AddEditDistanceCells(
                editDistanceCells,
                expectedCharacters.Count,
                actualCharacters.Count,
                nameof(samples));
            var characterEdits = ComputeEdits(expectedCharacters, actualCharacters);
            characterErrors = checked(characterErrors + characterEdits.Total);
            referenceCharacters = checked(referenceCharacters + expectedCharacters.Count);
        }

        var aggregateResources = AggregateResources(samples.Select(sample => sample.Resources).ToArray());
        var resourceResult = LocalBenchmarkResourceEvaluator.Evaluate(aggregateResources, limits.Resources);
        var wordErrorRate = wordEdits.Total / (double)referenceWords;
        var characterErrorRate = characterErrors / (double)referenceCharacters;
        var failures = new List<string>();
        if (wordErrorRate > limits.MaximumWordErrorRate)
        {
            failures.Add("word_error_rate_exceeded");
        }
        if (characterErrorRate > limits.MaximumCharacterErrorRate)
        {
            failures.Add("character_error_rate_exceeded");
        }
        failures.AddRange(resourceResult.FailureCodes);

        return new TranscriptionAccuracyResult(
            failures.Count == 0,
            samples.Count,
            referenceWords,
            wordEdits.Substitutions,
            wordEdits.Deletions,
            wordEdits.Insertions,
            wordErrorRate,
            referenceCharacters,
            characterErrors,
            characterErrorRate,
            resourceResult,
            failures.AsReadOnly());
    }

    private static IReadOnlyList<string> NormalizeWords(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var rune in text.Normalize(NormalizationForm.FormKC).ToLowerInvariant().EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                current.Append(rune.ToString());
            }
            else if (rune.Value is not ('\'' or 0x2019) || current.Length == 0)
            {
                FlushWord(words, current);
            }
        }
        FlushWord(words, current);
        return words;
    }

    private static IReadOnlyList<string> NormalizeCharacters(string text) =>
        text.Normalize(NormalizationForm.FormKC)
            .ToLowerInvariant()
            .EnumerateRunes()
            .Where(Rune.IsLetterOrDigit)
            .Select(rune => rune.ToString())
            .ToArray();

    private static void FlushWord(List<string> words, StringBuilder current)
    {
        if (current.Length == 0) return;
        words.Add(current.ToString());
        current.Clear();
    }

    private static LocalBenchmarkResourceObservation AggregateResources(
        IReadOnlyList<LocalBenchmarkResourceObservation> observations)
    {
        var workingSetComplete = observations.All(item => item.PeakWorkingSetBytes is not null);
        var gpuComplete = observations.All(item => item.PeakGpuBytes is not null);
        return new LocalBenchmarkResourceObservation(
            observations.Max(item => item.Elapsed),
            workingSetComplete ? observations.Max(item => item.PeakWorkingSetBytes) : null,
            gpuComplete ? observations.Max(item => item.PeakGpuBytes) : null,
            observations.All(item => item.UnloadConfirmed));
    }

    private static void ValidateText(string? text, bool allowEmpty, string parameterName)
    {
        if (text is null || text.Length > MaximumTextCharacters || text.Contains('\0') ||
            text.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')) ||
            !allowEmpty && string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Transcription benchmark text is missing or outside its safety bounds.", parameterName);
        }
    }

    internal static void ValidateId(string? id, string parameterName)
    {
        if (id is not { Length: > 0 and <= 64 } || !IsAsciiLetterOrDigit(id[0]) ||
            id.Any(character => !IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')))
        {
            throw new ArgumentException("Benchmark identifiers must be bounded ASCII tokens.", parameterName);
        }
    }

    private static bool IsAsciiLetterOrDigit(char character) =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    private static EditCounts ComputeEdits(IReadOnlyList<string> reference, IReadOnlyList<string> hypothesis)
    {
        var previous = new EditCounts[hypothesis.Count + 1];
        var current = new EditCounts[hypothesis.Count + 1];
        for (var column = 1; column <= hypothesis.Count; column++)
        {
            previous[column] = previous[column - 1] with { Insertions = previous[column - 1].Insertions + 1 };
        }

        for (var row = 1; row <= reference.Count; row++)
        {
            current[0] = previous[0] with { Deletions = previous[0].Deletions + 1 };
            for (var column = 1; column <= hypothesis.Count; column++)
            {
                if (string.Equals(reference[row - 1], hypothesis[column - 1], StringComparison.Ordinal))
                {
                    current[column] = previous[column - 1];
                    continue;
                }

                var substitution = previous[column - 1] with
                {
                    Substitutions = previous[column - 1].Substitutions + 1,
                };
                var deletion = previous[column] with { Deletions = previous[column].Deletions + 1 };
                var insertion = current[column - 1] with { Insertions = current[column - 1].Insertions + 1 };
                current[column] = Best(substitution, deletion, insertion);
            }
            (previous, current) = (current, previous);
        }
        return previous[hypothesis.Count];
    }

    private static EditCounts Best(EditCounts first, EditCounts second, EditCounts third) =>
        IsBetter(second, first)
            ? IsBetter(third, second) ? third : second
            : IsBetter(third, first) ? third : first;

    private static bool IsBetter(EditCounts candidate, EditCounts current) =>
        candidate.Total < current.Total ||
        candidate.Total == current.Total && candidate.Substitutions < current.Substitutions ||
        candidate.Total == current.Total && candidate.Substitutions == current.Substitutions &&
        candidate.Deletions < current.Deletions ||
        candidate.Total == current.Total && candidate.Substitutions == current.Substitutions &&
        candidate.Deletions == current.Deletions && candidate.Insertions < current.Insertions;

    private static long AddEditDistanceCells(long current, int referenceCount, int hypothesisCount, string parameterName)
    {
        var next = checked(current + checked((long)(referenceCount + 1) * (hypothesisCount + 1)));
        if (next > MaximumEditDistanceCells)
        {
            throw new ArgumentException("Transcription benchmark edit-distance work exceeds its safety bound.", parameterName);
        }
        return next;
    }

    private readonly record struct EditCounts(int Substitutions, int Deletions, int Insertions)
    {
        public int Total => checked(Substitutions + Deletions + Insertions);

        public static EditCounts operator +(EditCounts left, EditCounts right) => new(
            checked(left.Substitutions + right.Substitutions),
            checked(left.Deletions + right.Deletions),
            checked(left.Insertions + right.Insertions));
    }
}

public sealed record EmbeddingRetrievalDocument(string Id, IReadOnlyList<float> Vector);

public sealed record EmbeddingRetrievalQuery(
    string Id,
    IReadOnlyList<float> Vector,
    IReadOnlyList<string> RelevantDocumentIds);

public sealed record EmbeddingRetrievalLimits(
    int K,
    double MinimumRecallAtK,
    double MinimumMeanReciprocalRank,
    LocalBenchmarkResourceLimits Resources);

public sealed record EmbeddingRetrievalResult(
    bool Passed,
    int DocumentCount,
    int QueryCount,
    int Dimensions,
    int K,
    int RelevantDocuments,
    int RelevantDocumentsRetrieved,
    double RecallAtK,
    double MeanReciprocalRank,
    LocalBenchmarkResourceResult Resources,
    IReadOnlyList<string> FailureCodes);

public static class EmbeddingRetrievalEvaluator
{
    private const int MaximumDocuments = 256;
    private const int MaximumQueries = 64;
    private const int MaximumDimensions = 8192;

    public static EmbeddingRetrievalResult Evaluate(
        IReadOnlyList<EmbeddingRetrievalDocument> documents,
        IReadOnlyList<EmbeddingRetrievalQuery> queries,
        LocalBenchmarkResourceObservation resources,
        EmbeddingRetrievalLimits limits)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(limits);
        if (documents.Count is 0 or > MaximumDocuments || queries.Count is 0 or > MaximumQueries)
        {
            throw new ArgumentException("Embedding benchmarks require bounded nonempty document and query sets.");
        }
        if (limits.K is <= 0 or > 32 || limits.K > documents.Count ||
            !double.IsFinite(limits.MinimumRecallAtK) || limits.MinimumRecallAtK is < 0 or > 1 ||
            !double.IsFinite(limits.MinimumMeanReciprocalRank) || limits.MinimumMeanReciprocalRank is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "Embedding retrieval limits are invalid.");
        }

        var documentIds = new HashSet<string>(StringComparer.Ordinal);
        var dimensions = -1;
        foreach (var document in documents)
        {
            ArgumentNullException.ThrowIfNull(document);
            TranscriptionAccuracyEvaluator.ValidateId(document.Id, nameof(documents));
            if (!documentIds.Add(document.Id))
            {
                throw new ArgumentException("Embedding document identifiers must be unique.", nameof(documents));
            }
            dimensions = ValidateVector(document.Vector, dimensions, nameof(documents));
        }

        var queryIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var query in queries)
        {
            ArgumentNullException.ThrowIfNull(query);
            TranscriptionAccuracyEvaluator.ValidateId(query.Id, nameof(queries));
            if (!queryIds.Add(query.Id))
            {
                throw new ArgumentException("Embedding query identifiers must be unique.", nameof(queries));
            }
            ValidateVector(query.Vector, dimensions, nameof(queries));
            if (query.RelevantDocumentIds is null || query.RelevantDocumentIds.Count is 0 or > MaximumDocuments ||
                query.RelevantDocumentIds.Distinct(StringComparer.Ordinal).Count() != query.RelevantDocumentIds.Count ||
                query.RelevantDocumentIds.Any(id => !documentIds.Contains(id)))
            {
                throw new ArgumentException("Embedding query relevance labels are missing, duplicated, or unknown.", nameof(queries));
            }
        }

        var relevantTotal = 0;
        var relevantRetrieved = 0;
        double reciprocalRankTotal = 0;
        foreach (var query in queries)
        {
            var relevant = query.RelevantDocumentIds.ToHashSet(StringComparer.Ordinal);
            var ranked = documents
                .Select(document => new
                {
                    document.Id,
                    Similarity = CosineSimilarity(query.Vector, document.Vector),
                })
                .OrderByDescending(item => item.Similarity)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .ToArray();
            relevantTotal = checked(relevantTotal + relevant.Count);
            relevantRetrieved = checked(relevantRetrieved + ranked.Take(limits.K).Count(item => relevant.Contains(item.Id)));
            var firstRelevant = Array.FindIndex(ranked, item => relevant.Contains(item.Id));
            if (firstRelevant >= 0)
            {
                reciprocalRankTotal += 1d / (firstRelevant + 1);
            }
        }

        var recall = relevantRetrieved / (double)relevantTotal;
        var meanReciprocalRank = reciprocalRankTotal / queries.Count;
        var resourceResult = LocalBenchmarkResourceEvaluator.Evaluate(resources, limits.Resources);
        var failures = new List<string>();
        if (recall < limits.MinimumRecallAtK)
        {
            failures.Add("recall_at_k_below_minimum");
        }
        if (meanReciprocalRank < limits.MinimumMeanReciprocalRank)
        {
            failures.Add("mean_reciprocal_rank_below_minimum");
        }
        failures.AddRange(resourceResult.FailureCodes);

        return new EmbeddingRetrievalResult(
            failures.Count == 0,
            documents.Count,
            queries.Count,
            dimensions,
            limits.K,
            relevantTotal,
            relevantRetrieved,
            recall,
            meanReciprocalRank,
            resourceResult,
            failures.AsReadOnly());
    }

    private static int ValidateVector(IReadOnlyList<float>? vector, int expectedDimensions, string parameterName)
    {
        if (vector is null || vector.Count is <= 0 or > MaximumDimensions ||
            expectedDimensions >= 0 && vector.Count != expectedDimensions ||
            vector.Any(value => !float.IsFinite(value)))
        {
            throw new ArgumentException("Embedding vectors must be finite and consistently dimensioned.", parameterName);
        }
        double magnitude = 0;
        foreach (var value in vector)
        {
            magnitude += (double)value * value;
        }
        if (!double.IsFinite(magnitude) || magnitude <= 0)
        {
            throw new ArgumentException("Embedding vectors must have a finite nonzero magnitude.", parameterName);
        }
        return expectedDimensions < 0 ? vector.Count : expectedDimensions;
    }

    private static double CosineSimilarity(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        double dot = 0;
        double leftMagnitude = 0;
        double rightMagnitude = 0;
        for (var index = 0; index < left.Count; index++)
        {
            dot += (double)left[index] * right[index];
            leftMagnitude += (double)left[index] * left[index];
            rightMagnitude += (double)right[index] * right[index];
        }
        return dot / Math.Sqrt(leftMagnitude * rightMagnitude);
    }
}
