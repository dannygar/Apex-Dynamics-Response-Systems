using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Evaluation.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Evaluation.Tests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public static readonly Guid Tenant = Guid.NewGuid(), Team = Guid.NewGuid(), OtherTeam = Guid.NewGuid(), AdminTeam = Guid.NewGuid();
    public static readonly string Administrator = Guid.NewGuid().ToString(), Member = Guid.NewGuid().ToString(), Reviewer = Guid.NewGuid().ToString(),
        PlatformAdministrator = Guid.NewGuid().ToString();
    public const string RedirectUrl = "https://evaluation.example.test/";
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly string storage = Path.Combine(AppContext.BaseDirectory, "private-fixture", Guid.NewGuid().ToString("N"));
    private readonly string? spaRoot;
    private readonly bool invitations;
    private readonly string? databaseFile;
    public FakeDirectory Directory { get; } = new();
    // fileDatabase gives every request its own SQLite connection so concurrent requests are truly concurrent.
    public ApiFactory(string? spaRoot = null, bool invitations = true, bool fileDatabase = false)
    {
        this.spaRoot = spaRoot; this.invitations = invitations;
        if (fileDatabase)
        {
            System.IO.Directory.CreateDirectory(storage);
            databaseFile = Path.Combine(storage, "fixture.db");
        }
        else connection.Open();
    }
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Storage:Root", storage);
        if (spaRoot is not null) builder.UseSetting("Spa:Root", spaRoot);
        if (invitations) builder.UseSetting("Invitations:RedirectUrl", RedirectUrl);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<EvaluationDb>>();
            if (databaseFile is null) services.AddDbContext<EvaluationDb>(o => o.UseSqlite(connection));
            else services.AddDbContext<EvaluationDb>(o => o.UseSqlite($"Data Source={databaseFile};Pooling=False;Default Timeout=30"));
            services.RemoveAll<IDirectoryInvitations>();
            services.AddSingleton<IDirectoryInvitations>(Directory);
            services.AddAuthentication(o =>
            {
                o.DefaultAuthenticateScheme = "Fixture"; o.DefaultChallengeScheme = "Fixture";
            }).AddScheme<AuthenticationSchemeOptions, FixtureAuthentication>("Fixture", _ => { });
        });
    }
    public void Initialize()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EvaluationDb>();
        db.Database.EnsureCreated();
        db.Teams.AddRange(new Team { Id = Team, TenantId = Tenant.ToString(), Name = "Authorized fixture" },
            new Team { Id = OtherTeam, TenantId = Tenant.ToString(), Name = "Forbidden fixture" },
            new Team { Id = AdminTeam, TenantId = Tenant.ToString(), Name = "Platform fixture - Administrator", IsPlatformAdmin = true });
        foreach (var (subject, role) in new[] { (Administrator, TeamRole.Administrator), (Member, TeamRole.Member), (Reviewer, TeamRole.Reviewer) })
            db.Memberships.Add(new Membership { TeamId = Team, Subject = subject, Role = role });
        db.Memberships.Add(new Membership { TeamId = AdminTeam, Subject = PlatformAdministrator, Role = TeamRole.Member });
        db.SaveChanges();
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            connection.Dispose();
            if (System.IO.Directory.Exists(storage)) System.IO.Directory.Delete(storage, true);
        }
    }
}

