using System.Text.Json;

namespace Evaluation.Core;

public enum DocumentRole { Source, DT, BRD, PRD }
public enum TeamRole { Administrator, Member, Reviewer }

// Displayed scores and team thresholds use the 0-100 scale (sidecar score x 100).
public static class ScoreScale
{
    public const double Minimum = 0, Maximum = 100;
}

public record PolicySnapshot(int Version, double DT, double BRD, double PRD)
{
    public double For(DocumentRole role) => role switch
    { DocumentRole.DT => DT, DocumentRole.BRD => BRD, DocumentRole.PRD => PRD, _ => throw new ArgumentException("Evidence is not scored") };
    public void Validate()
    {
        if (new[] { DT, BRD, PRD }.Any(x => !double.IsFinite(x) || x < ScoreScale.Minimum || x > ScoreScale.Maximum))
            throw new ArgumentException("Thresholds must be finite numbers from 0 to 100.");
    }
}

public record InputSnapshot(Guid VersionId, DocumentRole Role, string Hash, int TextLength, Guid BundleId, string RelativePath);
public record ArtifactSelection(DocumentRole Role, Guid[] VersionIds);

public record LayerOutcome(string Layer, double Weight, double? Score);
public record CheckOutcome(string Id, string Layer, string Title, string Status, double Score, double Weight,
    string Detail, string[] Evidence, string Guidance);
public record ArtifactOutcome(DocumentRole Role, Guid[] VersionIds, string Decision, double? Score100, double Threshold,
    LayerOutcome[] Layers, CheckOutcome[] Checks, string Summary, bool InjectionDetected, bool Truncated, JudgeUsage? Usage,
    string? InvocationId, string? JudgeStatus, string[] Errors);
public record ScanResult(string Outcome, ArtifactOutcome[] Artifacts, string[] Errors);

public static class Decisions
{
    public const string Pass = "Pass", Fail = "Fail", Inconclusive = "Inconclusive", Pending = "Pending";
}

// Deterministic, server-owned acceptance of agent-eval sidecar responses. Pure functions only.
public static class Acceptance
{
    public const double Tolerance = 0.000001;
    public static readonly string[] Layers = ["structure", "reference", "judge"];
    private static readonly string[] CheckStatuses = ["pass", "warn", "fail", "skip"];
    private static readonly string[] ArtifactStatuses = ["pass", "warn", "fail"];
    public const int MaximumChecks = 1000;

    public static string Kind(DocumentRole role) => role switch
    { DocumentRole.DT => "dt", DocumentRole.BRD => "brd", DocumentRole.PRD => "prd", _ => throw new ArgumentException("Source documents are never scored.") };

    public static ArtifactOutcome Evaluate(ArtifactSelection artifact, JudgeContractSnapshot contract, PolicySnapshot policy, JudgeResponse? response)
    {
        contract.Validate();
        policy.Validate();
        var threshold = policy.For(artifact.Role);
        if (response is null) return Inconclusive(artifact, policy, "Missing judge response.");
        var errors = new List<string>();
        if (response.Kind != Kind(artifact.Role)) errors.Add("Judge response kind does not match the artifact role.");
        if (response.ContractVersion != contract.Version) errors.Add("Judge response contract version does not match the scan snapshot.");
        if (!UnitInterval(response.Score)) errors.Add("Reported score is missing, non-finite or outside 0..1.");
        if (response.Status is null || !ArtifactStatuses.Contains(response.Status)) errors.Add("Reported artifact status is invalid.");
        if (response.InjectionDetected is null || response.Truncated is null) errors.Add("Injection or truncation flag is missing.");
        if (response.LayerScores is null) errors.Add("Reported layer scores are missing.");
        else if (response.LayerScores.Any(x => !Layers.Contains(x.Key) || !UnitInterval(x.Value))) errors.Add("Reported layer scores are invalid.");
        if (response.Checks is null || response.Checks.Length == 0) errors.Add("Judge response has no checks.");
        else if (response.Checks.Length > MaximumChecks) errors.Add("Judge response has too many checks.");
        var checks = response.Checks ?? [];
        foreach (var c in checks)
        {
            if (c is null) { errors.Add("Null check."); continue; }
            if (string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.Title)) errors.Add("Check id or title is missing.");
            if (c.Layer is null || !Layers.Contains(c.Layer)) errors.Add("Check layer is unknown.");
            if (!UnitInterval(c.Score)) errors.Add("Check score is missing, non-finite or outside 0..1.");
            if (c.Weight is not { } w || !double.IsFinite(w) || w < 0) errors.Add("Check weight is missing, non-finite or negative.");
            if (c.Status is null || !CheckStatuses.Contains(c.Status)) errors.Add("Check status is invalid.");
            if (c.Evidence?.Any(e => e is null) == true) errors.Add("Check evidence contains null.");
        }
        if (errors.Count > 0) return Inconclusive(artifact, policy, errors.ToArray());

