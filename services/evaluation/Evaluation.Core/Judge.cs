using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Core;
using Azure.Identity;
using Microsoft.EntityFrameworkCore;

namespace Evaluation.Core;

public record LayerWeights(double Structure, double Reference, double Judge)
{
    public double For(string layer) => layer switch
    {
        "structure" => Structure, "reference" => Reference, "judge" => Judge,
        _ => throw new ArgumentException("Unknown evaluation layer.")
    };
}
public record JudgeReferenceInfo(string Id, string Sha);
public record JudgeModelInfo(string Deployment, string ApiVersion, int Samples);
public record JudgeScale(double Minimum, double Maximum);

// The registered agent-eval sidecar contract. Scans snapshot it immutably at creation.
public record JudgeContractSnapshot(string Version, string Evaluator, string Sidecar, JudgeReferenceInfo Reference,
    string RubricsSha, string ProfilesSha, JudgeModelInfo Judge, JudgeScale Scale, LayerWeights LayerWeights, string[] Kinds)
{
    public void Validate()
    {
        static bool Text(string? s, int max) => !string.IsNullOrWhiteSpace(s) && s.Length <= max && !s.Any(char.IsControl);
        static bool Weight(double w) => double.IsFinite(w) && w > 0;
        if (!Text(Version, 512) || !Text(Evaluator, 200) || !Text(Sidecar, 100) || Reference is null || !Text(Reference.Id, 200) ||
            !Text(Reference.Sha, 128) || !Text(RubricsSha, 128) || !Text(ProfilesSha, 128) || Judge is null ||
            !Text(Judge.Deployment, 200) || !Text(Judge.ApiVersion, 100) || Judge.Samples is < 1 or > 20 ||
            Scale is null || Scale.Minimum != ScoreScale.Minimum || Scale.Maximum != ScoreScale.Maximum ||
            LayerWeights is null || !Weight(LayerWeights.Structure) || !Weight(LayerWeights.Reference) || !Weight(LayerWeights.Judge) ||
            Kinds is null || !Kinds.Order(StringComparer.Ordinal).SequenceEqual(["brd", "dt", "prd"], StringComparer.Ordinal))
            throw new InvalidDataException("Judge contract is invalid.");
    }
}

