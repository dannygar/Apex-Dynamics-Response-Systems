using Evaluation.Core;
using Xunit;

namespace Evaluation.Tests;

// Synthetic, test-only sidecar responses. Never used by the application.
public static class JudgeFixtures
{
    public static readonly JudgeContractSnapshot Contract = new("agent-eval/test rubrics/a profiles/b reference/case@c judge/fixture@v1x1",
        "agent-eval test", "0.1.0", new("case", "c"), "a", "b", new("fixture", "v1", 1), new(0, 100), new(0.4, 0.3, 0.3), ["dt", "brd", "prd"]);
    public static readonly PolicySnapshot Policy = new(1, 70, 80, 90);

    public static JudgeCheck Check(string layer, double score, double weight = 1, string status = "pass", string id = "check") =>
        new($"{layer}.{id}", layer, $"{layer} {id}", score, status, weight, "detail", ["✔ quoted evidence"], "Improve this.");

    // A response whose reported scores are exactly what agent-eval would compute from its checks.
    public static JudgeResponse Response(DocumentRole role, double structure, double reference, double judge, string? version = null)
    {
        JudgeCheck[] checks = [Check("structure", structure), Check("reference", reference), Check("judge", judge),
            Check("judge", 0.1, 0, "skip", "evidence_verified")];
        var (layers, score) = Acceptance.Recompute(checks, Contract.LayerWeights);
        return new(Guid.NewGuid().ToString(), version ?? Contract.Version, Acceptance.Kind(role), score, "warn", layers, checks,
            "Summary", false, false, new(100, 20, 1));
    }
    public static JudgeResponse Scored(DocumentRole role, double unit) => Response(role, unit, unit, unit);
}

public class DomainTests
{
    private static readonly ArtifactSelection Dt = new(DocumentRole.DT, [Guid.NewGuid(), Guid.NewGuid()]);
    private static ArtifactSelection Artifact(DocumentRole role) => role == DocumentRole.DT ? Dt : new(role, [Guid.NewGuid()]);
    private static ArtifactOutcome Evaluate(JudgeResponse? response, DocumentRole role = DocumentRole.DT) =>
        Acceptance.Evaluate(Artifact(role), JudgeFixtures.Contract, JudgeFixtures.Policy, response);

    [Theory]
    [InlineData(DocumentRole.DT, 0.6999, "Fail")]
    [InlineData(DocumentRole.DT, 0.70, "Pass")]
    [InlineData(DocumentRole.DT, 0.7001, "Pass")]
    [InlineData(DocumentRole.BRD, 0.7999, "Fail")]
    [InlineData(DocumentRole.BRD, 0.80, "Pass")]
    [InlineData(DocumentRole.BRD, 0.8001, "Pass")]
    [InlineData(DocumentRole.PRD, 0.8999, "Fail")]
    [InlineData(DocumentRole.PRD, 0.90, "Pass")]
    [InlineData(DocumentRole.PRD, 0.9001, "Pass")]
    public void ThresholdBoundariesPerRole(DocumentRole role, double unit, string expected)
    {
        var outcome = Evaluate(JudgeFixtures.Scored(role, unit), role);
        Assert.Equal(expected, outcome.Decision);
        Assert.Equal(unit * 100, outcome.Score100!.Value, 9);
        Assert.Equal(JudgeFixtures.Policy.For(role), outcome.Threshold);
        Assert.Empty(outcome.Errors);
    }

    [Fact] public void FloatingNoiseDoesNotFailAnEqualScore()
    {
        Assert.True(0.29 * 100 < 29); // Binary floating point: 28.999999999999996
        var outcome = Acceptance.Evaluate(Artifact(DocumentRole.BRD), JudgeFixtures.Contract, JudgeFixtures.Policy with { BRD = 29 },
            JudgeFixtures.Scored(DocumentRole.BRD, 0.29));
        Assert.Equal("Pass", outcome.Decision);
    }

    [Fact] public void RecomputesLikeAgentEvalWithRenormalizedLayers()
    {
        JudgeCheck[] checks = [JudgeFixtures.Check("structure", 1, 2), JudgeFixtures.Check("structure", 0.5, 1, id: "b"),
            JudgeFixtures.Check("judge", 0.25), JudgeFixtures.Check("reference", 0, 0, "skip")];
        var (layers, score) = Acceptance.Recompute(checks, JudgeFixtures.Contract.LayerWeights);
        Assert.Equal(2, layers.Count);
        Assert.Equal(2.5 / 3, layers["structure"], 12);
        Assert.Equal((0.4 * 2.5 / 3 + 0.3 * 0.25) / 0.7, score!.Value, 12);
    }