// Test-only directory double; never calls Microsoft Graph.
public sealed class FakeDirectory : IDirectoryInvitations
{
    public readonly ConcurrentDictionary<string, DirectoryUser> Users = new(StringComparer.OrdinalIgnoreCase);
    public readonly ConcurrentDictionary<string, string> Guests = new(StringComparer.OrdinalIgnoreCase);
    public readonly ConcurrentQueue<(string Email, Uri Redirect, string Message, InvitingUser? Inviter)> Invites = new();
    // Delegated directory view: people by object ID, searchable; DirectoryAvailable=false simulates no delegated access.
    public readonly ConcurrentDictionary<string, DirectoryPerson> People = new(StringComparer.OrdinalIgnoreCase);
    public readonly ConcurrentQueue<(string Query, InvitingUser? Caller)> Searches = new();
    public readonly ConcurrentQueue<(string Id, InvitingUser? Caller)> PersonLookups = new();
    public readonly ConcurrentDictionary<string, bool> FailingInvites = new(StringComparer.OrdinalIgnoreCase);
    public volatile bool DirectoryAvailable = true;
    public int Lookups;
    public Task<DirectoryUser?> FindUser(string email, CancellationToken ct)
    { Interlocked.Increment(ref Lookups); return Task.FromResult(Users.TryGetValue(email, out var user) ? user : null); }
    public Task<DirectoryInvitationResult> Invite(string email, Uri redirectUrl, string message, InvitingUser? inviter, CancellationToken ct)
    {
        if (FailingInvites.ContainsKey(email)) throw new DirectoryUnavailableException(502, "Microsoft Graph rejected the directory request (500).");
        Invites.Enqueue((email, redirectUrl, message, inviter));
        var id = Guests.GetOrAdd(email, _ => Guid.NewGuid().ToString());
        return Task.FromResult(new DirectoryInvitationResult(id, $"https://login.microsoftonline.com/redeem?fixture={Invites.Count}"));
    }
    public Task<IReadOnlyList<DirectoryPerson>> SearchPeople(string query, InvitingUser? caller, CancellationToken ct)
    {
        Searches.Enqueue((query, caller));
        if (!DirectoryAvailable) return Task.FromResult<IReadOnlyList<DirectoryPerson>>(DirectoryPeople.Unavailable);
        static bool Has(string? value, string q) => value?.Contains(q, StringComparison.OrdinalIgnoreCase) == true;
        var found = People.Values.Where(p => Has(p.DisplayName, query) || Has(p.Mail, query) || Has(p.UserPrincipalName, query))
            .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        return Task.FromResult<IReadOnlyList<DirectoryPerson>>(new DirectoryPeople(found, true));
    }
    public Task<DirectoryPerson?> GetPerson(string id, InvitingUser? caller, CancellationToken ct)
    {
        PersonLookups.Enqueue((id, caller));
        if (!DirectoryAvailable) throw new DirectoryUnavailableException(503, DirectorySearch.LookupUnavailable);
        return Task.FromResult(People.TryGetValue(id, out var person) ? person : null);
    }
}

public sealed class FixtureAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var subject = Request.Headers["X-Fixture-Subject"].ToString();
        if (string.IsNullOrEmpty(subject)) return Task.FromResult(AuthenticateResult.NoResult());
        var tenant = Request.Headers["X-Fixture-Tenant"].ToString();
        List<Claim> claims = [new("oid", subject), new("tid", string.IsNullOrEmpty(tenant) ? ApiFactory.Tenant.ToString() : tenant),
            new("scp", "Evaluation.Access")];
        if (Request.Headers["X-Fixture-Name"].ToString() is { Length: > 0 } name) claims.Add(new("name", name));
        if (Request.Headers["X-Fixture-Email"].ToString() is { Length: > 0 } email) claims.Add(new("preferred_username", email));
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}