public static class JudgeContracts
{
    // Strict parser for the sidecar's GET /contract body (snake_case). Unknown extra members are ignored.
    public static JudgeContractSnapshot Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = Object(document.RootElement, "contract");
            var reference = Object(Property(root, "reference"), "reference");
            var judge = Object(Property(root, "judge"), "judge");
            var scale = Object(Property(root, "scale"), "scale");
            var weights = Object(Property(root, "layer_weights"), "layer_weights");
            var names = weights.EnumerateObject().Select(p => p.Name).ToArray();
            if (names.Length != 3 || !names.Order(StringComparer.Ordinal).SequenceEqual(["judge", "reference", "structure"], StringComparer.Ordinal))
                throw Invalid("layer_weights");
            var kinds = Property(root, "kinds");
            if (kinds.ValueKind != JsonValueKind.Array || kinds.EnumerateArray().Any(k => k.ValueKind != JsonValueKind.String)) throw Invalid("kinds");
            if (!Property(judge, "samples").TryGetInt32(out var samples)) throw Invalid("judge.samples");
            var contract = new JudgeContractSnapshot(String(root, "version"), String(root, "evaluator"), String(root, "sidecar"),
                new(String(reference, "id"), String(reference, "sha")), String(root, "rubrics_sha"), String(root, "profiles_sha"),
                new(String(judge, "deployment"), String(judge, "api_version"), samples),
                new(Number(scale, "minimum"), Number(scale, "maximum")),
                new(Number(weights, "structure"), Number(weights, "reference"), Number(weights, "judge")),
                kinds.EnumerateArray().Select(k => k.GetString()!).ToArray());
            contract.Validate();
            return contract;
        }
        catch (JsonException) { throw new InvalidDataException("Judge contract is not valid JSON."); }
        catch (InvalidOperationException) { throw new InvalidDataException("Judge contract has an unexpected shape."); }
    }
    private static JsonElement Property(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v : throw Invalid(name);
    private static JsonElement Object(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object ? e : throw Invalid(name);
    private static string String(JsonElement e, string name) =>
        Property(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString()! : throw Invalid(name);
    private static double Number(JsonElement e, string name) =>
        Property(e, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetDouble(out var d) ? d : throw Invalid(name);
    private static InvalidDataException Invalid(string name) => new($"Judge contract field '{name}' is missing or invalid.");
}

public record JudgeStatus(bool Ready, JudgeContractSnapshot? Contract, DateTimeOffset? LastHeartbeat, string[] Blockers);

public static class JudgeRegistry
{
    public const int Key = 1;
    public static readonly TimeSpan Freshness = TimeSpan.FromMinutes(5);
    public const string NotRegistered = "Judge worker has not registered recently. Start the worker with Judge__SidecarUrl and a healthy judge sidecar.";

    public static async Task Register(EvaluationDb db, JudgeContractSnapshot contract, DateTimeOffset now, CancellationToken ct = default)
    {
        contract.Validate();
        var json = Json.Write(contract);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (await db.JudgeRegistrations.Where(x => x.Id == Key).ExecuteUpdateAsync(s => s.SetProperty(x => x.Version, contract.Version)
                    .SetProperty(x => x.Contract, json).SetProperty(x => x.UpdatedAt, now), ct) == 1) return;
            var row = new JudgeRegistration { Id = Key, Version = contract.Version, Contract = json, UpdatedAt = now };
            db.JudgeRegistrations.Add(row);
            try { await db.SaveChangesAsync(ct); return; }
            catch (DbUpdateException) { db.Entry(row).State = EntityState.Detached; } // Another worker inserted concurrently.
        }
        throw new InvalidOperationException("Judge registration could not be stored.");
    }

    // Worker heartbeat: fetch and strictly validate GET /contract, then store it. Failures leave the registration to age out.
    public static async Task<bool> Refresh(EvaluationDb db, IJudgeAdapter judge, CancellationToken ct)
    {
        if (!judge.Configured) return false;
        try
        {
            var contract = await judge.GetContract(ct);
            await Register(db, contract, DateTimeOffset.UtcNow, ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return false; }
    }

    public static async Task<JudgeStatus> Current(EvaluationDb db, DateTimeOffset now, CancellationToken ct = default)
    {
        var row = await db.JudgeRegistrations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == Key, ct);
        if (row is null) return new(false, null, null, [NotRegistered]);
        JudgeContractSnapshot contract;
        try { contract = Json.Read<JudgeContractSnapshot>(row.Contract); contract.Validate(); }
        catch (Exception e) when (e is JsonException or InvalidDataException)
        { return new(false, null, row.UpdatedAt, ["Stored judge registration is invalid; the worker must re-register."]); }
        var fresh = row.UpdatedAt <= now.AddMinutes(1) && now - row.UpdatedAt <= Freshness;
        return new(fresh, contract, row.UpdatedAt, fresh ? [] : [NotRegistered]);
    }
}

public sealed record JudgeFile(string Path, string Text);
public sealed record JudgeCheck(string? Id, string? Layer, string? Title, double? Score, string? Status, double? Weight,
    string? Detail, string?[]? Evidence, string? Guidance);
public sealed record JudgeUsage(long PromptTokens, long CompletionTokens, long Calls);
public sealed record JudgeResponse(string? InvocationId, string? ContractVersion, string? Kind, double? Score, string? Status,
    Dictionary<string, double>? LayerScores, JudgeCheck?[]? Checks, string? Summary, bool? InjectionDetected, bool? Truncated,
    JudgeUsage? Usage);

public interface IJudgeAdapter
{
    bool Configured { get; }
    TimeSpan RequestTimeout { get; }
    Task<JudgeContractSnapshot> GetContract(CancellationToken ct);
    Task<JudgeResponse> Evaluate(string kind, IReadOnlyList<JudgeFile> files, CancellationToken ct);
}

// Fail-closed adapter used whenever Judge:SidecarUrl is not configured.
public sealed class UnavailableJudgeAdapter : IJudgeAdapter
{
    public bool Configured => false;
    public TimeSpan RequestTimeout => TimeSpan.Zero;
    public Task<JudgeContractSnapshot> GetContract(CancellationToken ct) => throw new JudgeUnavailableException();
    public Task<JudgeResponse> Evaluate(string kind, IReadOnlyList<JudgeFile> files, CancellationToken ct) => throw new JudgeUnavailableException();
}
public sealed class JudgeUnavailableException() : Exception("Judge sidecar is not configured.") { }
// Messages are safe for users: they never include artifact text, URLs or credentials.
public sealed class JudgeCallException(string message, int? statusCode = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
}

public sealed class SidecarJudgeAdapter(HttpClient http, TimeSpan requestTimeout) : IJudgeAdapter
{
    public const int MaximumResponseBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions RequestOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions ResponseOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    public bool Configured => true;
    public TimeSpan RequestTimeout { get; } = requestTimeout;

    public static SidecarJudgeAdapter Create(Uri baseUri, TimeSpan requestTimeout) =>
        new(new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10) })
        { BaseAddress = baseUri, Timeout = Timeout.InfiniteTimeSpan }, requestTimeout);

    // Artifact text must not cross a network in clear text: plain HTTP is accepted only for loopback sidecars.
    public static Uri ValidateUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && (uri.IsLoopback || IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip)))))
            throw new ArgumentException("Judge:SidecarUrl must be an http loopback URL (for example http://127.0.0.1:8081) or an https URL without credentials or query.");
        return new Uri(uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");
    }

    public static TimeSpan ValidateTimeout(string? minutes)
    {
        if (string.IsNullOrWhiteSpace(minutes)) return TimeSpan.FromMinutes(10);
        if (!double.TryParse(minutes, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ||
            !double.IsFinite(value) || value < 1 || value > 60)
            throw new ArgumentException("Judge:RequestTimeoutMinutes must be between 1 and 60.");
        return TimeSpan.FromMinutes(value);
    }

    public async Task<JudgeContractSnapshot> GetContract(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var response = await http.GetAsync("contract", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new JudgeCallException($"Judge sidecar contract request failed ({(int)response.StatusCode}).", (int)response.StatusCode);
            return JudgeContracts.Parse(await ReadBounded(response, 1024 * 1024, timeout.Token));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new JudgeCallException("Judge sidecar contract request timed out."); }
        catch (HttpRequestException) { throw new JudgeCallException("Judge sidecar is unreachable."); }
        catch (InvalidDataException e) { throw new JudgeCallException(e.Message); }
        catch (DecoderFallbackException) { throw new JudgeCallException("Judge sidecar contract is not UTF-8."); }
    }

    public Task<JudgeResponse> Evaluate(string kind, IReadOnlyList<JudgeFile> files, CancellationToken ct) =>
        SendEvaluate(http, () => new HttpRequestMessage(HttpMethod.Post, "evaluate")
        { Content = EvaluateContent(new { kind, files = files.Select(f => new { path = f.Path, text = f.Text }) }) }, RequestTimeout, ct);

    internal static ByteArrayContent EvaluateContent(object payload)
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(payload, RequestOptions));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    // Shared by the sidecar and Foundry hosted-agent adapters: both speak the same agent-eval JSON contract.
    internal static async Task<JudgeResponse> SendEvaluate(HttpClient http, Func<HttpRequestMessage> create, TimeSpan requestTimeout,
        CancellationToken ct, Func<HttpRequestMessage, CancellationToken, Task>? authorize = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(requestTimeout);
        string text;
        try
        {
            using var request = create();
            if (authorize is not null) await authorize(request, timeout.Token);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            text = await ReadBounded(response, MaximumResponseBytes, timeout.Token);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                var code = (int)response.StatusCode;
                var detail = ErrorText(text);
                throw new JudgeCallException(code switch
                {
                    400 => $"Judge rejected the artifact input (400){detail}. No score produced.",
                    502 => $"AI judge failed (502){detail}. No score produced.",
                    _ => $"Judge service error ({code}){detail}. No score produced."
                }, code);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new JudgeCallException($"Judge call timed out after {requestTimeout.TotalMinutes:0.#} minutes. No score produced."); }
        catch (HttpRequestException) { throw new JudgeCallException("Judge connection failed during evaluation. No score produced."); }
        catch (InvalidDataException) { throw new JudgeCallException("Judge response exceeded the size limit. No score produced."); }
        catch (DecoderFallbackException) { throw new JudgeCallException("Judge returned an unreadable response. No score produced."); }
        catch (AuthenticationFailedException) { throw new JudgeCallException("Judge credential could not be acquired. No score produced."); }
        try { return JsonSerializer.Deserialize<JudgeResponse>(text, ResponseOptions) ?? throw new JsonException(); }
        catch (JsonException) { throw new JudgeCallException("Judge returned an unreadable response. No score produced."); }
    }

    private static string ErrorText(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out var e) &&
                e.ValueKind == JsonValueKind.String)
            {
                var clean = new string(e.GetString()!.Where(c => !char.IsControl(c)).ToArray());
                return clean.Length == 0 ? "" : ": " + Acceptance.Bound(clean, 300);
            }
        }
        catch (JsonException) { }
        return "";
    }

    private static async Task<string> ReadBounded(HttpResponseMessage response, int limit, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Response too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920]; int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > limit) throw new InvalidDataException("Response too large.");
            buffer.Write(chunk, 0, read);
        }
        return new UTF8Encoding(false, true).GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}

