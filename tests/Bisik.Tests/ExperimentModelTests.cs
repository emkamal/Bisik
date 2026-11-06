using System.Collections.Immutable;
using System.Text.Json;
using Bisik.Models;
using FluentAssertions;

namespace Bisik.Tests;

public class ExperimentModelTests
{
    internal static ExperimentDefinition Definition() => new()
    {
        Reference = "Use HAPPI, not Happy. FetchClassData returns 12 objects.",
        Terms = [new("HAPPI", "Happy")],
        Prompts = [new("Clean", "Keep meaning and identifiers.")],
        Models = [new("Candidate", "deployment", "model", [null])],
    };

    [Fact]
    public void DefaultsAndRoundTripAreStable()
    {
        var definition = Definition().Snapshot();
        var copy = JsonSerializer.Deserialize<ExperimentDefinition>(JsonSerializer.Serialize(definition))!;
        copy.Validate();
        copy.InputIdentity.Should().Be(definition.InputIdentity);
        copy.ConfigurationIdentity.Should().Be(definition.ConfigurationIdentity);
        copy.Limits.Should().Be(new ExperimentLimits());
        copy.Weights.Should().Be(new ExperimentWeights());
        copy.RetainAudio.Should().BeFalse();
        var take = new ExperimentTake(Guid.NewGuid(), "raw", 0, null, definition.InputIdentity);
        var run = new ExperimentRun(definition, take, ExperimentState.Completed, 7,
            [new() { Candidate = ExperimentCandidate.Baseline, Output = "raw" }]);
        JsonSerializer.Deserialize<ExperimentRun>(JsonSerializer.Serialize(run))!.Trials[0].Output.Should().Be("raw");
    }

    [Fact]
    public void CollectionsCannotMutateSnapshotAndWeightsDoNotChangeIdentity()
    {
        var aliases = new[] { "Happy" };
        var original = Definition() with { Terms = [new("HAPPI", aliases)] };
        var snapshot = original.Snapshot();
        aliases[0] = "mutated";
        original = original with { Terms = original.Terms.SetItem(0, new("OTHER")) };
        snapshot.Terms[0].WrongAliases[0].Should().Be("Happy");
        (snapshot with { Weights = new(0, 0, 0, 0, 100, 30) }).ConfigurationIdentity.Should().Be(snapshot.ConfigurationIdentity);
        (snapshot with { Reference = "different" }).InputIdentity.Should().NotBe(snapshot.InputIdentity);
        (snapshot with { ConnectionFingerprint = "changed" }).ConfigurationIdentity.Should().NotBe(snapshot.ConfigurationIdentity);
    }

    [Theory]
    [InlineData(0)] [InlineData(6)]
    public void InvalidBoundsAreRejected(int repeats) =>
        FluentActions.Invoking(() => (Definition() with { Limits = new() { Repetitions = repeats } }).Snapshot())
            .Should().Throw<ArgumentOutOfRangeException>();

    [Theory]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(0)]
    public void InvalidTimingIsRejected(double value)
    {
        FluentActions.Invoking(() => new ExperimentWeights(SpeedReferenceSeconds: value).Validate()).Should().Throw<ArgumentException>();
        if (value != 0)
            FluentActions.Invoking(() => new ExperimentTake(Guid.NewGuid(), "raw", value, null, "id").Validate()).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void PublicDtosCannotContainLiveConnectionSettings()
    {
        foreach (var type in new[] { typeof(ExperimentDefinition), typeof(ExperimentRun), typeof(ExperimentTrial), typeof(ExperimentCandidate) })
            type.GetProperties().Should().NotContain(p => p.PropertyType == typeof(ProviderSettings));
        ExperimentCandidate.Baseline.IsBaseline.Should().BeTrue();
        ExperimentIdentity.Hash("v1", "a\nb", "c").Should().NotBe(ExperimentIdentity.Hash("v1", "a", "b\nc"));
    }
    [Theory]
    [InlineData(0.0001)] [InlineData(1_000_001)]
    public void ViewSpeedScaleMustFitItsValidatedEditorBounds(double value) =>
        FluentActions.Invoking(() => new ExperimentWeights(SpeedReferenceSeconds: value).Validate()).Should().Throw<ArgumentException>();
}
