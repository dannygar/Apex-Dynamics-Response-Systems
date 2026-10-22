using System.Net;
using System.Text;
using System.Text.Json;
using Evaluation.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Evaluation.Tests;

public sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add((request, request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
        return await send(request, ct);
    }
}

public class JudgeTests
{
    // Synthetic sidecar wire payloads (snake_case), test-only.
    public const string ContractJson = """
        {"version":"agent-eval/0.1 rubrics/r profiles/p reference/case@abc judge/fixture@2025x1","evaluator":"agent-eval 0.1",
         "sidecar":"0.1.0","reference":{"id":"case","sha":"abc"},"rubrics_sha":"r","profiles_sha":"p",
         "judge":{"deployment":"fixture","api_version":"2025","samples":1},"scale":{"minimum":0,"maximum":100},
         "layer_weights":{"structure":0.4,"reference":0.3,"judge":0.3},"kinds":["dt","brd","prd"],"future_field":true}
        """;
    public const string ResponseJson = """
        {"invocation_id":"inv-1","contract_version":"agent-eval/0.1 rubrics/r profiles/p reference/case@abc judge/fixture@2025x1",
         "kind":"brd","score":0.75,"status":"warn","layer_scores":{"structure":0.75,"judge":0.75},
         "checks":[{"id":"brd.sections","layer":"structure","title":"Sections","score":0.75,"status":"warn","weight":1.0,
                    "detail":"All present","evidence":[],"guidance":""},
                   {"id":"judge.brd.clarity","layer":"judge","title":"Clarity <b>","score":0.75,"status":"warn","weight":1.0,
                    "detail":"3/5","evidence":["✔ Ünïcode quote"],"guidance":"Aim for: crisp"},
                   {"id":"judge.brd.evidence_verified","layer":"judge","title":"Quotes","score":1.0,"status":"skip","weight":0.0,
                    "detail":"1/1","evidence":[],"guidance":""}],
         "summary":"Mixed","injection_detected":false,"truncated":false,
         "usage":{"prompt_tokens":1200,"completion_tokens":300,"calls":1}}
        """;