// Routes evaluations to the agent-eval Foundry hosted agent (Invocations protocol) while the internal judge
// Container App, built from the same image and configuration, supplies the contract heartbeat. The hosted
// agent reports its contract_version in every result, and acceptance rejects any mismatch with the snapshot.
public sealed partial class FoundryAgentJudgeAdapter(IJudgeAdapter contractSource, HttpClient http, TokenCredential credential,
    Uri invocationsUri, string sessionId, TimeSpan requestTimeout) : IJudgeAdapter
{
    public const string Scope = "https://ai.azure.com/.default";
    public bool Configured => true;
    public TimeSpan RequestTimeout { get; } = requestTimeout;

    public static FoundryAgentJudgeAdapter Create(IJudgeAdapter contractSource, Uri projectEndpoint, string agentName, string sessionId,
        TokenCredential credential, TimeSpan requestTimeout) =>
        new(contractSource, new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(15) })
        { Timeout = Timeout.InfiniteTimeSpan }, credential, InvocationsUri(projectEndpoint, agentName, sessionId), sessionId, requestTimeout);

    // https://<account>.services.ai.azure.com/api/projects/<project>, no credentials, query or fragment.
    public static Uri ValidateProjectEndpoint(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || !uri.Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase) ||
            !ProjectPath().IsMatch(uri.AbsolutePath.TrimEnd('/')))
            throw new ArgumentException("Judge:FoundryProjectEndpoint must be https://<account>.services.ai.azure.com/api/projects/<project>.");
        return new Uri(uri.GetLeftPart(UriPartial.Path).TrimEnd('/'));
    }

    public static string ValidateAgentName(string? value) =>
        value is not null && AgentName().IsMatch(value) ? value :
        throw new ArgumentException("Judge:FoundryAgentName must be 1-63 letters, digits or hyphens.");

    public static string ValidateSessionId(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "apex-evaluation-worker" :
        AgentName().IsMatch(value) ? value : throw new ArgumentException("Judge:FoundrySessionId must be 1-63 letters, digits or hyphens.");

    // A stable session reuses one warm sandbox for sequential scans instead of cold-starting per call.
    public static Uri InvocationsUri(Uri projectEndpoint, string agentName, string sessionId) =>
        new($"{projectEndpoint.AbsoluteUri.TrimEnd('/')}/agents/{Uri.EscapeDataString(agentName)}/endpoint/protocols/invocations" +
            $"?api-version=v1&agent_session_id={Uri.EscapeDataString(sessionId)}");

    public string SessionId { get; } = sessionId;

    public Task<JudgeContractSnapshot> GetContract(CancellationToken ct) => contractSource.GetContract(ct);

    public Task<JudgeResponse> Evaluate(string kind, IReadOnlyList<JudgeFile> files, CancellationToken ct) =>
        SidecarJudgeAdapter.SendEvaluate(http, () => new HttpRequestMessage(HttpMethod.Post, invocationsUri)
        {
            Content = SidecarJudgeAdapter.EvaluateContent(new { action = "evaluate", kind, files = files.Select(f => new { path = f.Path, text = f.Text }) })
        }, RequestTimeout, ct, async (request, token) =>
        {
            var access = await credential.GetTokenAsync(new TokenRequestContext([Scope]), token);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", access.Token);
        });

    [GeneratedRegex("^/api/projects/[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex ProjectPath();
    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$")]
    private static partial Regex AgentName();
}