        var valid = checks.Select(c => c!).ToArray();
        var recomputed = Recompute(valid, contract.LayerWeights);
        if (!recomputed.Layers.ContainsKey("judge")) errors.Add("AI judge layer has no scored checks.");
        if (recomputed.Score is null || Math.Abs(recomputed.Score.Value - response.Score!.Value) > Tolerance)
            errors.Add("Reported score does not match recomputation from checks.");
        var reported = response.LayerScores!;
        if (reported.Count != recomputed.Layers.Count || recomputed.Layers.Any(x =>
                !reported.TryGetValue(x.Key, out var value) || Math.Abs(value - x.Value) > Tolerance))
            errors.Add("Reported layer scores do not match recomputation from checks.");
        if (errors.Count > 0) return Inconclusive(artifact, policy, errors.ToArray());

        var score100 = recomputed.Score!.Value * 100;
        return new(artifact.Role, artifact.VersionIds, Meets(score100, threshold) ? Decisions.Pass : Decisions.Fail, score100, threshold,
            Layers.Select(l => new LayerOutcome(l, contract.LayerWeights.For(l), recomputed.Layers.TryGetValue(l, out var s) ? s : null)).ToArray(),
            valid.Select(c => new CheckOutcome(Bound(c.Id, 200), c.Layer!, Bound(c.Title, 300), c.Status!, c.Score!.Value, c.Weight!.Value,
                Bound(c.Detail, 4000), (c.Evidence ?? []).Take(20).Select(e => Bound(e, 1000)).ToArray(), Bound(c.Guidance, 2000))).ToArray(),
            Bound(response.Summary, 8000), response.InjectionDetected!.Value, response.Truncated!.Value, response.Usage,
            Bound(response.InvocationId, 128), response.Status, []);
    }

    // Inclusive comparison after rounding away binary floating-point noise (e.g. 0.29 x 100 = 28.999999999999996).
    public static bool Meets(double score100, double threshold) => Math.Round(score100, 9) >= threshold;

    // agent-eval semantics: per-layer weighted mean over scored checks, then layer weights renormalized over present layers.
    public static (Dictionary<string, double> Layers, double? Score) Recompute(IEnumerable<JudgeCheck> checks, LayerWeights weights)
    {
        var list = checks.ToArray();
        var layers = new Dictionary<string, double>();
        foreach (var layer in Layers)
        {
            var scored = list.Where(c => c.Layer == layer && c.Status != "skip" && c.Weight > 0).ToArray();
            if (scored.Length == 0) continue;
            layers[layer] = scored.Sum(c => c.Score!.Value * c.Weight!.Value) / scored.Sum(c => c.Weight!.Value);
        }
        if (layers.Count == 0) return (layers, null);
        var total = layers.Keys.Sum(weights.For);
        return (layers, layers.Sum(x => x.Value * weights.For(x.Key)) / total);
    }

    public static ArtifactOutcome Inconclusive(ArtifactSelection artifact, PolicySnapshot policy, params string[] errors) =>
        new(artifact.Role, artifact.VersionIds, Decisions.Inconclusive, null, policy.For(artifact.Role), [], [], "", false, false, null, null, null,
            errors.Distinct().Take(20).ToArray());

    public static ArtifactOutcome Pending(ArtifactSelection artifact, PolicySnapshot policy) =>
        Inconclusive(artifact, policy) with { Decision = Decisions.Pending };

    public static string Combine(IReadOnlyCollection<ArtifactOutcome> artifacts) =>
        artifacts.Count == 0 || artifacts.Any(a => a.Decision is not Decisions.Pass and not Decisions.Fail) ? Decisions.Inconclusive :
        artifacts.Any(a => a.Decision == Decisions.Fail) ? Decisions.Fail : Decisions.Pass;

    // After an interrupted invocation, unfinished artifacts become Inconclusive; validated ones remain visible.
    public static ScanResult? Interrupt(ScanResult? result, string error) => result is null ? null :
        new(Decisions.Inconclusive, result.Artifacts.Select(a => a.Decision == Decisions.Pending ? a with
            { Decision = Decisions.Inconclusive, Errors = [error] } : a).ToArray(), result.Errors.Append(error).Distinct().ToArray());

    private static bool UnitInterval(double? value) => value is { } v && double.IsFinite(v) && v >= 0 && v <= 1;
    public static string Bound(string? value, int length) =>
        value is null ? "" : value.Length <= length ? value : value[..(length - 1)] + "…";
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(allowIntegerValues: false) }
    };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options) ?? throw new InvalidDataException("Missing snapshot");
}