    private static SidecarJudgeAdapter Adapter(StubHandler handler, TimeSpan? timeout = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8081/") }, timeout ?? TimeSpan.FromMinutes(10));
    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static readonly JudgeFile[] Files = [new("brd.md", "# BRD — Ünïcode")];

    [Fact] public void ContractParsesAndIgnoresUnknownFields()
    {
        var contract = JudgeContracts.Parse(ContractJson);
        Assert.Equal("case", contract.Reference.Id);
        Assert.Equal(new LayerWeights(0.4, 0.3, 0.3), contract.LayerWeights);
        Assert.Equal(new JudgeModelInfo("fixture", "2025", 1), contract.Judge);
        Assert.Equal(100, contract.Scale.Maximum);
    }

    [Theory]
    [InlineData("\"maximum\":100", "\"maximum\":1")]
    [InlineData("\"minimum\":0", "\"minimum\":10")]
    [InlineData(",\"judge\":0.3}", "}")]
    [InlineData("\"judge\":0.3}", "\"judge\":0.3,\"style\":0.1}")]
    [InlineData("\"judge\":0.3}", "\"judge\":0}")]
    [InlineData("\"structure\":0.4", "\"structure\":-0.4")]
    [InlineData("\"structure\":0.4", "\"structure\":\"0.4\"")]
    [InlineData("\"version\":\"agent-eval/0.1 rubrics/r profiles/p reference/case@abc judge/fixture@2025x1\"", "\"version\":\"\"")]
    [InlineData("\"kinds\":[\"dt\",\"brd\",\"prd\"]", "\"kinds\":[\"dt\",\"brd\"]")]
    [InlineData("\"samples\":1", "\"samples\":0")]
    [InlineData("\"samples\":1", "\"samples\":1.5")]
    [InlineData("\"reference\":{\"id\":\"case\",\"sha\":\"abc\"},", "")]
    [InlineData("{\"version\"", "[{\"version\"")]
    public void ContractValidationIsStrict(string find, string replace)
    {
        Assert.Contains(find, ContractJson);
        Assert.Throws<InvalidDataException>(() => JudgeContracts.Parse(ContractJson.Replace(find, replace)));
    }
    [Fact] public void ContractRejectsNonJson() => Assert.Throws<InvalidDataException>(() => JudgeContracts.Parse("not json"));

    [Theory]
    [InlineData("http://127.0.0.1:8081")]
    [InlineData("http://localhost:8081/")]
    [InlineData("http://[::1]:8081")]
    [InlineData("https://judge.internal.example")]
    public void SidecarUrlAccepted(string url) => Assert.EndsWith("/", SidecarJudgeAdapter.ValidateUrl(url).AbsoluteUri);
    [Theory]
    [InlineData("http://10.0.0.5:8081")]
    [InlineData("http://judge.example")]
    [InlineData("ftp://127.0.0.1")]
    [InlineData("http://user:pass@127.0.0.1:8081")]
    [InlineData("http://127.0.0.1:8081/?key=1")]
    [InlineData("relative/path")]
    public void SidecarUrlRejected(string url) => Assert.Throws<ArgumentException>(() => SidecarJudgeAdapter.ValidateUrl(url));
    [Theory]
    [InlineData("0")]
    [InlineData("61")]
    [InlineData("NaN")]
    [InlineData("ten")]
    public void TimeoutRejected(string minutes) => Assert.Throws<ArgumentException>(() => SidecarJudgeAdapter.ValidateTimeout(minutes));
    [Fact] public void TimeoutDefaultsToTenMinutes() => Assert.Equal(TimeSpan.FromMinutes(10), SidecarJudgeAdapter.ValidateTimeout(null));

    [Fact] public async Task EvaluatePostsContractBodyAndParsesResponse()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, ResponseJson)));
        var response = await Adapter(handler).Evaluate("brd", Files, default);
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://127.0.0.1:8081/evaluate", request.RequestUri!.AbsoluteUri);
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        Assert.NotNull(request.Content.Headers.ContentLength);
        Assert.Contains("Ünïcode", body); // UTF-8, not \u-escaped, keeps the request under the sidecar byte limit.
        using var json = JsonDocument.Parse(body!);
        Assert.Equal("brd", json.RootElement.GetProperty("kind").GetString());
        Assert.Equal("brd.md", json.RootElement.GetProperty("files")[0].GetProperty("path").GetString());
        Assert.Equal("inv-1", response.InvocationId);
        Assert.Equal(0.75, response.LayerScores!["judge"]);
        Assert.Equal(new JudgeUsage(1200, 300, 1), response.Usage);
        Assert.Equal("Aim for: crisp", response.Checks![1]!.Guidance);
        var outcome = Acceptance.Evaluate(new(DocumentRole.BRD, [Guid.NewGuid()]), JudgeContracts.Parse(ContractJson), new(1, 70, 75, 80), response);
        Assert.Equal("Pass", outcome.Decision);
        Assert.Equal(75, outcome.Score100!.Value, 9);
        Assert.Equal("Clarity <b>", outcome.Checks[1].Title);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "Judge rejected the artifact input (400): duplicate file path")]
    [InlineData(HttpStatusCode.BadGateway, "AI judge failed (502): duplicate file path")]
    [InlineData(HttpStatusCode.InternalServerError, "Judge service error (500): duplicate file path")]
    public async Task HttpErrorsBecomeSafeTechnicalFailures(HttpStatusCode code, string expected)
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Json(code, """{"error":"duplicate file path"}""")));
        var error = await Assert.ThrowsAsync<JudgeCallException>(() => Adapter(handler).Evaluate("dt", Files, default));
        Assert.StartsWith(expected, error.Message);
        Assert.Equal((int)code, error.StatusCode);
    }
    [Fact] public async Task UnreadableResponseIsTechnicalFailure()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, """{"score":"high"}""")));
        await Assert.ThrowsAsync<JudgeCallException>(() => Adapter(handler).Evaluate("dt", Files, default));
    }
    [Fact] public async Task OversizedResponseIsTechnicalFailure()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, new string(' ', SidecarJudgeAdapter.MaximumResponseBytes + 1))));
        await Assert.ThrowsAsync<JudgeCallException>(() => Adapter(handler).Evaluate("dt", Files, default));
    }
    [Fact] public async Task TimeoutIsTechnicalFailure()
    {
        var handler = new StubHandler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); });
        var error = await Assert.ThrowsAsync<JudgeCallException>(() => Adapter(handler, TimeSpan.FromMilliseconds(100)).Evaluate("dt", Files, default));
        Assert.Contains("timed out", error.Message);
    }
    [Fact] public async Task CallerCancellationIsNotReportedAsTimeout()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var handler = new StubHandler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Adapter(handler).Evaluate("dt", Files, cancel.Token));
    }
    [Fact] public async Task ConnectionFailureIsTechnicalFailure()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("connection refused 127.0.0.1"));
        var error = await Assert.ThrowsAsync<JudgeCallException>(() => Adapter(handler).Evaluate("dt", Files, default));
        Assert.DoesNotContain("127.0.0.1", error.Message);
    }
    [Fact] public async Task ContractFetchValidates()
    {
        var good = new StubHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, ContractJson)));
        Assert.Equal("case", (await Adapter(good).GetContract(default)).Reference.Id);
        Assert.Equal("http://127.0.0.1:8081/contract", good.Requests[0].Request.RequestUri!.AbsoluteUri);
        var bad = new StubHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, ContractJson.Replace("\"maximum\":100", "\"maximum\":1"))));
        await Assert.ThrowsAsync<JudgeCallException>(() => Adapter(bad).GetContract(default));
        var down = new StubHandler((_, _) => Task.FromResult(Json(HttpStatusCode.ServiceUnavailable, "{}")));
        await Assert.ThrowsAsync<JudgeCallException>(() => Adapter(down).GetContract(default));
    }
    [Fact] public async Task UnavailableAdapterFailsClosed()
    {
        var adapter = new UnavailableJudgeAdapter();
        Assert.False(adapter.Configured);
        await Assert.ThrowsAsync<JudgeUnavailableException>(() => adapter.GetContract(default));
        await Assert.ThrowsAsync<JudgeUnavailableException>(() => adapter.Evaluate("dt", Files, default));
    }

    // End-to-end over a real loopback socket against a tiny fake sidecar (never the real judge).
    [Fact] public async Task WorksAgainstFakeLoopbackSidecar()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        string? received = null; long? length = null;
        app.MapGet("/contract", () => Results.Text(ContractJson, "application/json"));
        app.MapPost("/evaluate", async (HttpRequest request) =>
        {
            length = request.ContentLength;
            if (!(request.ContentType ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase)) return Results.StatusCode(415);
            received = await new StreamReader(request.Body).ReadToEndAsync();
            return received.Contains("\"kind\":\"dt\"") ? Results.Json(new { error = "AI judge layer failed; no score produced" }, statusCode: 502) :
                Results.Text(ResponseJson, "application/json");
        });
        await app.StartAsync();
        var adapter = SidecarJudgeAdapter.Create(SidecarJudgeAdapter.ValidateUrl(app.Urls.First()), TimeSpan.FromMinutes(1));
        Assert.Equal("case", (await adapter.GetContract(default)).Reference.Id);
        var response = await adapter.Evaluate("brd", Files, default);
        Assert.Equal("inv-1", response.InvocationId);
        Assert.True(length > 0);
        Assert.Contains("Ünïcode", received);
        var failure = await Assert.ThrowsAsync<JudgeCallException>(() => adapter.Evaluate("dt", [new("method-01-scope/a.md", "# A")], default));
        Assert.Equal(502, failure.StatusCode);
        await app.StopAsync();
    }
}
