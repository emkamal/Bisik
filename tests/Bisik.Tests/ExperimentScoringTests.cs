using System.Collections.Immutable;
using Bisik.Core;
using Bisik.Models;

namespace Bisik.Tests;

public class ExperimentScoringTests
{
    [Theory]
    [InlineData(0, 100)] [InlineData(5, 50)] [InlineData(15, 25)]
    public void SpeedHasStableScale(double time, double score) => Assert.Equal(score, ExperimentScoring.Speed(5, time));

    [Theory]
    [InlineData("HAPPI Happy", 50)] [InlineData("HAPPI happi", 50)] [InlineData("HAPPI", 100)]
    [InlineData("HAPPIEx", 0)] [InlineData("unrelated", 0)]
    public void CanonicalCaseAndAliasesAreCountedExactly(string output, double score)
    {
        var result = ExperimentScoring.Compare("HAPPI", output, [new("HAPPI", "Happy")]);
        Assert.Equal(score, result.Identifiers);
    }

    [Fact]
    public void LongestAliasWinsAndAmbiguityIsFlaggedWithoutDoubleCounting()
    {
        var result = ExperimentScoring.Compare("API", "fetch class data", [new("FetchClassData", "fetch class data"), new("Class", "class")]);
        Assert.Equal(1, result.Terms[0].Wrong);
        Assert.Equal(0, result.Terms[1].Wrong);
        var ambiguous = ExperimentScoring.Compare("API", "tool", [new("API", "tool"), new("Other", "tool")]);
        Assert.Single(ambiguous.Warnings);
        Assert.All(ambiguous.Terms, t => Assert.Equal(0, t.Wrong));
    }

    [Fact]
    public void WordingPreservesNegationQuantitiesAndDoesNotCorrectAliases()
    {
        Assert.Equal(0, ExperimentScoring.Compare("one", "two three four five", []).Wording);
        Assert.True(ExperimentScoring.Compare("not 12", "12", []).Wording < 100);
        Assert.True(ExperimentScoring.Compare("HAPPI", "Happy", [new("HAPPI", "Happy")]).Wording < 100);
        Assert.Throws<ArgumentException>(() => ExperimentScoring.Compare("...", "text", []));
        Assert.Null(ExperimentScoring.Compare("one", "one", []).Identifiers);
        Assert.Equal(100, ExperimentScoring.Compare("café", "cafe\u0301", []).Wording);
    }

    [Fact]
    public void MissingAndZeroWeightsDifferFromMeasuredZero()
    {
        var factors = new ExperimentFactors(0, null, null, null, 100);
        Assert.Null(ExperimentScoring.WeightedScore(factors, new()));
        Assert.Null(ExperimentScoring.WeightedScore(factors, new(0, 0, 0, 0, 0)));
        Assert.Equal(0, ExperimentScoring.WeightedScore(factors, new(100, 0, 0, 0, 0)));
        Assert.Equal(100, ExperimentScoring.WeightedScore(factors, new(0, 0, 0, 0, 100)));
    }

    [Fact]
    public void RepeatsUseSeparateFixedCohortsAndSemanticWarningsDoNotExclude()
    {
        var warning = new ExperimentWarning(ExperimentWarningCategory.Meaning, ExperimentSeverity.Major, "not", "is", "Negation lost");
        var candidate = ExperimentCandidate.Baseline with { Id = "candidate" };
        var trials = new[]
        {
            Trial(candidate, 1000, 100, new(80, 30, 100, [warning], "one")),
            Trial(candidate, 9000, 50, null),
            Trial(candidate, 5000, 0, new(80, 10, 80, [], "two")),
            new ExperimentTrial { Candidate = candidate, Generation = ExperimentGenerationStatus.Failed },
        }.ToImmutableArray();
        var run = new ExperimentRun(ExperimentModelTests.Definition(), new(Guid.NewGuid(), "raw", 0, null, "id"), ExperimentState.Completed, 1, trials);
        var row = Assert.Single(ExperimentScoring.Rank(run, new(0, 0, 0, 0, 100)));
        Assert.Equal(50, row.Factors.Wording); Assert.Equal(20, row.Factors.Meaning); Assert.Equal(90, row.Factors.Readability);
        Assert.Equal(50, row.Factors.Speed); Assert.Equal(1, row.Rank); Assert.Single(row.Warnings);
        Assert.Equal(3, row.Successful); Assert.Equal(2, row.Judged); Assert.Equal(1, row.Failed); Assert.True(row.Provisional);
        var other = Trial(candidate with { Id = "other" }, 5001, 100, null);
        var reordered = ExperimentScoring.Rank(run with { Trials = run.Trials.Add(other) }, new(0, 0, 0, 0, 100));
        Assert.All(reordered, r => Assert.Equal(1, r.Rank));
        Assert.Equal(row.Factors.Speed, reordered.Single(r => r.CandidateId == candidate.Id).Factors.Speed);
    }

    private static ExperimentTrial Trial(ExperimentCandidate candidate, double cleanup, double wording, ExperimentEvaluation? evaluation) => new()
    {
        Candidate = candidate, Generation = ExperimentGenerationStatus.Succeeded, CleanupMs = cleanup,
        Factors = new(wording, 0, null, null, null), Evaluation = evaluation,
        EvaluationStatus = evaluation is null ? ExperimentEvaluationStatus.Failed : ExperimentEvaluationStatus.Succeeded,
    };
}