    [Fact] public void LayersAndChecksArePersistedForValidResponse()
    {
        var outcome = Evaluate(JudgeFixtures.Response(DocumentRole.DT, 1, 0.5, 0.75));
        Assert.Equal(["structure", "reference", "judge"], outcome.Layers.Select(l => l.Layer));
        Assert.Equal(0.4, outcome.Layers[0].Weight);
        Assert.Equal(4, outcome.Checks.Length);
        Assert.Equal("Improve this.", outcome.Checks[0].Guidance);
        Assert.Equal(new JudgeUsage(100, 20, 1), outcome.Usage);
        Assert.False(string.IsNullOrEmpty(outcome.InvocationId));
    }

    [Fact] public void ScoreMismatchIsInconclusive() =>
        AssertInconclusive(JudgeFixtures.Scored(DocumentRole.DT, 0.8) with { Score = 0.81 }, "Reported score does not match");
    [Fact] public void LayerMismatchIsInconclusive()
    {
        var response = JudgeFixtures.Scored(DocumentRole.DT, 0.8);
        AssertInconclusive(response with { LayerScores = new(response.LayerScores!) { ["structure"] = 0.5 } }, "layer scores do not match");
    }
    [Fact] public void ExtraReportedLayerIsInconclusive()
    {
        var response = JudgeFixtures.Response(DocumentRole.DT, 1, 1, 1);
        var checks = response.Checks!.Where(c => c!.Layer != "reference").ToArray();
        var (layers, score) = Acceptance.Recompute(checks!, JudgeFixtures.Contract.LayerWeights);
        AssertInconclusive(response with { Checks = checks, Score = score, LayerScores = new(layers) { ["reference"] = 1 } }, "layer scores do not match");
    }
    [Fact] public void MissingJudgeLayerIsInconclusive()
    {
        JudgeCheck[] checks = [JudgeFixtures.Check("structure", 1), JudgeFixtures.Check("judge", 1, 0, "skip")];
        var (layers, score) = Acceptance.Recompute(checks, JudgeFixtures.Contract.LayerWeights);
        AssertInconclusive(JudgeFixtures.Scored(DocumentRole.DT, 1) with { Checks = checks, Score = score, LayerScores = layers }, "AI judge layer");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void InvalidCheckScoresNeverPass(double score)
    {
        var response = JudgeFixtures.Scored(DocumentRole.DT, 1);
        AssertInconclusive(response with { Checks = [.. response.Checks!.Skip(1), JudgeFixtures.Check("structure", 1) with { Score = score }] }, "Check score");
    }
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-1d)]
    [InlineData(1.5d)]
    public void InvalidOverallScoresNeverPass(double score) =>
        AssertInconclusive(JudgeFixtures.Scored(DocumentRole.DT, 1) with { Score = score }, "Reported score");
    [Fact] public void MissingScoreIsInconclusive() => AssertInconclusive(JudgeFixtures.Scored(DocumentRole.DT, 1) with { Score = null }, "Reported score");
    [Fact] public void NegativeOrNonfiniteWeightIsInconclusive()
    {
        var response = JudgeFixtures.Scored(DocumentRole.DT, 1);
        AssertInconclusive(response with { Checks = [.. response.Checks!, JudgeFixtures.Check("judge", 1, -1)] }, "Check weight");
        AssertInconclusive(response with { Checks = [.. response.Checks!, JudgeFixtures.Check("judge", 1, double.NaN)] }, "Check weight");
    }
    [Theory]
    [InlineData("PASS")]
    [InlineData("unknown")]
    [InlineData(null)]
    public void BadCheckStatusIsInconclusive(string? status)
    {
        var response = JudgeFixtures.Scored(DocumentRole.DT, 1);
        AssertInconclusive(response with { Checks = [.. response.Checks!, JudgeFixtures.Check("judge", 1) with { Status = status }] }, "Check status");
    }
    [Fact] public void BadArtifactStatusIsInconclusive() =>
        AssertInconclusive(JudgeFixtures.Scored(DocumentRole.DT, 1) with { Status = "skip" }, "artifact status");
    [Fact] public void UnknownLayerIsInconclusive()
    {
        var response = JudgeFixtures.Scored(DocumentRole.DT, 1);
        AssertInconclusive(response with { Checks = [.. response.Checks!, JudgeFixtures.Check("style", 1)] }, "layer is unknown");
    }
    [Fact] public void MissingIdOrTitleIsInconclusive()
    {
        var response = JudgeFixtures.Scored(DocumentRole.DT, 1);
        AssertInconclusive(response with { Checks = [.. response.Checks!, JudgeFixtures.Check("judge", 1) with { Id = " " }] }, "id or title");
        AssertInconclusive(response with { Checks = [.. response.Checks!, JudgeFixtures.Check("judge", 1) with { Title = null }] }, "id or title");
    }
    [Fact] public void NullCheckIsInconclusive()
    {
        var response = JudgeFixtures.Scored(DocumentRole.DT, 1);
        AssertInconclusive(response with { Checks = [.. response.Checks!, null] }, "Null check");
    }
    [Fact] public void EmptyChecksAreInconclusive() =>
        AssertInconclusive(JudgeFixtures.Scored(DocumentRole.DT, 1) with { Checks = [] }, "no checks");
    [Theory]
    [InlineData(DocumentRole.BRD)]
    [InlineData(DocumentRole.PRD)]
    public void WrongKindIsInconclusive(DocumentRole responseRole) =>
        AssertInconclusive(JudgeFixtures.Scored(responseRole, 1), "kind");
    [Fact] public void WrongContractVersionIsInconclusive() =>
        AssertInconclusive(JudgeFixtures.Response(DocumentRole.DT, 1, 1, 1, "other-version"), "contract version");
    [Fact] public void MissingFlagsAreInconclusive() =>
        AssertInconclusive(JudgeFixtures.Scored(DocumentRole.DT, 1) with { InjectionDetected = null }, "Injection");
    [Fact] public void MissingResponseIsInconclusive() => AssertInconclusive(null, "Missing judge response");

    [Fact] public void InjectionAndTruncationFlagsAreRetained()
    {
        var outcome = Evaluate(JudgeFixtures.Scored(DocumentRole.DT, 1) with { InjectionDetected = true, Truncated = true });
        Assert.True(outcome.InjectionDetected); Assert.True(outcome.Truncated);
    }

    [Fact] public void UntrustedStringsAreBounded()
    {
        var response = JudgeFixtures.Scored(DocumentRole.DT, 1);
        var huge = new string('x', 20_000);
        var outcome = Evaluate(response with { Summary = huge, Checks = [.. response.Checks!.Select(c => c! with { Detail = huge,
            Evidence = Enumerable.Repeat(huge, 50).ToArray() })] });
        Assert.Equal("Pass", outcome.Decision);
        Assert.True(outcome.Summary.Length <= 8000);
        Assert.All(outcome.Checks, c => { Assert.True(c.Detail.Length <= 4000); Assert.True(c.Evidence.Length <= 20); });
    }

    [Fact] public void ScanOutcomeCombination()
    {
        ArtifactOutcome With(string decision) => Acceptance.Inconclusive(Dt, JudgeFixtures.Policy) with { Decision = decision };
        Assert.Equal("Pass", Acceptance.Combine([With("Pass"), With("Pass")]));
        Assert.Equal("Fail", Acceptance.Combine([With("Pass"), With("Fail")]));
        Assert.Equal("Inconclusive", Acceptance.Combine([With("Fail"), With("Inconclusive")]));
        Assert.Equal("Inconclusive", Acceptance.Combine([With("Pass"), With("Pending")]));
        Assert.Equal("Inconclusive", Acceptance.Combine([]));
    }

    [Fact] public void InterruptKeepsValidatedArtifactsVisible()
    {
        var done = Evaluate(JudgeFixtures.Scored(DocumentRole.DT, 1));
        var pending = Acceptance.Pending(Artifact(DocumentRole.BRD), JudgeFixtures.Policy);
        var result = Acceptance.Interrupt(new ScanResult("Pending", [done, pending], []), "Interrupted")!;
        Assert.Equal("Inconclusive", result.Outcome);
        Assert.Equal("Pass", result.Artifacts[0].Decision);
        Assert.Equal("Inconclusive", result.Artifacts[1].Decision);
    }

    [Theory]
    [InlineData(double.NaN, 1, 1)]
    [InlineData(-0.1, 1, 1)]
    [InlineData(1, 100.1, 1)]
    [InlineData(1, 1, double.PositiveInfinity)]
    public void PolicyValidation(double dt, double brd, double prd) =>
        Assert.Throws<ArgumentException>(() => new PolicySnapshot(1, dt, brd, prd).Validate());
    [Fact] public void PolicyAcceptsFullScale() => new PolicySnapshot(1, 0, 100, 55.5).Validate();

    [Fact] public void StrictJsonRejectsNonnumericSnapshotScore() =>
        Assert.Throws<System.Text.Json.JsonException>(() => Json.Read<PolicySnapshot>("""{"version":1,"dt":"seventy"}"""));

    private static void AssertInconclusive(JudgeResponse? response, string error)
    {
        var outcome = Evaluate(response);
        Assert.Equal("Inconclusive", outcome.Decision);
        Assert.Null(outcome.Score100);
        Assert.Empty(outcome.Checks);
        Assert.Contains(outcome.Errors, e => e.Contains(error, StringComparison.OrdinalIgnoreCase));
    }
}
