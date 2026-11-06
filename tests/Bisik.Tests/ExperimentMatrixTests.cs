using System.Collections.Immutable;
using Bisik.Core;
using Bisik.Models;

namespace Bisik.Tests;

public class ExperimentMatrixTests
{
    private static PostProcessorSettings Connection => TranscriptionServiceTextTests.Settings();
    private static ExperimentDefinition Definition => ExperimentModelTests.Definition() with
    { ConnectionFingerprint = ExperimentMatrix.ConnectionIdentity(Connection) };

    [Fact]
    public void DefaultAndNoneRemainDistinctAndBaselineIsNotAPaidTrial()
    {
        var definition = Definition with { Models = [new("one", "dep", "model", [null, "none", "low"])] };
        var preview = ExperimentMatrix.Build(definition, Connection);
        Assert.Equal(1, preview.ScheduledTrials);
        Assert.Equal(2, preview.UnknownTrials);
        Assert.False(preview.CanRun);
        var low = preview.Rows.Single(r => r.Candidate.RequestedEffort == "low").Candidate;
        Assert.Null(low.EffectiveEffort);
        Assert.Throws<InvalidOperationException>(() => ExperimentMatrix.Settings(Connection, low));
        var proof = new ExperimentCompatibility(low.ApiFingerprint, "low", ExperimentVerification.Verified);
        var verified = ExperimentMatrix.Build(definition, Connection, [proof]);
        Assert.Equal(2, verified.ScheduledTrials);
        Assert.Equal("low", verified.Rows.Single(r => r.Candidate.RequestedEffort == "low").Candidate.EffectiveEffort);
        Assert.DoesNotContain(verified.Rows, r => r.Candidate.IsBaseline);
    }

    [Fact]
    public void EffectiveAliasesAndPromptsDeduplicateBeforeRepeatExpansion()
    {
        var definition = Definition with
        {
            Models = [new("First", "dep", "model", [null, null]), new("Alias", "dep", "model", [null])],
            Prompts = [new("Prompt", "same"), new("Other name", "same")], Limits = new() { Repetitions = 5 },
        };
        var preview = ExperimentMatrix.Build(definition, Connection);
        Assert.Single(preview.Rows);
        Assert.Equal(5, preview.ScheduledTrials);
        Assert.True(preview.CanRun);
    }

    [Fact]
    public void CapabilityIsScopedToApiDeploymentAndPayloadModel()
    {
        var definition = Definition with { Models = [new("one", "dep", "model", ["low"])] };
        var candidate = ExperimentMatrix.Build(definition, Connection).Rows[0].Candidate;
        var proof = new ExperimentCompatibility(candidate.ApiFingerprint, "low", ExperimentVerification.Verified);
        Assert.True(ExperimentMatrix.Build(definition, Connection, [proof]).CanRun);
        Assert.False(ExperimentMatrix.Build(definition with { Models = [new("one", "other-dep", "model", ["low"])] }, Connection, [proof]).CanRun);
        var changed = Connection with { Provider = Connection.Provider with { ApiVersion = "different" } };
        Assert.Throws<InvalidOperationException>(() => ExperimentMatrix.Build(definition, changed, [proof]));
        var revised = definition with { ConnectionFingerprint = ExperimentMatrix.ConnectionIdentity(changed) };
        Assert.False(ExperimentMatrix.Build(revised, changed, [proof]).CanRun);
    }

    [Fact]
    public void MatrixBoundsAndBlankEntriesAreExplicit()
    {
        var definition = Definition with { Prompts = Enumerable.Range(0, 21).Select(i => new ExperimentPrompt($"p{i}", $"prompt{i}")).ToImmutableArray(),
            Limits = new() { Repetitions = 5 } };
        Assert.Throws<ArgumentException>(() => ExperimentMatrix.Build(definition, Connection));
        var preview = ExperimentMatrix.Build(Definition with { Models = [new("", "", "", [null]), new("Invalid", "dep", "model", ["banana"])] }, Connection);
        Assert.All(preview.Rows, r => { Assert.Equal(ExperimentMatrixStatus.Excluded, r.Status); Assert.NotNull(r.Reason); });
        Assert.Equal(0, preview.ScheduledTrials);
    }

    [Fact]
    public void ExistingVerifiedCapabilityAndFailedProbeAreHonest()
    {
        var definition = Definition with { Models = [new("one", "deployment", "model", ["low"])] };
        var settings = ExperimentMatrix.Settings(Connection, definition.Models[0], definition.Prompts[0], "low");
        var capability = new ReasoningCapability(ReasoningCapability.Fingerprint(settings), ["low"]);
        Assert.True(ExperimentMatrix.Build(definition, Connection with { ReasoningCapability = capability }).CanRun);
        var preview = ExperimentMatrix.Build(definition, Connection, [new(capability.ApiFingerprint, "low", ExperimentVerification.Rejected, "http-400")]);
        Assert.Equal(ExperimentMatrixStatus.Excluded, preview.Rows[0].Status);
        Assert.Equal("http-400", preview.Rows[0].Reason);
    }
}
