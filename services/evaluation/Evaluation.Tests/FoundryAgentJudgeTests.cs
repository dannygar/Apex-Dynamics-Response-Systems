using System.Net;
using System.Text;
using Azure.Core;
using Evaluation.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Evaluation.Tests;

public class FoundryAgentJudgeTests
{
    private sealed class FakeCredential(Exception? failure = null) : TokenCredential
    {
        public List<string[]> Scopes { get; } = [];
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken ct) => throw new NotSupportedException();
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken ct)
        {
            Scopes.Add(context.Scopes);
            if (failure is not null) throw failure;
            return ValueTask.FromResult(new AccessToken("fixture-token", DateTimeOffset.UtcNow.AddHours(1)));
        }
    }

    private sealed class ContractOnly : IJudgeAdapter
    {
        public int Calls;
        public bool Configured => true;
        public TimeSpan RequestTimeout => TimeSpan.FromMinutes(1);
        public Task<JudgeContractSnapshot> GetContract(CancellationToken ct) { Calls++; return Task.FromResult(JudgeContracts.Parse(JudgeTests.ContractJson)); }
        public Task<JudgeResponse> Evaluate(string kind, IReadOnlyList<JudgeFile> files, CancellationToken ct) =>
            throw new InvalidOperationException("Contract source must never evaluate.");
    }

    private static readonly Uri Project = new("https://acct.services.ai.azure.com/api/projects/proj");
    private static readonly JudgeFile[] Files = [new("method-01/x.md", "# DT")];

    private static (FoundryAgentJudgeAdapter Adapter, StubHandler Handler, FakeCredential Credential, ContractOnly Source) Create(
        HttpStatusCode code, string body, Exception? credentialFailure = null)
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(code)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") }));
        var credential = new FakeCredential(credentialFailure);
        var source = new ContractOnly();
        var adapter = new FoundryAgentJudgeAdapter(source, new HttpClient(handler), credential,
            FoundryAgentJudgeAdapter.InvocationsUri(Project, "apex-judge", "worker-1"), "worker-1", TimeSpan.FromMinutes(5));
        return (adapter, handler, credential, source);
    }

    [Fact]
    public async Task EvaluatePostsInvocationWithBearerAndAction()
    {
        var (adapter, handler, credential, _) = Create(HttpStatusCode.OK, JudgeTests.ResponseJson);
        var result = await adapter.Evaluate("dt", Files, default);
        Assert.Equal(0.75, result.Score);
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://acct.services.ai.azure.com/api/projects/proj/agents/apex-judge/endpoint/protocols/invocations?api-version=v1&agent_session_id=worker-1",
            request.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("fixture-token", request.Headers.Authorization.Parameter);
        Assert.Equal([FoundryAgentJudgeAdapter.Scope], Assert.Single(credential.Scopes));
        Assert.Contains("\"action\":\"evaluate\"", body);
        Assert.Contains("\"kind\":\"dt\"", body);
        Assert.Contains("\"path\":\"method-01/x.md\"", body);
    }

    [Fact]
    public async Task ContractComesFromInternalJudgeAppWithoutCallingAgent()
    {
        var (adapter, handler, credential, source) = Create(HttpStatusCode.OK, JudgeTests.ResponseJson);
        var contract = await adapter.GetContract(default);
        Assert.Equal("case", contract.Reference.Id);
        Assert.Equal(1, source.Calls);
        Assert.Empty(handler.Requests);
        Assert.Empty(credential.Scopes);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "rejected the artifact input (400)")]
    [InlineData(HttpStatusCode.BadGateway, "AI judge failed (502)")]
    [InlineData(HttpStatusCode.Unauthorized, "Judge service error (401)")]
    [InlineData(HttpStatusCode.Forbidden, "Judge service error (403)")]
    public async Task ErrorsBecomeJudgeCallExceptions(HttpStatusCode code, string expected)
    {
        var (adapter, _, _, _) = Create(code, "{\"error\":\"nope\"}");
        var error = await Assert.ThrowsAsync<JudgeCallException>(() => adapter.Evaluate("brd", Files, default));
        Assert.Contains(expected, error.Message);
        Assert.Equal((int)code, error.StatusCode);
    }

    [Fact]
    public async Task CredentialFailureIsSafeJudgeError()
    {
        var (adapter, handler, _, _) = Create(HttpStatusCode.OK, JudgeTests.ResponseJson,
            new Azure.Identity.AuthenticationFailedException("token endpoint detail"));
        var error = await Assert.ThrowsAsync<JudgeCallException>(() => adapter.Evaluate("brd", Files, default));
        Assert.DoesNotContain("token endpoint", error.Message);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("https://acct.services.ai.azure.com/api/projects/proj")]
    [InlineData("https://acct.services.ai.azure.com/api/projects/proj/")]
    public void ProjectEndpointAccepted(string value) =>
        Assert.Equal("https://acct.services.ai.azure.com/api/projects/proj", FoundryAgentJudgeAdapter.ValidateProjectEndpoint(value).AbsoluteUri);

    [Theory]
    [InlineData("http://acct.services.ai.azure.com/api/projects/proj")]
    [InlineData("https://acct.example.com/api/projects/proj")]
    [InlineData("https://acct.services.ai.azure.com/api/projects/proj?x=1")]
    [InlineData("https://acct.services.ai.azure.com/api/projects/a/b")]
    [InlineData("https://user:pw@acct.services.ai.azure.com/api/projects/proj")]
    [InlineData(null)]
    public void ProjectEndpointRejected(string? value) =>
        Assert.Throws<ArgumentException>(() => FoundryAgentJudgeAdapter.ValidateProjectEndpoint(value));

    [Theory]
    [InlineData("apex-judge", true)]
    [InlineData("a", true)]
    [InlineData("-bad", false)]
    [InlineData("bad_name", false)]
    [InlineData("", false)]
    public void AgentNameValidated(string name, bool ok)
    {
        if (ok) Assert.Equal(name, FoundryAgentJudgeAdapter.ValidateAgentName(name));
        else Assert.Throws<ArgumentException>(() => FoundryAgentJudgeAdapter.ValidateAgentName(name));
    }

    [Fact]
    public void SessionDefaultsAndValidates()
    {
        Assert.Equal("apex-evaluation-worker", FoundryAgentJudgeAdapter.ValidateSessionId(null));
        Assert.Throws<ArgumentException>(() => FoundryAgentJudgeAdapter.ValidateSessionId("has space"));
    }

    private static IJudgeAdapter Resolve(Dictionary<string, string?> values) =>
        new ServiceCollection().AddJudge(new ConfigurationBuilder().AddInMemoryCollection(values).Build())
            .BuildServiceProvider().GetRequiredService<IJudgeAdapter>();

    [Fact]
    public void RegistrationSelectsAdapterByMode()
    {
        Assert.IsType<UnavailableJudgeAdapter>(Resolve(new()));
        Assert.IsType<SidecarJudgeAdapter>(Resolve(new() { ["Judge:SidecarUrl"] = "https://judge.internal.example" }));
        Assert.IsType<FoundryAgentJudgeAdapter>(Resolve(new()
        {
            ["Judge:SidecarUrl"] = "https://judge.internal.example", ["Judge:Mode"] = "FoundryAgent",
            ["Judge:FoundryProjectEndpoint"] = Project.AbsoluteUri, ["Judge:FoundryAgentName"] = "apex-judge"
        }));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddJudge(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Judge:SidecarUrl"] = "https://judge.internal.example", ["Judge:Mode"] = "Other" }).Build()));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddJudge(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Judge:SidecarUrl"] = "https://judge.internal.example", ["Judge:Mode"] = "FoundryAgent" }).Build()));
    }
}