public sealed class ApiTests : IDisposable
{
    private readonly ApiFactory factory = new();
    private readonly HttpClient client;
    public ApiTests() { client = factory.CreateClient(); factory.Initialize(); }
    private string TeamPath => $"/api/teams/{ApiFactory.Team}";
    private void As(string subject) { client.DefaultRequestHeaders.Remove("X-Fixture-Subject"); client.DefaultRequestHeaders.Add("X-Fixture-Subject", subject); }
    [Fact] public async Task UnauthenticatedDenied() => Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/teams")).StatusCode);
    [Fact] public async Task MembershipFiltersTeams()
    {
        As(ApiFactory.Member); var content = await client.GetStringAsync("/api/teams");
        Assert.Contains("Authorized fixture", content); Assert.DoesNotContain("Forbidden fixture", content);
    }
    [Fact] public async Task ForgedTeamDenied()
    {
        As(ApiFactory.Member); Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/teams/{ApiFactory.OtherTeam}/projects")).StatusCode);
    }
    [Fact] public async Task CrossTenantDenied()
    {
        As(ApiFactory.Member); client.DefaultRequestHeaders.Add("X-Fixture-Tenant", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(TeamPath + "/projects")).StatusCode);
    }
    [Fact] public async Task ReviewerCannotMutate()
    {
        As(ApiFactory.Reviewer); Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(TeamPath + "/projects", new { name = "Denied" })).StatusCode);
    }
    [Fact] public async Task MemberCannotChangeMembership()
    {
        As(ApiFactory.Member);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync(TeamPath + "/members",
            new { subject = ApiFactory.Member, role = "Administrator" })).StatusCode);
    }
    [Fact] public async Task LastAdministratorCannotDemoteSelf()
    {
        As(ApiFactory.Administrator);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(TeamPath + "/members",
            new { subject = ApiFactory.Administrator, role = "Reviewer" })).StatusCode);
    }
    [Fact] public async Task ReadinessFailsClosedUntilWorkerRegisters()
    {
        As(ApiFactory.Administrator);
        var readiness = JsonDocument.Parse(await client.GetStringAsync(TeamPath + "/readiness")).RootElement;
        Assert.False(readiness.GetProperty("ready").GetBoolean());
        Assert.Equal(JudgeRegistry.NotRegistered, readiness.GetProperty("blockers")[0].GetString());
        Assert.Equal(JsonValueKind.Null, readiness.GetProperty("judge").ValueKind);
        Assert.Equal(100, readiness.GetProperty("scale").GetProperty("maximum").GetDouble());
        await Register(DateTimeOffset.UtcNow);
        readiness = JsonDocument.Parse(await client.GetStringAsync(TeamPath + "/readiness")).RootElement;
        Assert.True(readiness.GetProperty("ready").GetBoolean());
        Assert.Equal(0, readiness.GetProperty("blockers").GetArrayLength());
        var judge = readiness.GetProperty("judge");
        Assert.Equal(JudgeFixtures.Contract.Version, judge.GetProperty("version").GetString());
        Assert.Equal("fixture", judge.GetProperty("judge").GetProperty("deployment").GetString());
        Assert.Equal(1, judge.GetProperty("judge").GetProperty("samples").GetInt32());
        Assert.Equal("case", judge.GetProperty("reference").GetProperty("id").GetString());
        Assert.Equal(0.4, judge.GetProperty("layerWeights").GetProperty("structure").GetDouble());
        Assert.NotEqual(JsonValueKind.Null, readiness.GetProperty("lastHeartbeat").ValueKind);
        await Register(DateTimeOffset.UtcNow.AddMinutes(-6));
        readiness = JsonDocument.Parse(await client.GetStringAsync(TeamPath + "/readiness")).RootElement;
        Assert.False(readiness.GetProperty("ready").GetBoolean());
    }
    [Fact] public async Task PolicyThresholdsUseZeroToHundredScale()
    {
        As(ApiFactory.Administrator);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(TeamPath + "/policy", new { expectedVersion = 0, dt = 0, brd = 80.5, prd = 100 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(TeamPath + "/policy", new { expectedVersion = 0, dt = 70, brd = 80, prd = 90 })).StatusCode);
        foreach (var invalid in new[] { -1, 100.01, 1000 })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(TeamPath + "/policy", new { expectedVersion = 1, dt = invalid, brd = 80, prd = 90 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync(TeamPath + "/policy",
            new StringContent("""{"expectedVersion":1,"dt":"NaN","brd":80,"prd":90}""", Encoding.UTF8, "application/json"))).StatusCode);
        var policy = JsonDocument.Parse(await client.GetStringAsync(TeamPath + "/policy")).RootElement;
        Assert.Equal(1, policy.GetProperty("version").GetInt32());
        Assert.Equal(80.5, policy.GetProperty("thresholds").GetProperty("brd").GetDouble());
        As(ApiFactory.Member);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync(TeamPath + "/policy", new { expectedVersion = 1, dt = 1, brd = 1, prd = 1 })).StatusCode);
    }
    private async Task Register(DateTimeOffset at)
    {
        using var scope = factory.Services.CreateScope();
        await JudgeRegistry.Register(scope.ServiceProvider.GetRequiredService<EvaluationDb>(), JudgeFixtures.Contract, at);
    }
    private async Task<Guid> CreateSubmission()
    {
        As(ApiFactory.Member);
        var response = await client.PostAsJsonAsync(TeamPath + "/projects", new { name = "Project fixture" });
        var project = await response.Content.ReadFromJsonAsync<Project>();
        response = await client.PostAsJsonAsync(TeamPath + $"/projects/{project!.Id}/submissions", new { name = "Submission fixture", description = "" });
        return (await response.Content.ReadFromJsonAsync<Submission>())!.Id;
    }
    private static MultipartFormDataContent Form(string role, (string Name, string Text)[] files, string?[]? paths = null)
    {
        var form = new MultipartFormDataContent { { new StringContent(role), "role" } };
        foreach (var (name, text) in files) form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(text)), "file", name);
        foreach (var path in paths ?? []) form.Add(new StringContent(path ?? ""), "paths");
        return form;
    }
    [Fact] public async Task UploadQuarantinesAndDeniesDownload()
    {
        var submission = await CreateSubmission();
        var response = await client.PostAsync(TeamPath + $"/submissions/{submission}/documents", Form("Source", [("fixture.txt", "Fixture only")]));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var version = json.GetProperty("versions")[0];
        var id = version.GetProperty("id").GetGuid();
        Assert.Equal("Quarantined", version.GetProperty("status").GetString());
        Assert.Equal(id, json.GetProperty("bundleId").GetGuid());
        Assert.Equal("fixture.txt", version.GetProperty("relativePath").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync(TeamPath + $"/documents/{id}/download")).StatusCode);
        As(ApiFactory.Reviewer);
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync(TeamPath + $"/documents/{id}/download")).StatusCode);
    }
    [Fact] public async Task DtBundleUploadSharesOneBundle()
    {
        var submission = await CreateSubmission();
        var response = await client.PostAsync(TeamPath + $"/submissions/{submission}/documents", Form("DT",
            [("stakeholder-map.md", "# Map"), ("coaching-state.md", "# State")], ["dt\\method-01-scope\\stakeholder-map.md", "dt/coaching-state.md"]));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var bundle = json.GetProperty("bundleId").GetGuid();
        Assert.All(json.GetProperty("versions").EnumerateArray(), v => Assert.Equal(bundle, v.GetProperty("bundleId").GetGuid()));
        var detail = JsonDocument.Parse(await client.GetStringAsync(TeamPath + $"/submissions/{submission}")).RootElement;
        var paths = detail.GetProperty("documents").EnumerateArray().Select(d => d.GetProperty("relativePath").GetString()!).Order().ToArray();
        Assert.Equal(["dt/coaching-state.md", "dt/method-01-scope/stakeholder-map.md"], paths);
    }
    [Theory]
    [InlineData("../escape.md")]
    [InlineData("/absolute.md")]
    [InlineData("C:/drive.md")]
    [InlineData("a//empty.md")]
    [InlineData("./dot.md")]
    [InlineData("notes.txt")]
    [InlineData("")]
    public async Task DtUploadPathValidation(string path)
    {
        var submission = await CreateSubmission();
        var response = await client.PostAsync(TeamPath + $"/submissions/{submission}/documents", Form("DT", [("file.md", "# A")], [path]));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
    [Fact] public async Task DtUploadRejectsDuplicatesMissingPathsAndLongPaths()
    {
        var submission = await CreateSubmission();
        var url = TeamPath + $"/submissions/{submission}/documents";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(url, Form("DT", [("a.md", "# A"), ("b.md", "# B")], ["x/A.md", "x/a.md"]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(url, Form("DT", [("a.md", "# A"), ("b.md", "# B")], ["a.md"]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(url, Form("DT", [("a.md", "# A")]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(url, Form("DT", [("a.md", "# A")], [new string('a', 258) + ".md"]))).StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.Empty(scope.ServiceProvider.GetRequiredService<EvaluationDb>().Documents);
    }
    [Fact] public async Task SingleFileRolesTakeExactlyOneFile()
    {
        var submission = await CreateSubmission();
        var url = TeamPath + $"/submissions/{submission}/documents";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(url, Form("BRD", [("a.md", "# A"), ("b.md", "# B")]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(url, Form("PRD", [("a.md", "# A")], ["a.md"]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(url, Form("BRD", [("a.exe", "MZ")]))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync(url, Form("PRD", [("prd.pdf", "%PDF-")]))).StatusCode);
    }
    [Fact] public async Task SourceFolderUploadSharesOneBundleWithMixedFormats()
    {
        var submission = await CreateSubmission();
        var response = await client.PostAsync(TeamPath + $"/submissions/{submission}/documents", Form("Source",
            [("interview.txt", "Notes"), ("survey.pdf", "%PDF-"), ("report.docx", "PK"), ("summary.md", "# S")],
            ["research\\interviews\\interview.txt", "research/survey.pdf", "research/report.docx", "research/summary.md"]));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var bundle = json.GetProperty("bundleId").GetGuid();
        var versions = json.GetProperty("versions").EnumerateArray().ToArray();
        Assert.Equal(4, versions.Length);
        Assert.All(versions, v => { Assert.Equal(bundle, v.GetProperty("bundleId").GetGuid()); Assert.Equal("Source", v.GetProperty("role").GetString()); });
        Assert.Equal(4, versions.Select(v => v.GetProperty("id").GetGuid()).Distinct().Count());
        Assert.Contains(versions, v => v.GetProperty("relativePath").GetString() == "research/interviews/interview.txt" &&
            v.GetProperty("fileName").GetString() == "interview.txt");
    }
    [Fact] public async Task SourceUploadValidatesPathsCountsAndExtensions()
    {
        var submission = await CreateSubmission();
        var url = TeamPath + $"/submissions/{submission}/documents";
        // Multi-file uploads need one path per file; single files may omit paths.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(url, Form("Source", [("a.txt", "A"), ("b.txt", "B")]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(url, Form("Source", [("a.txt", "A"), ("b.txt", "B")], ["x/A.txt", "x/a.txt"]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(url, Form("Source", [("a.txt", "A")], ["../a.txt"]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(url, Form("Source", [("a.txt", "A"), ("b.png", "B")], ["x/a.txt", "x/b.png"]))).StatusCode);
        var tooMany = Enumerable.Range(0, Artifacts.MaximumSourceFiles + 1).Select(i => ($"{i}.txt", "x")).ToArray();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(url, Form("Source", tooMany, tooMany.Select(f => (string?)$"x/{f.Item1}").ToArray()))).StatusCode);
        using (var scope = factory.Services.CreateScope()) Assert.Empty(scope.ServiceProvider.GetRequiredService<EvaluationDb>().Documents);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync(url, Form("Source", [("single.pdf", "%PDF-")], ["folder/single.pdf"]))).StatusCode);
    }
    [Fact] public async Task ScansUnavailableAndNoOutboxCreated()
    {
        var submission = await CreateSubmission();
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await client.PostAsJsonAsync(TeamPath + $"/submissions/{submission}/scans",
            new { targetVersionIds = new[] { Guid.NewGuid() }, evidenceVersionIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(JudgeRegistry.NotRegistered, await response.Content.ReadAsStringAsync());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EvaluationDb>();
        Assert.Empty(db.Scans); Assert.Empty(db.Outbox);
    }

    private async Task<(Guid Submission, DocumentVersion[] Dt, DocumentVersion Brd, DocumentVersion Brd2, DocumentVersion Prd, DocumentVersion Source)> ReadyScanFixture()
    {
        var submission = await CreateSubmission();
        As(ApiFactory.Administrator);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(TeamPath + "/policy", new { expectedVersion = 0, dt = 70, brd = 80, prd = 90 })).StatusCode);
        await Register(DateTimeOffset.UtcNow);
        As(ApiFactory.Member);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EvaluationDb>();
        var bundle = Guid.NewGuid();
        DocumentVersion Doc(DocumentRole role, string path, Guid? bundleId = null)
        {
            var id = Guid.NewGuid();
            var doc = new DocumentVersion { Id = id, TeamId = ApiFactory.Team, SubmissionId = submission, Role = role, FileName = Path.GetFileName(path),
                RelativePath = path, BundleId = bundleId ?? id, Status = "Ready", Hash = "hash", TextLength = 10, BlobKey = Guid.NewGuid().ToString("N") };
            db.Documents.Add(doc); return doc;
        }
        var result = (submission, new[] { Doc(DocumentRole.DT, "dt/a.md", bundle), Doc(DocumentRole.DT, "dt/b.md", bundle) },
            Doc(DocumentRole.BRD, "brd.md"), Doc(DocumentRole.BRD, "brd-v2.md"), Doc(DocumentRole.PRD, "prd.md"), Doc(DocumentRole.Source, "source.txt"));
        await db.SaveChangesAsync();
        return result;
    }
    private Task<HttpResponseMessage> StartScan(Guid submission, IEnumerable<Guid> targets, IEnumerable<Guid>? evidence = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, TeamPath + $"/submissions/{submission}/scans")
        { Content = JsonContent.Create(new { targetVersionIds = targets.ToArray(), evidenceVersionIds = (evidence ?? []).ToArray() }) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }
    [Fact] public async Task ScanSnapshotsRegisteredContractAndArtifacts()
    {
        var f = await ReadyScanFixture();
        var response = await StartScan(f.Submission, [.. f.Dt.Select(d => d.Id), f.Brd.Id, f.Prd.Id], [f.Source.Id]);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var id = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var scan = JsonDocument.Parse(await client.GetStringAsync(TeamPath + $"/scans/{id}")).RootElement;
        Assert.Equal(JudgeFixtures.Contract.Version, scan.GetProperty("judge").GetProperty("version").GetString());
        Assert.Equal(0.3, scan.GetProperty("judge").GetProperty("layerWeights").GetProperty("judge").GetDouble());
        Assert.Equal(70, scan.GetProperty("policy").GetProperty("dt").GetDouble());
        Assert.Equal(5, scan.GetProperty("inputs").GetArrayLength());
        Assert.Contains(scan.GetProperty("inputs").EnumerateArray(), i => i.GetProperty("relativePath").GetString() == "dt/a.md" &&
            i.GetProperty("bundleId").GetGuid() == f.Dt[0].BundleId);
        Assert.Equal(JsonValueKind.Null, scan.GetProperty("result").ValueKind);
        using var scope = factory.Services.CreateScope();
        Assert.Single(scope.ServiceProvider.GetRequiredService<EvaluationDb>().Outbox);
    }
    [Fact] public async Task SourceEvidenceIsOptional()
    {
        var f = await ReadyScanFixture();
        Assert.Equal(HttpStatusCode.Accepted, (await StartScan(f.Submission, [f.Brd.Id])).StatusCode);
    }
    [Fact] public async Task ScanAcceptsSeveralEvidenceFilesFromOneSourceUpload()
    {
        var f = await ReadyScanFixture();
        Guid[] evidence;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EvaluationDb>();
            var bundle = Guid.NewGuid();
            var docs = Enumerable.Range(0, 3).Select(i => new DocumentVersion { Id = Guid.NewGuid(), TeamId = ApiFactory.Team, SubmissionId = f.Submission,
                Role = DocumentRole.Source, FileName = $"n{i}.txt", RelativePath = $"research/n{i}.txt", BundleId = bundle, Status = "Ready",
                Hash = "hash", TextLength = 5, BlobKey = Guid.NewGuid().ToString("N") }).ToArray();
            db.Documents.AddRange(docs); await db.SaveChangesAsync();
            evidence = [f.Source.Id, docs[0].Id, docs[2].Id]; // A subset of the folder plus another upload.
        }
        var response = await StartScan(f.Submission, [f.Brd.Id], evidence);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var id = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var scan = JsonDocument.Parse(await client.GetStringAsync(TeamPath + $"/scans/{id}")).RootElement;
        var sources = scan.GetProperty("inputs").EnumerateArray().Where(i => i.GetProperty("role").GetString() == "Source")
            .Select(i => i.GetProperty("relativePath").GetString()!).Order().ToArray();
        Assert.Equal(["research/n0.txt", "research/n2.txt", "source.txt"], sources);
        Assert.Equal(HttpStatusCode.BadRequest, (await StartScan(f.Submission, [f.Brd.Id],
            Enumerable.Range(0, Artifacts.MaximumSourceFiles + 1).Select(_ => Guid.NewGuid()))).StatusCode);
    }
    [Fact] public async Task ScanSelectionRules()
    {
        var f = await ReadyScanFixture();
        Assert.Equal(HttpStatusCode.BadRequest, (await StartScan(f.Submission, [f.Dt[0].Id])).StatusCode); // Incomplete DT bundle.
        Assert.Equal(HttpStatusCode.BadRequest, (await StartScan(f.Submission, [f.Brd.Id, f.Brd2.Id])).StatusCode); // Two BRDs.
        Assert.Equal(HttpStatusCode.BadRequest, (await StartScan(f.Submission, [f.Source.Id])).StatusCode); // Source is context only.
        Assert.Equal(HttpStatusCode.BadRequest, (await StartScan(f.Submission, [f.Prd.Id], [f.Brd.Id])).StatusCode); // Evidence must be Source.
        Assert.Equal(HttpStatusCode.BadRequest, (await StartScan(f.Submission, [])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await StartScan(f.Submission, [f.Prd.Id, Guid.NewGuid()])).StatusCode); // Not owned.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EvaluationDb>();
        Assert.Empty(db.Scans);
        db.Documents.Single(x => x.Id == f.Dt[1].Id).Status = "Quarantined"; db.SaveChanges();
        Assert.Equal(HttpStatusCode.BadRequest, (await StartScan(f.Submission, f.Dt.Select(d => d.Id))).StatusCode); // Bundle not ready.
    }
    [Fact] public async Task StaleRegistrationBlocksScans()
    {
        var f = await ReadyScanFixture();
        await Register(DateTimeOffset.UtcNow.AddMinutes(-10));
        var response = await StartScan(f.Submission, [f.Brd.Id]);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("has not registered recently", await response.Content.ReadAsStringAsync());
    }    [Fact] public async Task ArbitraryImportsNeverFetched()
    {
        var submission = await CreateSubmission();
        var response = await client.PostAsJsonAsync(TeamPath + $"/submissions/{submission}/imports",
            new { url = "http://169.254.169.254/metadata/identity/oauth2/token" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
    public void Dispose() { client.Dispose(); factory.Dispose(); }
}
