using System.Text.Json;
using DCx.Core.LocalModels;

namespace DCx.Core.Tests.LocalModels;

public sealed class LocalModelBenchmarkEvaluationTests
{
    [Fact]
    public void TranscriptionAccuracy_NormalizesTextAndReturnsAggregateOnlyEvidence()
    {
        var result = TranscriptionAccuracyEvaluator.Evaluate(
            [new("fixture-one", "Hello, DCx 42!", "hello dcx 42", GoodResources())],
            new TranscriptionAccuracyLimits(0, 0, ResourceLimits()));

        Assert.True(result.Passed);
        Assert.Equal(3, result.ReferenceWordCount);
        Assert.Equal(0, result.Substitutions + result.Deletions + result.Insertions);
        Assert.Equal(0, result.WordErrorRate);
        Assert.Equal(0, result.CharacterErrorRate);
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("fixture-one", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Hello", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void TranscriptionAccuracy_ComputesErrorsAndFailsClosedOnQualityOrResourceLimits()
    {
        var result = TranscriptionAccuracyEvaluator.Evaluate(
            [new(
                "fixture-two",
                "alpha beta gamma",
                "alpha delta",
                new LocalBenchmarkResourceObservation(TimeSpan.FromSeconds(3), null, null, false))],
            new TranscriptionAccuracyLimits(
                0.5,
                0.2,
                new LocalBenchmarkResourceLimits(TimeSpan.FromSeconds(2), 100, 50)));

        Assert.False(result.Passed);
        Assert.Equal(1, result.Substitutions);
        Assert.Equal(1, result.Deletions);
        Assert.Equal(2d / 3d, result.WordErrorRate, 10);
        Assert.Contains("word_error_rate_exceeded", result.FailureCodes);
        Assert.Contains("character_error_rate_exceeded", result.FailureCodes);
        Assert.Contains("elapsed_limit_exceeded", result.FailureCodes);
        Assert.Contains("working_set_measurement_missing", result.FailureCodes);
        Assert.Contains("gpu_measurement_missing", result.FailureCodes);
        Assert.Contains("unload_confirmation_missing", result.FailureCodes);
    }

    [Fact]
    public void TranscriptionAccuracy_EmptyHypothesisCountsAsDeletions()
    {
        var result = TranscriptionAccuracyEvaluator.Evaluate(
            [new("empty-output", "one two", string.Empty, GoodResources())],
            new TranscriptionAccuracyLimits(1, 1, ResourceLimits()));

        Assert.True(result.Passed);
        Assert.Equal(2, result.Deletions);
        Assert.Equal(1, result.WordErrorRate);
        Assert.Equal(1, result.CharacterErrorRate);
    }

    [Fact]
    public void TranscriptionAccuracy_RejectsMalformedOrAmbiguousFixtures()
    {
        var duplicate = new TranscriptionAccuracySample("same", "one", "one", GoodResources());
        Assert.Throws<ArgumentException>(() => TranscriptionAccuracyEvaluator.Evaluate(
            [duplicate, duplicate],
            new TranscriptionAccuracyLimits(1, 1, ResourceLimits())));
        Assert.Throws<ArgumentException>(() => TranscriptionAccuracyEvaluator.Evaluate(
            [new("bad id", "one", "one", GoodResources())],
            new TranscriptionAccuracyLimits(1, 1, ResourceLimits())));
        Assert.Throws<ArgumentException>(() => TranscriptionAccuracyEvaluator.Evaluate(
            [new("control", "one", "one\u0001", GoodResources())],
            new TranscriptionAccuracyLimits(1, 1, ResourceLimits())));
        Assert.Throws<ArgumentOutOfRangeException>(() => TranscriptionAccuracyEvaluator.Evaluate(
            [duplicate],
            new TranscriptionAccuracyLimits(double.NaN, 1, ResourceLimits())));
        Assert.Throws<ArgumentException>(() => TranscriptionAccuracyEvaluator.Evaluate(
            [new("too-expensive", new string('a', 5_000), new string('a', 5_000), GoodResources())],
            new TranscriptionAccuracyLimits(1, 1, ResourceLimits())));
    }

    [Fact]
    public void ResourceEvaluation_EnforcesWorkingSetGpuElapsedAndUnloadLimits()
    {
        var result = LocalBenchmarkResourceEvaluator.Evaluate(
            new LocalBenchmarkResourceObservation(TimeSpan.FromSeconds(2), 201, 101, false),
            new LocalBenchmarkResourceLimits(TimeSpan.FromSeconds(1), 200, 100));

        Assert.False(result.Passed);
        Assert.Equal(
            [
                "elapsed_limit_exceeded",
                "working_set_limit_exceeded",
                "gpu_limit_exceeded",
                "unload_confirmation_missing",
            ],
            result.FailureCodes);
        Assert.Throws<ArgumentOutOfRangeException>(() => LocalBenchmarkResourceEvaluator.Evaluate(
            new LocalBenchmarkResourceObservation(TimeSpan.Zero, 100, null, true),
            new LocalBenchmarkResourceLimits(TimeSpan.Zero, 100)));
        Assert.Throws<ArgumentOutOfRangeException>(() => LocalBenchmarkResourceEvaluator.Evaluate(
            new LocalBenchmarkResourceObservation(TimeSpan.Zero, -1, null, true),
            ResourceLimits()));
    }

    [Fact]
    public void EmbeddingRetrieval_ComputesRecallAndMrrWithoutReturningVectors()
    {
        var result = EmbeddingRetrievalEvaluator.Evaluate(
            [
                new("cat", [1f, 0f]),
                new("dog", [0f, 1f]),
            ],
            [new("cat-query", [1f, 0.1f], ["cat"])],
            GoodResources(),
            new EmbeddingRetrievalLimits(1, 1, 1, ResourceLimits()));

        Assert.True(result.Passed);
        Assert.Equal(1, result.RelevantDocumentsRetrieved);
        Assert.Equal(1, result.RecallAtK);
        Assert.Equal(1, result.MeanReciprocalRank);
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("cat-query", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Vector", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddingRetrieval_UsesStableTieOrderAndFailsQualityThresholds()
    {
        var result = EmbeddingRetrievalEvaluator.Evaluate(
            [
                new("a", [1f, 0f]),
                new("b", [1f, 0f]),
            ],
            [new("tie-query", [1f, 0f], ["b"])],
            GoodResources(),
            new EmbeddingRetrievalLimits(1, 1, 1, ResourceLimits()));

        Assert.False(result.Passed);
        Assert.Equal(0, result.RecallAtK);
        Assert.Equal(0.5, result.MeanReciprocalRank);
        Assert.Contains("recall_at_k_below_minimum", result.FailureCodes);
        Assert.Contains("mean_reciprocal_rank_below_minimum", result.FailureCodes);
    }

    [Fact]
    public void EmbeddingRetrieval_RejectsMalformedVectorsAndRelevanceLabels()
    {
        var documents = new EmbeddingRetrievalDocument[]
        {
            new("a", [1f, 0f]),
            new("b", [0f, 1f]),
        };
        var limits = new EmbeddingRetrievalLimits(1, 0, 0, ResourceLimits());

        Assert.Throws<ArgumentException>(() => EmbeddingRetrievalEvaluator.Evaluate(
            [new("zero", [0f, 0f])],
            [new("query", [1f, 0f], ["zero"])],
            GoodResources(),
            limits));
        Assert.Throws<ArgumentException>(() => EmbeddingRetrievalEvaluator.Evaluate(
            documents,
            [new("query", [float.NaN, 0f], ["a"])],
            GoodResources(),
            limits));
        Assert.Throws<ArgumentException>(() => EmbeddingRetrievalEvaluator.Evaluate(
            documents,
            [new("query", [1f], ["a"])],
            GoodResources(),
            limits));
        Assert.Throws<ArgumentException>(() => EmbeddingRetrievalEvaluator.Evaluate(
            documents,
            [new("query", [1f, 0f], ["unknown"])],
            GoodResources(),
            limits));
    }

    private static LocalBenchmarkResourceObservation GoodResources() =>
        new(TimeSpan.FromMilliseconds(250), 64 * 1024 * 1024, null, true);

    private static LocalBenchmarkResourceLimits ResourceLimits() =>
        new(TimeSpan.FromSeconds(1), 128 * 1024 * 1024);
}
