using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Evaluation.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Evaluation.Tests;

public sealed class PlatformTests : IDisposable
{
    private readonly ApiFactory factory;
    private readonly HttpClient client;
    public PlatformTests() : this(new ApiFactory()) { }
    private PlatformTests(ApiFactory factory) { this.factory = factory; client = factory.CreateClient(); factory.Initialize(); }
    private static string Admin => ApiFactory.PlatformAdministrator;
    private void As(string subject, string? name = null, string? email = null) => As(client, subject, name, email);
    private static void As(HttpClient http, string subject, string? name = null, string? email = null)
    {
        foreach (var header in new[] { "X-Fixture-Subject", "X-Fixture-Name", "X-Fixture-Email" }) http.DefaultRequestHeaders.Remove(header);
        http.DefaultRequestHeaders.Add("X-Fixture-Subject", subject);
        if (name is not null) http.DefaultRequestHeaders.Add("X-Fixture-Name", name);
        if (email is not null) http.DefaultRequestHeaders.Add("X-Fixture-Email", email);
    }
    private T Data<T>(Func<EvaluationDb, T> read)
    {
        using var scope = factory.Services.CreateScope();
        return read(scope.ServiceProvider.GetRequiredService<EvaluationDb>());
    }
    private void Expire(Guid invitation, TimeSpan fromNow)
    {
        var expiresAt = DateTimeOffset.UtcNow + fromNow;
        Data(db => db.Invitations.Where(i => i.Id == invitation).ExecuteUpdate(s => s.SetProperty(i => i.ExpiresAt, expiresAt)));
    }
    private static async Task<JsonElement> Json(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    private Task<HttpResponseMessage> DeleteTeam(Guid team, object? body) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/teams/{team}") { Content = body is null ? null : JsonContent.Create(body) });
    private Task<HttpResponseMessage> Invite(string email, Guid? suggested = null) =>
        client.PostAsJsonAsync("/api/admin/invitations", new { email, suggestedTeamId = suggested });

    [Fact] public async Task PlatformAdministratorIsDetectedFromAdminTeamMembership()
    {
        As(Admin);
        var me = await Json(await client.GetAsync("/api/me"));
        Assert.True(me.GetProperty("isPlatformAdmin").GetBoolean());
        Assert.Equal(ApiFactory.AdminTeam, me.GetProperty("platformAdminTeamId").GetGuid());
        Assert.Equal(Admin, me.GetProperty("subject").GetString());
        Assert.Equal(ApiFactory.Tenant.ToString(), me.GetProperty("tenantId").GetString());
        var membership = Assert.Single(me.GetProperty("memberships").EnumerateArray());
        Assert.True(membership.GetProperty("isPlatformAdmin").GetBoolean());
        Assert.Equal("Member", membership.GetProperty("role").GetString());
        Assert.Equal(0, me.GetProperty("pendingInvitationCount").GetInt32());
        As(ApiFactory.Administrator);
        me = await Json(await client.GetAsync("/api/me"));
        Assert.False(me.GetProperty("isPlatformAdmin").GetBoolean());
        Assert.Equal(JsonValueKind.Null, me.GetProperty("platformAdminTeamId").ValueKind);
        Assert.Equal("Administrator", Assert.Single(me.GetProperty("memberships").EnumerateArray()).GetProperty("role").GetString());
        // Same object ID in another tenant is not a platform administrator.
        As(Admin); client.DefaultRequestHeaders.Add("X-Fixture-Tenant", Guid.NewGuid().ToString());
        me = await Json(await client.GetAsync("/api/me"));
        Assert.False(me.GetProperty("isPlatformAdmin").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/admin/teams")).StatusCode);
    }

    [Fact] public async Task MeRefreshesDisplayClaimsWithoutClearingThem()
    {
        As(ApiFactory.Member, "Fixture Person", "Person@Example.Test");
        Assert.Equal("Fixture Person", (await Json(await client.GetAsync("/api/me"))).GetProperty("name").GetString());
        var stored = Data(db => db.Memberships.AsNoTracking().Single(m => m.Subject == ApiFactory.Member));
        Assert.Equal("person@example.test", stored.Email); Assert.Equal("Fixture Person", stored.DisplayName);
        As(ApiFactory.Member);
        await client.GetAsync("/api/me");
        stored = Data(db => db.Memberships.AsNoTracking().Single(m => m.Subject == ApiFactory.Member));
        Assert.Equal("person@example.test", stored.Email);
    }

    [Fact] public async Task TeamsShowAllTenantTeamsReadOnlyForPlatformAdministrators()
    {
        As(Admin);
        var teams = (await Json(await client.GetAsync("/api/teams"))).EnumerateArray().ToArray();
        Assert.Equal(3, teams.Length);
        var other = teams.Single(t => t.GetProperty("id").GetGuid() == ApiFactory.OtherTeam);
        Assert.Equal(Platform.ReadOnlyRole, other.GetProperty("role").GetString());
        Assert.False(other.GetProperty("isPlatformAdmin").GetBoolean());
        var own = teams.Single(t => t.GetProperty("id").GetGuid() == ApiFactory.AdminTeam);
        Assert.Equal("Member", own.GetProperty("role").GetString());
        Assert.True(own.GetProperty("isPlatformAdmin").GetBoolean());
        As(ApiFactory.Member);
        var member = Assert.Single((await Json(await client.GetAsync("/api/teams"))).EnumerateArray());
        Assert.Equal(ApiFactory.Team, member.GetProperty("id").GetGuid());
        Assert.Equal("Member", member.GetProperty("role").GetString());
        Assert.False(member.GetProperty("isPlatformAdmin").GetBoolean());
    }

    [Fact] public async Task PlatformAdministratorReadsButCannotMutateOtherTeams()
    {
        As(ApiFactory.Member);
        var project = await (await client.PostAsJsonAsync($"/api/teams/{ApiFactory.Team}/projects", new { name = "Readable project" })).Content.ReadFromJsonAsync<Project>();
        var submission = await (await client.PostAsJsonAsync($"/api/teams/{ApiFactory.Team}/projects/{project!.Id}/submissions",
            new { name = "Readable submission", description = "" })).Content.ReadFromJsonAsync<Submission>();
        As(Admin);
        var path = $"/api/teams/{ApiFactory.Team}";
        foreach (var url in new[] { "/projects", $"/projects/{project.Id}", $"/projects/{project.Id}/submissions", "/readiness", "/policy",
            $"/submissions/{submission!.Id}", $"/submissions/{submission.Id}/scans" })
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path + url)).StatusCode);
        Assert.Contains("Readable project", await client.GetStringAsync(path + "/projects"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(path + "/projects", new { name = "Denied" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"{path}/projects/{project.Id}/submissions", new { name = "Denied", description = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"{path}/submissions/{submission.Id}/documents", new MultipartFormDataContent())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"{path}/submissions/{submission.Id}/scans", new { targetVersionIds = new[] { Guid.NewGuid() } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{path}/members/{ApiFactory.Reviewer}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync(path + "/policy", new { expectedVersion = 0, dt = 1, brd = 1, prd = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path + "/members")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync(path + "/members", new { subject = Admin, role = "Administrator" })).StatusCode);
        // Ordinary members never gain cross-team reads.
        As(ApiFactory.Member);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/teams/{ApiFactory.OtherTeam}/projects")).StatusCode);
    }

    [Fact] public async Task NonPlatformAdministratorsGet404OnAdminEndpoints()
    {
        foreach (var subject in new[] { ApiFactory.Administrator, ApiFactory.Member, ApiFactory.Reviewer, Guid.NewGuid().ToString() })
        {
            As(subject);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/admin/teams")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/admin/teams", new { name = "Denied" })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await DeleteTeam(ApiFactory.OtherTeam, new { confirmName = "Forbidden fixture" })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/admin/teams/{ApiFactory.Team}/members")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/admin/invitations")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await Invite("someone@example.test")).StatusCode);
        }
        Assert.True(Data(db => db.Teams.Count()) == 3);
        Assert.Empty(factory.Directory.Invites);
    }

    [Fact] public async Task CreateTeamValidatesAndEnforcesCaseInsensitiveUniqueness()
    {
        As(Admin);
        var created = await client.PostAsJsonAsync("/api/admin/teams", new { name = "  Response Team  " });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await Json(created);
        Assert.Equal("Response Team", body.GetProperty("name").GetString());
        Assert.False(body.GetProperty("isPlatformAdmin").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/admin/teams", new { name = "response TEAM" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/admin/teams", new { name = "forbidden fixture" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/admin/teams", new { name = "Équipe Rouge" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/admin/teams", new { name = "ÉQUIPE rouge" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/admin/teams", new { name = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/admin/teams", new { name = new string('x', 201) })).StatusCode);
        var list = (await Json(await client.GetAsync("/api/admin/teams"))).EnumerateArray().ToArray();
        Assert.Equal(5, list.Length);
        Assert.True(list[0].GetProperty("isPlatformAdmin").GetBoolean());
        var team = list.Single(t => t.GetProperty("name").GetString() == "Authorized fixture");
        Assert.Equal(3, team.GetProperty("members").GetInt32());
        Assert.Equal(0, team.GetProperty("projects").GetInt32());
        Assert.Equal(2, Data(db => db.PlatformAudit.Count(a => a.Action == "TeamCreated" && a.Actor == Admin)));
        // The creator is the new team's Administrator and can use it immediately.
        var createdId = body.GetProperty("id").GetGuid();
        Assert.Equal("Administrator", body.GetProperty("role").GetString());
        Assert.Equal(1, list.Single(t => t.GetProperty("id").GetGuid() == createdId).GetProperty("members").GetInt32());
        Assert.Equal(TeamRole.Administrator, Data(db => db.Memberships.Single(m => m.TeamId == createdId).Role));
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"/api/teams/{createdId}/projects", new { name = "First project" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/teams/{createdId}/policy",
            new { expectedVersion = 0, dt = 70, brd = 70, prd = 70 })).StatusCode);
        // The unique index backs the code check.
        Assert.Throws<DbUpdateException>(() => Data(db =>
        { db.Teams.Add(new Team { Id = Guid.NewGuid(), TenantId = ApiFactory.Tenant.ToString(), Name = "Response Team" }); return db.SaveChanges(); }));
    }

    [Fact] public async Task PlatformAdministratorJoinsAsMemberWithFullReadWriteAndCanLeave()
    {
        As(Admin, "Platform Admin", "admin@apex.example");
        var path = $"/api/teams/{ApiFactory.OtherTeam}";
        // Read-only before joining.
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(path + "/projects", new { name = "Before join" })).StatusCode);
        var joined = await client.PostAsync(path + "/join", null);
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        Assert.Equal("Member", (await Json(joined)).GetProperty("role").GetString());
        var membership = Data(db => db.Memberships.Single(m => m.TeamId == ApiFactory.OtherTeam && m.Subject == Admin));
        Assert.Equal(TeamRole.Member, membership.Role);
        Assert.Equal("admin@apex.example", membership.Email);
        Assert.Equal("Member", (await Json(await client.GetAsync("/api/teams"))).EnumerateArray()
            .Single(t => t.GetProperty("id").GetGuid() == ApiFactory.OtherTeam).GetProperty("role").GetString());
        // Full read/write: projects, submissions, uploads.
        var project = await (await client.PostAsJsonAsync(path + "/projects", new { name = "Joined project" })).Content.ReadFromJsonAsync<Project>();
        Assert.NotNull(project);
        var submission = await client.PostAsJsonAsync($"{path}/projects/{project!.Id}/submissions", new { name = "Joined submission", description = "" });
        Assert.Equal(HttpStatusCode.Created, submission.StatusCode);
        // Platform administrators who joined manage the team's settings like a team Administrator.
        Assert.Equal("Member", (await Json(await client.GetAsync("/api/teams"))).EnumerateArray()
            .Single(t => t.GetProperty("id").GetGuid() == ApiFactory.OtherTeam).GetProperty("role").GetString());
        Assert.True((await Json(await client.GetAsync("/api/teams"))).EnumerateArray()
            .Single(t => t.GetProperty("id").GetGuid() == ApiFactory.OtherTeam).GetProperty("canManage").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(path + "/policy", new { expectedVersion = 0, dt = 60, brd = 65, prd = 70 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path + "/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(path + "/join", null)).StatusCode);
        Assert.Equal(1, Data(db => db.PlatformAudit.Count(a => a.Action == "PlatformAdminJoined" && a.Resource == ApiFactory.OtherTeam.ToString())));
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync(path + "/leave", null)).StatusCode);
        Assert.False(Data(db => db.Memberships.Any(m => m.TeamId == ApiFactory.OtherTeam && m.Subject == Admin)));
        Assert.Equal("PlatformAdmin", (await Json(await client.GetAsync("/api/teams"))).EnumerateArray()
            .Single(t => t.GetProperty("id").GetGuid() == ApiFactory.OtherTeam).GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(path + "/leave", null)).StatusCode);
    }

    [Fact] public async Task OrdinaryMembersStillCannotManageSettingsAndAdminTeamKeepsRealRoles()
    {
        As(ApiFactory.Member);
        var path = $"/api/teams/{ApiFactory.Team}";
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync(path + "/policy", new { expectedVersion = 0, dt = 1, brd = 1, prd = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path + "/members")).StatusCode);
        Assert.False((await Json(await client.GetAsync("/api/teams"))).EnumerateArray()
            .Single(t => t.GetProperty("id").GetGuid() == ApiFactory.Team).GetProperty("canManage").GetBoolean());
        // A platform administrator who is not an Administrator of the administration team gets no elevated rights there.
        var role = Data(db => db.Memberships.Single(m => m.TeamId == ApiFactory.AdminTeam && m.Subject == Admin).Role);
        As(Admin);
        var adminTeam = (await Json(await client.GetAsync("/api/teams"))).EnumerateArray().Single(t => t.GetProperty("id").GetGuid() == ApiFactory.AdminTeam);
        Assert.Equal(role == TeamRole.Administrator, adminTeam.GetProperty("canManage").GetBoolean());
        if (role != TeamRole.Administrator)
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/teams/{ApiFactory.AdminTeam}/members")).StatusCode);
        // Read-only (not joined) teams stay read-only.
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/teams/{ApiFactory.OtherTeam}/policy",
            new { expectedVersion = 0, dt = 1, brd = 1, prd = 1 })).StatusCode);
    }

    [Fact] public async Task JoinAndLeaveExcludeTheAdminTeam()
    {
        As(Admin);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/teams/{ApiFactory.AdminTeam}/join", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/teams/{ApiFactory.AdminTeam}/leave", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/teams/{Guid.NewGuid()}/join", null)).StatusCode);
        // Self-service: ordinary members join any non-administration team, never the administration team.
        As(ApiFactory.Member);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/teams/{ApiFactory.AdminTeam}/join", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/teams/{ApiFactory.AdminTeam}/leave", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/teams/{ApiFactory.OtherTeam}/leave", null)).StatusCode);
        Assert.False(Data(db => db.Memberships.Any(m => m.TeamId == ApiFactory.AdminTeam && m.Subject == ApiFactory.Member)));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/teams/{ApiFactory.OtherTeam}/join", null)).StatusCode);
        Assert.Equal(TeamRole.Member, Data(db => db.Memberships.Single(m => m.TeamId == ApiFactory.OtherTeam && m.Subject == ApiFactory.Member).Role));
        Assert.Equal(0, Data(db => db.PlatformAudit.Count(a => a.Action == "PlatformAdminJoined")));
    }

    [Fact] public async Task LastAdministratorCannotLeaveACreatedTeam()
    {
        As(Admin);
        var id = (await Json(await client.PostAsJsonAsync("/api/admin/teams", new { name = "Solo team" }))).GetProperty("id").GetGuid();
        var response = await client.PostAsync($"/api/teams/{id}/leave", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("At least one administrator", await response.Content.ReadAsStringAsync());
    }

    [Fact] public async Task PlatformAdminTeamCannotBeDeleted()
    {
        As(Admin);
        var response = await DeleteTeam(ApiFactory.AdminTeam, new { confirmName = "Platform fixture - Administrator" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("cannot be deleted", await response.Content.ReadAsStringAsync());
        Assert.True(Data(db => db.Teams.Any(t => t.Id == ApiFactory.AdminTeam)));
    }

    [Fact] public async Task DeleteRequiresExactNameAndUnknownTeamsAreHidden()
    {
        As(Admin);
        Assert.Equal(HttpStatusCode.BadRequest, (await DeleteTeam(ApiFactory.OtherTeam, new { confirmName = "forbidden fixture" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await DeleteTeam(ApiFactory.OtherTeam, new { confirmName = "Forbidden fixture " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await DeleteTeam(ApiFactory.OtherTeam, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await DeleteTeam(Guid.NewGuid(), new { confirmName = "x" })).StatusCode);
        Assert.True(Data(db => db.Teams.Any(t => t.Id == ApiFactory.OtherTeam)));
    }

    private async Task<(string[] Keys, Guid Scan)> SeedTeam(Guid team, string scanStatus, string outboxState)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EvaluationDb>();
        var files = scope.ServiceProvider.GetRequiredService<PrivateFiles>();
        var project = Guid.NewGuid(); var submission = Guid.NewGuid(); var document = Guid.NewGuid(); var scan = Guid.NewGuid();
        var blob = await files.StoreText("original fixture", default); var text = await files.StoreText("extracted fixture", default);
        db.Projects.Add(new Project { Id = project, TeamId = team, Name = "Doomed project" });
        db.Submissions.Add(new Submission { Id = submission, TeamId = team, ProjectId = project, Name = "Doomed submission" });
        db.Documents.Add(new DocumentVersion { Id = document, TeamId = team, SubmissionId = submission, BlobKey = blob, TextKey = text, Status = "Ready",
            FileName = "brd.md", RelativePath = "brd.md", BundleId = document, Role = DocumentRole.BRD, Hash = "h" });
        db.Scans.Add(new Scan { Id = scan, TeamId = team, SubmissionId = submission, IdempotencyKey = Guid.NewGuid().ToString(), Status = scanStatus });
        db.ScanInputs.Add(new ScanInput { TeamId = team, ScanId = scan, VersionId = document });
        db.Outbox.Add(new OutboxEvent { Id = Guid.NewGuid(), TeamId = team, ScanId = scan, State = outboxState });
        db.Policies.Add(new PolicyVersion { Id = Guid.NewGuid(), TeamId = team, Version = 1, Snapshot = "{}", Actor = "fixture" });
        db.Audit.Add(new AuditEvent { Id = Guid.NewGuid(), TeamId = team, Actor = "fixture", Action = "Seeded", Resource = "fixture" });
        db.Memberships.Add(new Membership { TeamId = team, Subject = Guid.NewGuid().ToString(), Role = TeamRole.Administrator });
        db.Invitations.Add(new Invitation { Id = Guid.NewGuid(), TenantId = ApiFactory.Tenant.ToString(), Email = "x@example.test", SuggestedTeamId = team,
            InvitedSubject = Guid.NewGuid().ToString(), InvitedBy = Admin, ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) });
        await db.SaveChangesAsync();
        return ([blob, text], scan);
    }

    [Theory]
    [InlineData("Queued", "Dispatched")]
    [InlineData("Evaluating", "Dispatched")]
    [InlineData("Completed", "Leased")]
    [InlineData("Completed", "Pending")]
    public async Task DeleteIsBlockedByActiveScans(string scanStatus, string outboxState)
    {
        var (keys, _) = await SeedTeam(ApiFactory.OtherTeam, scanStatus, outboxState);
        As(Admin);
        var response = await DeleteTeam(ApiFactory.OtherTeam, new { confirmName = "Forbidden fixture" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("running scans", await response.Content.ReadAsStringAsync());
        Assert.True(Data(db => db.Teams.Any(t => t.Id == ApiFactory.OtherTeam) && db.Documents.Any(d => d.TeamId == ApiFactory.OtherTeam)));
        var files = factory.Services.GetRequiredService<PrivateFiles>();
        foreach (var key in keys) files.Open(key).Dispose();
    }

    [Fact] public async Task DeleteRemovesEveryTeamRowAndPrivateObjectButLeavesOtherTeams()
    {
        var (keys, _) = await SeedTeam(ApiFactory.OtherTeam, "Completed", "Dispatched");
        var (keptKeys, _) = await SeedTeam(ApiFactory.Team, "Completed", "Dispatched");
        As(Admin);
        var response = await DeleteTeam(ApiFactory.OtherTeam, new { confirmName = "Forbidden fixture" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(2, body.GetProperty("objectsDeleted").GetInt32());
        Assert.Equal(0, body.GetProperty("objectDeleteFailures").GetInt32());
        foreach (var table in TeamDeletion.Order) Assert.Equal(1, body.GetProperty("rows").GetProperty(table).GetInt32());
        var t = ApiFactory.OtherTeam;
        Assert.True(Data(db => !db.Teams.Any(x => x.Id == t) && !db.Memberships.Any(x => x.TeamId == t) && !db.Projects.Any(x => x.TeamId == t) &&
            !db.Submissions.Any(x => x.TeamId == t) && !db.Documents.Any(x => x.TeamId == t) && !db.Scans.Any(x => x.TeamId == t) &&
            !db.ScanInputs.Any(x => x.TeamId == t) && !db.Outbox.Any(x => x.TeamId == t) && !db.Policies.Any(x => x.TeamId == t) && !db.Audit.Any(x => x.TeamId == t)));
        Assert.True(Data(db => db.Invitations.Any(i => i.SuggestedTeamId == null) && !db.Invitations.Any(i => i.SuggestedTeamId == t)));
        var files = factory.Services.GetRequiredService<PrivateFiles>();
        foreach (var key in keys) Assert.Throws<FileNotFoundException>(() => files.Open(key));
        foreach (var key in keptKeys) files.Open(key).Dispose();
        var kept = ApiFactory.Team;
        Assert.True(Data(db => db.Teams.Any(x => x.Id == kept) && db.Memberships.Count(x => x.TeamId == kept) == 4 &&
            db.Documents.Any(x => x.TeamId == kept) && db.Scans.Any(x => x.TeamId == kept) && db.Audit.Any(x => x.TeamId == kept) && db.Outbox.Any(x => x.TeamId == kept)));
        var audit = Data(db => db.PlatformAudit.AsNoTracking().Where(a => a.Resource == t.ToString()).ToList());
        Assert.Contains(audit, a => a.Action == "TeamDeleted" && a.Detail.Contains("Documents=1") && a.Detail.Contains("PrivateObjectsScheduled=2"));
        Assert.Contains(audit, a => a.Action == "TeamObjectsDeleted" && a.Detail == "deleted=2, failed=0");
        Assert.All(audit, a => Assert.DoesNotContain(keys[0], a.Detail));
    }

    [Fact] public async Task TeamAdministratorsCanRemoveMembersButNotTheLastAdministrator()
    {
        As(ApiFactory.Administrator);
        var path = $"/api/teams/{ApiFactory.Team}/members";
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"{path}/{ApiFactory.Administrator}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{path}/{ApiFactory.Reviewer}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{path}/{ApiFactory.Reviewer}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync($"{path}/not-a-guid")).StatusCode);
        Assert.Equal(1, Data(db => db.Audit.Count(a => a.Action == "MembershipRemoved" && a.Resource == ApiFactory.Reviewer)));
        As(ApiFactory.Member);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{path}/{ApiFactory.Administrator}")).StatusCode);
    }

    [Fact] public async Task PlatformAdministratorsManageAnyTeamsMemberships()
    {
        As(Admin);
        var subject = Guid.NewGuid().ToString().ToUpperInvariant();
        var path = $"/api/admin/teams/{ApiFactory.OtherTeam}/members";
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"{path}/{subject}", new { role = "Administrator" })).StatusCode);
        var members = (await Json(await client.GetAsync(path))).EnumerateArray().ToArray();
        Assert.Equal(subject.ToLowerInvariant(), Assert.Single(members).GetProperty("subject").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"{path}/{subject}", new { role = "Member" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"{path}/{subject}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{path}/not-a-guid", new { role = "Member" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/admin/teams/{Guid.NewGuid()}/members")).StatusCode);
        // The platform-admin team keeps at least one member even when nobody holds the Administrator role.
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/admin/teams/{ApiFactory.AdminTeam}/members/{Admin}")).StatusCode);
        var second = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/admin/teams/{ApiFactory.AdminTeam}/members/{second}", new { role = "Reviewer" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/admin/teams/{ApiFactory.AdminTeam}/members/{second}")).StatusCode);
        Assert.True(Data(db => db.PlatformAudit.Count(a => a.Action == "AdminMembershipChanged") == 2 && db.PlatformAudit.Count(a => a.Action == "AdminMembershipRemoved") == 1));
    }

    [Fact] public async Task EmailInvitationUsesGraphAndListsForAdministrators()
    {
        As(Admin);
        var response = await Invite(" Guest@Partner.Example ", ApiFactory.Team);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal("email", body.GetProperty("delivery").GetString());
        var invitation = body.GetProperty("invitation");
        var id = invitation.GetProperty("id").GetGuid();
        Assert.Equal("guest@partner.example", invitation.GetProperty("email").GetString());
        Assert.Equal("Pending", invitation.GetProperty("status").GetString());
        Assert.Equal("Authorized fixture", invitation.GetProperty("suggestedTeamName").GetString());
        Assert.StartsWith("https://login.microsoftonline.com/redeem", invitation.GetProperty("redeemUrl").GetString());
        var sent = Assert.Single(factory.Directory.Invites);
        Assert.Equal("guest@partner.example", sent.Email);
        Assert.Equal($"{ApiFactory.RedirectUrl}?invitation={id}#evaluation", sent.Redirect.AbsoluteUri);
        Assert.Contains("Apex Evaluation workspace", sent.Message);
        Assert.Contains("Authorized fixture", sent.Message);
        var stored = Data(db => db.Invitations.AsNoTracking().Single());
        Assert.Equal(factory.Directory.Guests["guest@partner.example"], stored.InvitedSubject);
        Assert.Equal(stored.CreatedAt + TimeSpan.FromDays(14), stored.ExpiresAt);
        var list = (await Json(await client.GetAsync("/api/admin/invitations"))).EnumerateArray().ToArray();
        Assert.Equal(id, Assert.Single(list).GetProperty("id").GetGuid());
        Assert.Equal(1, Data(db => db.PlatformAudit.Count(a => a.Action == "InvitationCreated")));
        Assert.Equal("app", body.GetProperty("sentAs").GetString());
        Assert.Null(sent.Inviter);
    }

    [Fact] public async Task InvitationPassesCallersTokenForSendingOnTheirBehalf()
    {
        As(Admin);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/invitations") { Content = JsonContent.Create(new { email = "guest@partner.example" }) };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "fixture-user-token");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
        var sent = Assert.Single(factory.Directory.Invites);
        Assert.Equal(new InvitingUser(ApiFactory.Tenant.ToString(), "fixture-user-token"), sent.Inviter);
        Assert.DoesNotContain("fixture-user-token", Data(db => string.Join("|", db.PlatformAudit.Select(a => a.Detail))));
    }

    [Fact] public async Task ExistingDirectoryMemberIsBoundWithoutSendingEmail()
    {
        var subject = Guid.NewGuid().ToString();
        factory.Directory.Users["colleague@apex.example"] = new DirectoryUser(subject, "Member");
        As(Admin);
        var body = await Json(await Invite("colleague@apex.example"));
        Assert.Equal("existing-member", body.GetProperty("delivery").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("invitation").GetProperty("redeemUrl").ValueKind);
        Assert.Empty(factory.Directory.Invites);
        Assert.Equal(subject, Data(db => db.Invitations.AsNoTracking().Single().InvitedSubject));
        // Existing guests are re-invited by email.
        factory.Directory.Users["guest@partner.example"] = new DirectoryUser(Guid.NewGuid().ToString(), "Guest");
        Assert.Equal("email", (await Json(await Invite("guest@partner.example"))).GetProperty("delivery").GetString());
        Assert.Single(factory.Directory.Invites);
    }

    [Fact] public async Task DuplicatePendingInvitationIsUpdatedAndResent()
    {
        As(Admin);
        var first = (await Json(await Invite("guest@partner.example", ApiFactory.Team))).GetProperty("invitation").GetProperty("id").GetGuid();
        Expire(first, TimeSpan.FromDays(1));
        var second = await Json(await Invite("GUEST@partner.example", ApiFactory.OtherTeam));
        Assert.True(second.GetProperty("reused").GetBoolean());
        Assert.Equal(first, second.GetProperty("invitation").GetProperty("id").GetGuid());
        var stored = Data(db => db.Invitations.AsNoTracking().Single());
        Assert.Equal(ApiFactory.OtherTeam, stored.SuggestedTeamId);
        Assert.True(stored.ExpiresAt > DateTimeOffset.UtcNow.AddDays(13));
        Assert.Equal(2, factory.Directory.Invites.Count);
        Assert.All(factory.Directory.Invites, i => Assert.Contains($"invitation={first}", i.Redirect.Query));
    }

    [Fact] public async Task InvitationInputIsValidated()
    {
        As(Admin);
        Assert.Equal(HttpStatusCode.BadRequest, (await Invite("guest@partner.example", ApiFactory.AdminTeam)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Invite("guest@partner.example", Guid.NewGuid())).StatusCode);
        foreach (var invalid in new[] { "", "not-an-email", "a@b", "two@@example.test", "Name <x@example.test>", "x y@example.test", new string('a', 250) + "@example.test" })
            Assert.Equal(HttpStatusCode.BadRequest, (await Invite(invalid)).StatusCode);
        Assert.Empty(factory.Directory.Invites); Assert.Equal(0, factory.Directory.Lookups);
        Assert.Equal(0, Data(db => db.Invitations.Count()));
    }

    [Fact] public async Task InvitationCreationIsRateLimitedPerAdministrator()
    {
        As(Admin);
        for (var i = 0; i < 30; i++) Assert.Equal(HttpStatusCode.OK, (await Invite($"guest{i}@partner.example")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Invite("guest-over@partner.example")).StatusCode);
        var id = Data(db => db.Invitations.First().Id);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsync($"/api/admin/invitations/{id}/resend", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/admin/invitations")).StatusCode);
    }

    [Fact] public async Task ResendExtendsExpiryAndRevokeEndsInvitation()
    {
        As(Admin);
        var id = (await Json(await Invite("guest@partner.example"))).GetProperty("invitation").GetProperty("id").GetGuid();
        Expire(id, TimeSpan.FromDays(-1));
        Assert.Equal("Expired", (await Json(await client.GetAsync("/api/admin/invitations")))[0].GetProperty("status").GetString());
        var resent = await client.PostAsync($"/api/admin/invitations/{id}/resend", null);
        Assert.Equal(HttpStatusCode.OK, resent.StatusCode);
        Assert.Equal("Pending", (await Json(resent)).GetProperty("invitation").GetProperty("status").GetString());
        Assert.Equal(2, factory.Directory.Invites.Count);
        Assert.True(Data(db => db.Invitations.AsNoTracking().Single().ExpiresAt) > DateTimeOffset.UtcNow.AddDays(13));
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/admin/invitations/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/admin/invitations/{id}")).StatusCode);
        Assert.Equal("Revoked", (await Json(await client.GetAsync("/api/admin/invitations")))[0].GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/admin/invitations/{id}/resend", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/admin/invitations/{Guid.NewGuid()}")).StatusCode);
        var subject = Data(db => db.Invitations.AsNoTracking().Single().InvitedSubject);
        As(subject);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/invitations/{id}/accept", new { teamId = ApiFactory.Team })).StatusCode);
        Assert.Equal(0, (await Json(await client.GetAsync("/api/me"))).GetProperty("pendingInvitationCount").GetInt32());
        Assert.True(Data(db => db.PlatformAudit.Any(a => a.Action == "InvitationResent") && db.PlatformAudit.Any(a => a.Action == "InvitationRevoked")));
    }

    [Fact] public async Task ResendForExistingMemberDoesNotEmail()
    {
        factory.Directory.Users["colleague@apex.example"] = new DirectoryUser(Guid.NewGuid().ToString(), "Member");
        As(Admin);
        var id = (await Json(await Invite("colleague@apex.example"))).GetProperty("invitation").GetProperty("id").GetGuid();
        var resent = await Json(await client.PostAsync($"/api/admin/invitations/{id}/resend", null));
        Assert.Equal("existing-member", resent.GetProperty("delivery").GetString());
        Assert.Empty(factory.Directory.Invites);
    }

    private async Task<(Guid Id, string Subject)> InvitedGuest(Guid? suggested = null)
    {
        As(Admin);
        var invitation = (await Json(await Invite($"guest-{Guid.NewGuid():N}@partner.example", suggested))).GetProperty("invitation");
        return (invitation.GetProperty("id").GetGuid(), invitation.GetProperty("invitedSubject").GetString()!);
    }

    [Fact] public async Task InviteeSeesOwnInvitationAndJoinableTeamsOnly()
    {
        As(Guid.NewGuid().ToString());
        var none = await Json(await client.GetAsync("/api/invitations/mine"));
        Assert.Equal(0, none.GetProperty("invitations").GetArrayLength());
        Assert.Equal(0, none.GetProperty("joinableTeams").GetArrayLength());
        var (id, subject) = await InvitedGuest(ApiFactory.OtherTeam);
        As(subject);
        var me = await Json(await client.GetAsync("/api/me"));
        Assert.Equal(1, me.GetProperty("pendingInvitationCount").GetInt32());
        Assert.Equal(0, me.GetProperty("memberships").GetArrayLength());
        var text = await client.GetStringAsync("/api/invitations/mine");
        var mine = JsonDocument.Parse(text).RootElement;
        var invitation = Assert.Single(mine.GetProperty("invitations").EnumerateArray());
        Assert.Equal(id, invitation.GetProperty("id").GetGuid());
        Assert.Equal("Forbidden fixture", invitation.GetProperty("suggestedTeamName").GetString());
        Assert.Equal(new[] { "Authorized fixture", "Forbidden fixture" }, mine.GetProperty("joinableTeams").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToArray());
        Assert.DoesNotContain("partner.example", text); Assert.DoesNotContain(Admin, text); Assert.DoesNotContain("Platform fixture", text);
    }

    [Fact] public async Task AcceptCreatesOneMembershipAndIsSingleUse()
    {
        var (id, subject) = await InvitedGuest(ApiFactory.Team);
        As(Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/invitations/{id}/accept", new { teamId = ApiFactory.Team })).StatusCode);
        As(subject); client.DefaultRequestHeaders.Add("X-Fixture-Tenant", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/invitations/{id}/accept", new { teamId = ApiFactory.Team })).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Fixture-Tenant");
        As(subject, "Guest Person", "guest@partner.example");
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/invitations/{id}/accept", new { teamId = ApiFactory.AdminTeam })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/invitations/{id}/accept", new { teamId = Guid.NewGuid() })).StatusCode);
        var accepted = await client.PostAsJsonAsync($"/api/invitations/{id}/accept", new { teamId = ApiFactory.OtherTeam });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("Member", (await Json(accepted)).GetProperty("role").GetString());
        var membership = Data(db => db.Memberships.AsNoTracking().Single(m => m.Subject == subject));
        Assert.Equal((ApiFactory.OtherTeam, TeamRole.Member, "guest@partner.example", "Guest Person"),
            (membership.TeamId, membership.Role, membership.Email, membership.DisplayName));
        var stored = Data(db => db.Invitations.AsNoTracking().Single(i => i.Id == id));
        Assert.Equal((InvitationStatus.Accepted, (Guid?)ApiFactory.OtherTeam), (stored.Status, stored.AcceptedTeamId));
        Assert.NotNull(stored.AcceptedAt);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/invitations/{id}/accept", new { teamId = ApiFactory.Team })).StatusCode);
        var teams = (await Json(await client.GetAsync("/api/teams"))).EnumerateArray().ToArray();
        Assert.Equal(ApiFactory.OtherTeam, Assert.Single(teams).GetProperty("id").GetGuid());
        Assert.True(Data(db => db.PlatformAudit.Any(a => a.Action == "InvitationAccepted") && db.Audit.Any(a => a.Action == "InvitationAccepted" && a.TeamId == ApiFactory.OtherTeam)));
        As(Admin);
        Assert.Equal("Accepted", (await Json(await client.GetAsync("/api/admin/invitations")))[0].GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/admin/invitations/{id}")).StatusCode);
    }

    [Fact] public async Task AcceptRejectsExistingMembershipAndExpiredInvitations()
    {
        As(Admin);
        var invited = await Json(await Invite("member@partner.example"));
        var id = invited.GetProperty("invitation").GetProperty("id").GetGuid();
        var subject = invited.GetProperty("invitation").GetProperty("invitedSubject").GetString()!;
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/admin/teams/{ApiFactory.Team}/members/{subject}", new { role = "Reviewer" })).StatusCode);
        As(subject);
        var joinable = (await Json(await client.GetAsync("/api/invitations/mine"))).GetProperty("joinableTeams").EnumerateArray()        .Select(t => t.GetProperty("id").GetGuid()).ToArray();
                Assert.Equal(new[] { ApiFactory.OtherTeam }, joinable);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/invitations/{id}/accept", new { teamId = ApiFactory.Team })).StatusCode);
        Expire(id, TimeSpan.FromMinutes(-1));
        var expired = await client.PostAsJsonAsync($"/api/invitations/{id}/accept", new { teamId = ApiFactory.OtherTeam });
        Assert.Equal(HttpStatusCode.Conflict, expired.StatusCode);
        Assert.Contains("expired", await expired.Content.ReadAsStringAsync());
        Assert.Equal(0, (await Json(await client.GetAsync("/api/invitations/mine"))).GetProperty("invitations").GetArrayLength());
        Assert.Equal(1, Data(db => db.Memberships.Count(m => m.Subject == subject)));
    }

    [Fact] public async Task InvitationEndpointsFailClosedWithoutRedirectUrl()
    {
        using var unconfigured = new ApiFactory(invitations: false);
        using var http = unconfigured.CreateClient();
        unconfigured.Initialize();
        As(http, Admin);
        foreach (var response in new[] { await http.PostAsJsonAsync("/api/admin/invitations", new { email = "guest@partner.example" }),
            await http.GetAsync("/api/admin/invitations"), await http.GetAsync("/api/invitations/mine"),
            await http.PostAsJsonAsync($"/api/invitations/{Guid.NewGuid()}/accept", new { teamId = ApiFactory.Team }) })
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("Invitations:RedirectUrl", await response.Content.ReadAsStringAsync());
        }
        Assert.Empty(unconfigured.Directory.Invites);
        As(http, ApiFactory.Member);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/admin/invitations")).StatusCode);
    }

    [Fact] public async Task DirectoryPermissionFailureIsReportedSafely()
    {
        using var failing = new FailingFactory();
        using var http = failing.CreateClient();
        failing.Initialize();
        As(http, Admin);
        var response = await http.PostAsJsonAsync("/api/admin/invitations", new { email = "guest@partner.example" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("User.Invite.All", text); Assert.DoesNotContain("guest@partner.example", text);
        using var scope = failing.Services.CreateScope();
        Assert.Empty(scope.ServiceProvider.GetRequiredService<EvaluationDb>().Invitations);
    }

    public void Dispose() { client.Dispose(); factory.Dispose(); }
}

internal sealed class FailingFactory : IDisposable
{
    private readonly ApiFactory inner = new();
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app;
    public FailingFactory()
    {
        app = inner.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddSingleton<IDirectoryInvitations>(new ForbiddenDirectory());
        }));
    }
    public IServiceProvider Services => app.Services;
    public HttpClient CreateClient() => app.CreateClient();
    public void Initialize()
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EvaluationDb>();
        db.Database.EnsureCreated();
        db.Teams.Add(new Team { Id = ApiFactory.AdminTeam, TenantId = ApiFactory.Tenant.ToString(), Name = "Admin", IsPlatformAdmin = true });
        db.Memberships.Add(new Membership { TeamId = ApiFactory.AdminTeam, Subject = ApiFactory.PlatformAdministrator, Role = TeamRole.Member });
        db.SaveChanges();
    }
    public void Dispose() { app.Dispose(); inner.Dispose(); }
    private sealed class ForbiddenDirectory : IDirectoryInvitations
    {
        public Task<DirectoryUser?> FindUser(string email, CancellationToken ct) => throw new DirectoryUnavailableException(503, InvitationSettings.NotPermitted);
        public Task<DirectoryInvitationResult> Invite(string email, Uri redirectUrl, string message, InvitingUser? inviter, CancellationToken ct) => throw new InvalidOperationException();
        public Task<IReadOnlyList<DirectoryPerson>> SearchPeople(string query, InvitingUser? caller, CancellationToken ct) => throw new InvalidOperationException();
        public Task<DirectoryPerson?> GetPerson(string id, InvitingUser? caller, CancellationToken ct) => throw new InvalidOperationException();
    }
}

public sealed class ConcurrentAcceptTests : IDisposable
{
    private readonly ApiFactory factory = new(fileDatabase: true);
    public ConcurrentAcceptTests() { factory.CreateClient().Dispose(); factory.Initialize(); }
    [Fact] public async Task ConcurrentDoubleAcceptCreatesOneMembership()
    {
        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Fixture-Subject", ApiFactory.PlatformAdministrator);
        var invitation = JsonDocument.Parse(await (await admin.PostAsJsonAsync("/api/admin/invitations", new { email = "race@partner.example" }))
            .Content.ReadAsStringAsync()).RootElement.GetProperty("invitation");
        var id = invitation.GetProperty("id").GetGuid();
        var subject = invitation.GetProperty("invitedSubject").GetString()!;
        var clients = Enumerable.Range(0, 6).Select(_ => factory.CreateClient()).ToArray();
        foreach (var c in clients) c.DefaultRequestHeaders.Add("X-Fixture-Subject", subject);
        var responses = await Task.WhenAll(clients.Select((c, i) =>
            c.PostAsJsonAsync($"/api/invitations/{id}/accept", new { teamId = i % 2 == 0 ? ApiFactory.Team : ApiFactory.OtherTeam })));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        using var scope = factory.Services.CreateScope();
        Assert.Equal(1, scope.ServiceProvider.GetRequiredService<EvaluationDb>().Memberships.Count(m => m.Subject == subject));
        foreach (var c in clients) c.Dispose();
    }
    public void Dispose() => factory.Dispose();
}
