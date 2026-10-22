using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Evaluation.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Evaluation.Tests;

// Self-service teams (open joining, team creation) and the team people picker.
public sealed class SelfServiceTests : IDisposable
{
    private const string UserToken = "fixture-user-token";
    private readonly ApiFactory factory;
    private readonly HttpClient client;
    private static readonly string Tenant = ApiFactory.Tenant.ToString();
    private static string TeamPath => $"/api/teams/{ApiFactory.Team}";
    public SelfServiceTests() : this(new ApiFactory()) { }
    private SelfServiceTests(ApiFactory factory) { this.factory = factory; client = factory.CreateClient(); factory.Initialize(); }

    private void As(string subject, string? name = null, string? email = null) => As(client, subject, name, email);
    private static void As(HttpClient http, string subject, string? name = null, string? email = null)
    {
        foreach (var header in new[] { "X-Fixture-Subject", "X-Fixture-Name", "X-Fixture-Email", "X-Fixture-Tenant" }) http.DefaultRequestHeaders.Remove(header);
        http.DefaultRequestHeaders.Add("X-Fixture-Subject", subject);
        if (name is not null) http.DefaultRequestHeaders.Add("X-Fixture-Name", name);
        if (email is not null) http.DefaultRequestHeaders.Add("X-Fixture-Email", email);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", UserToken);
    }
    private T Data<T>(Func<EvaluationDb, T> read)
    {
        using var scope = factory.Services.CreateScope();
        return read(scope.ServiceProvider.GetRequiredService<EvaluationDb>());
    }
    private void Seed(Action<EvaluationDb> seed) => Data(db => { seed(db); return db.SaveChanges(); });
    private static async Task<JsonElement> Json(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    private Task<HttpResponseMessage> AddPeople(object[] people, string path = "") =>
        client.PostAsJsonAsync($"{(path.Length > 0 ? path : TeamPath)}/invitations", new { people });
    private static JsonElement[] Items(JsonElement body) => body.GetProperty("results").EnumerateArray().ToArray();
    private static Invitation Pending(string subject, TimeSpan expiresIn, string status = InvitationStatus.Pending, Guid? suggested = null) => new()
    {
        Id = Guid.NewGuid(), TenantId = Tenant, Email = $"{Guid.NewGuid():N}@partner.example", InvitedSubject = subject, InvitedBy = ApiFactory.PlatformAdministrator,
        Status = status, SuggestedTeamId = suggested, ExpiresAt = DateTimeOffset.UtcNow + expiresIn
    };

    [Fact] public async Task AnyTenantUserJoinsNonAdminTeamsAsMemberAndPendingInvitationsAreAccepted()
    {
        var newcomer = Guid.NewGuid().ToString();
        var open = Pending(newcomer, TimeSpan.FromDays(3), suggested: ApiFactory.Team);
        var expired = Pending(newcomer, TimeSpan.FromDays(-1));
        var revoked = Pending(newcomer, TimeSpan.FromDays(3), InvitationStatus.Revoked);
        var someoneElse = Pending(Guid.NewGuid().ToString(), TimeSpan.FromDays(3));
        Seed(db => db.Invitations.AddRange(open, expired, revoked, someoneElse));
        As(newcomer, "New Person", "New@Apex.Example");
        Assert.Equal(1, (await Json(await client.GetAsync("/api/me"))).GetProperty("pendingInvitationCount").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/teams/{ApiFactory.AdminTeam}/join", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/teams/{Guid.NewGuid()}/join", null)).StatusCode);
        var joined = await client.PostAsync($"/api/teams/{ApiFactory.OtherTeam}/join", null);
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        var body = await Json(joined);
        Assert.Equal(("Member", "Forbidden fixture"), (body.GetProperty("role").GetString(), body.GetProperty("teamName").GetString()));
        var membership = Data(db => db.Memberships.AsNoTracking().Single(m => m.Subject == newcomer));
        Assert.Equal((ApiFactory.OtherTeam, TeamRole.Member, "new@apex.example", "New Person"),
            (membership.TeamId, membership.Role, membership.Email, membership.DisplayName));
        var invitations = Data(db => db.Invitations.AsNoTracking().ToDictionary(i => i.Id));
        Assert.Equal((InvitationStatus.Accepted, (Guid?)ApiFactory.OtherTeam), (invitations[open.Id].Status, invitations[open.Id].AcceptedTeamId));
        Assert.NotNull(invitations[open.Id].AcceptedAt);
        Assert.Equal(InvitationStatus.Pending, invitations[expired.Id].Status);
        Assert.Equal(InvitationStatus.Revoked, invitations[revoked.Id].Status);
        Assert.Equal(InvitationStatus.Pending, invitations[someoneElse.Id].Status);
        var me = await Json(await client.GetAsync("/api/me"));
        Assert.Equal(0, me.GetProperty("pendingInvitationCount").GetInt32());
        Assert.False(me.GetProperty("isPlatformAdmin").GetBoolean());
        // Full member access to the joined team; still nothing else.
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"/api/teams/{ApiFactory.OtherTeam}/projects", new { name = "Mine" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{TeamPath}/projects")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/teams/{ApiFactory.OtherTeam}/join", null)).StatusCode);
        Assert.True(Data(db => db.Audit.Any(a => a.TeamId == ApiFactory.OtherTeam && a.Action == "MemberJoined" && a.Actor == newcomer)));
        Assert.False(Data(db => db.PlatformAudit.Any(a => a.Action == "PlatformAdminJoined")));
        Assert.True(Data(db => db.PlatformAudit.Any(a => a.Action == "InvitationAccepted" && a.Resource == open.Id.ToString())));
        // Same object ID in another tenant can't see or join this tenant's teams.
        As(Guid.NewGuid().ToString()); client.DefaultRequestHeaders.Add("X-Fixture-Tenant", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"{TeamPath}/join", null)).StatusCode);
    }

    [Fact] public async Task MembersLeaveNonAdminTeamsButTheLastAdministratorStays()
    {
        As(ApiFactory.Member);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"{TeamPath}/leave", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"{TeamPath}/leave", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{TeamPath}/projects")).StatusCode);
        As(ApiFactory.Reviewer);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"{TeamPath}/leave", null)).StatusCode);
        As(ApiFactory.Administrator);
        var last = await client.PostAsync($"{TeamPath}/leave", null);
        Assert.Equal(HttpStatusCode.Conflict, last.StatusCode);
        Assert.Contains("At least one administrator", await last.Content.ReadAsStringAsync());
        Assert.Equal(2, Data(db => db.Audit.Count(a => a.Action == "MembershipRemoved" && a.TeamId == ApiFactory.Team)));
        Assert.False(Data(db => db.PlatformAudit.Any(a => a.Action == "PlatformAdminLeft" || a.Action == "AdminMembershipRemoved")));
    }

    [Fact] public async Task AnyUserCreatesATeamAndBecomesItsAdministratorOnly()
    {
        var creator = Guid.NewGuid().ToString();
        As(creator, "Creator", "creator@apex.example");
        var response = await client.PostAsJsonAsync("/api/teams", new { name = "  Self Team  " });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await Json(response);
        var id = body.GetProperty("id").GetGuid();
        Assert.Equal(("Self Team", "Administrator"), (body.GetProperty("name").GetString(), body.GetProperty("role").GetString()));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/teams", new { name = "SELF team" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/teams", new { name = "platform fixture - administrator" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/teams", new { name = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/teams", new { name = new string('x', 201) })).StatusCode);
        var me = await Json(await client.GetAsync("/api/me"));
        Assert.False(me.GetProperty("isPlatformAdmin").GetBoolean());
        var membership = Assert.Single(me.GetProperty("memberships").EnumerateArray());
        Assert.Equal((id, "Administrator"), (membership.GetProperty("teamId").GetGuid(), membership.GetProperty("role").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/admin/teams")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{TeamPath}/projects")).StatusCode);
        Assert.False(Data(db => db.Teams.Single(t => t.Id == id).IsPlatformAdmin));
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/teams/{id}/policy", new { expectedVersion = 0, dt = 70, brd = 70, prd = 70 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/teams/{id}/members")).StatusCode);
        Assert.True((await Json(await client.GetAsync("/api/teams"))).EnumerateArray().Single().GetProperty("canManage").GetBoolean());
        Assert.True(Data(db => db.PlatformAudit.Any(a => a.Action == "TeamCreated" && a.Actor == creator && a.Detail.Contains("via=self-service"))));
        // Platform administrators keep their own console for creating teams.
        As(ApiFactory.PlatformAdministrator);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/admin/teams", new { name = "Admin made" })).StatusCode);
    }

    [Fact] public async Task TeamCreationIsRateLimitedPerUser()
    {
        As(Guid.NewGuid().ToString());
        for (var i = 0; i < 10; i++) Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/teams", new { name = $"Team {i}" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/teams", new { name = "Team over" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/teams")).StatusCode);
        As(Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/teams", new { name = "Someone else's team" })).StatusCode);
    }

    [Fact] public async Task JoinableTeamsListNamesAndCountsOfOtherNonAdminTenantTeams()
    {
        Seed(db => db.Teams.Add(new Team { Id = Guid.NewGuid(), TenantId = Guid.NewGuid().ToString(), Name = "Another tenant's team" }));
        As(Guid.NewGuid().ToString());
        var text = await client.GetStringAsync("/api/teams/joinable");
        var teams = JsonDocument.Parse(text).RootElement.EnumerateArray().ToArray();
        Assert.Equal(new[] { "Authorized fixture", "Forbidden fixture" }, teams.Select(t => t.GetProperty("name").GetString()).ToArray());
        Assert.Equal(new[] { 3, 0 }, teams.Select(t => t.GetProperty("members").GetInt32()).ToArray());
        Assert.All(teams, t => Assert.Equal(new[] { "id", "name", "members" }, t.EnumerateObject().Select(p => p.Name).ToArray()));
        Assert.DoesNotContain("Platform fixture", text); Assert.DoesNotContain("Another tenant", text);
        As(ApiFactory.Member);
        var mine = (await Json(await client.GetAsync("/api/teams/joinable"))).EnumerateArray().ToArray();
        Assert.Equal(ApiFactory.OtherTeam, Assert.Single(mine).GetProperty("id").GetGuid());
    }

    [Fact] public async Task PeopleSearchRequiresManageRightsAndValidatesText()
    {
        foreach (var subject in new[] { ApiFactory.Member, ApiFactory.Reviewer, Guid.NewGuid().ToString(), ApiFactory.PlatformAdministrator })
        {
            As(subject);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{TeamPath}/people?q=ada")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await AddPeople([new { email = "x@partner.example" }])).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{TeamPath}/invitations")).StatusCode);
        }
        As(ApiFactory.Administrator);
        foreach (var q in new[] { "", "a", "%20a%20", new string('a', 101) })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{TeamPath}/people?q={q}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{TeamPath}/people")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{TeamPath}/people?q=%20ab%20")).StatusCode);
        Assert.Empty(factory.Directory.Invites);
        // The platform administration team's members are managed from Administration only.
        Data(db => db.Memberships.Where(m => m.TeamId == ApiFactory.AdminTeam).ExecuteUpdate(s => s.SetProperty(m => m.Role, TeamRole.Administrator)));
        As(ApiFactory.PlatformAdministrator);
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync($"/api/teams/{ApiFactory.AdminTeam}/people?q=ada")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await AddPeople([new { email = "x@partner.example" }], $"/api/teams/{ApiFactory.AdminTeam}")).StatusCode);
    }

    private (string Ada, string Guest, string Foreign) SeedPeople()
    {
        var ada = Guid.NewGuid().ToString(); var guest = Guid.NewGuid().ToString(); var foreign = Guid.NewGuid().ToString();
        var foreignTeam = Guid.NewGuid();
        Seed(db =>
        {
            db.Memberships.Add(new Membership { TeamId = ApiFactory.OtherTeam, Subject = ada, Role = TeamRole.Member, Email = "ada@apex.example", DisplayName = "Ada Lovelace" });
            db.Memberships.Single(m => m.TeamId == ApiFactory.Team && m.Subject == ApiFactory.Member).DisplayName = "Adam Member";
            db.Memberships.Single(m => m.TeamId == ApiFactory.Team && m.Subject == ApiFactory.Member).Email = "adam@apex.example";
            db.Teams.Add(new Team { Id = foreignTeam, TenantId = Guid.NewGuid().ToString(), Name = "Foreign" });
            db.Memberships.Add(new Membership { TeamId = foreignTeam, Subject = foreign, Role = TeamRole.Member, Email = "ada.other@else.example", DisplayName = "Ada Other" });
        });
        factory.Directory.People[ada] = new DirectoryPerson(ada, "Ada Lovelace", "ada@apex.example", "ada@apex.example");
        factory.Directory.People[guest] = new DirectoryPerson(guest, "Ada Guest", null, "ada_partner.example#EXT#@apex.example");
        return (ada, guest, foreign);
    }

    [Fact] public async Task PeopleSearchMergesPlatformAndDirectoryPeople()
    {
        var (ada, guest, foreign) = SeedPeople();
        As(ApiFactory.Administrator);
        var text = await client.GetStringAsync($"{TeamPath}/people?q=ADA");
        var body = JsonDocument.Parse(text).RootElement;
        Assert.True(body.GetProperty("directoryAvailable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("directoryNote").ValueKind);
        var results = Items(body);
        Assert.Equal(new[] { ada, ApiFactory.Member, guest }, results.Select(r => r.GetProperty("subject").GetString()).ToArray());
        Assert.Equal(new[] { "platform", "platform", "directory" }, results.Select(r => r.GetProperty("source").GetString()).ToArray());
        Assert.Equal(new[] { false, true, false }, results.Select(r => r.GetProperty("member").GetBoolean()).ToArray());
        Assert.Equal(new[] { false, false, true }, results.Select(r => r.GetProperty("guest").GetBoolean()).ToArray());
        Assert.Equal("Ada Lovelace", results[0].GetProperty("displayName").GetString());
        Assert.Equal("ada@apex.example", results[0].GetProperty("email").GetString());
        Assert.Equal(JsonValueKind.Null, results[2].GetProperty("email").ValueKind);
        Assert.DoesNotContain(foreign, text); Assert.DoesNotContain("else.example", text);
        var (query, caller) = Assert.Single(factory.Directory.Searches);
        Assert.Equal(("ADA", new InvitingUser(Tenant, UserToken)), (query, caller));
        // Platform people match by email too.
        var byEmail = Items(await Json(await client.GetAsync($"{TeamPath}/people?q=adam@apex")));
        Assert.Equal(ApiFactory.Member, Assert.Single(byEmail).GetProperty("subject").GetString());
    }

    [Fact] public async Task PeopleSearchStillReturnsPlatformPeopleWhenTheDirectoryIsUnavailable()
    {
        var (ada, _, _) = SeedPeople();
        factory.Directory.DirectoryAvailable = false;
        As(ApiFactory.Administrator);
        var body = await Json(await client.GetAsync($"{TeamPath}/people?q=ada"));
        Assert.False(body.GetProperty("directoryAvailable").GetBoolean());
        Assert.Equal(DirectorySearch.Unavailable, body.GetProperty("directoryNote").GetString());
        Assert.Equal(new[] { ada, ApiFactory.Member }, Items(body).Select(r => r.GetProperty("subject").GetString()).ToArray());
        Assert.All(Items(body), r => Assert.Equal("platform", r.GetProperty("source").GetString()));
    }

    [Fact] public async Task AddingBySubjectUsesPlatformOrDirectoryPeopleWithoutEmail()
    {
        var (ada, guest, foreign) = SeedPeople();
        As(ApiFactory.Administrator);
        var unknown = Guid.NewGuid().ToString();
        var response = await AddPeople([new { subject = ada }, new { subject = guest.ToUpperInvariant() }, new { subject = ApiFactory.Member },
            new { subject = unknown }, new { subject = foreign }]);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = Items(await Json(response));
        Assert.Equal(new[] { "added", "added", "already-member", "not-found", "not-found" }, items.Select(i => i.GetProperty("status").GetString()).ToArray());
        Assert.Equal(new[] { ada, guest, ApiFactory.Member, unknown, foreign }, items.Select(i => i.GetProperty("subject").GetString()).ToArray());
        Assert.Equal("Ada Guest", items[1].GetProperty("displayName").GetString());
        var added = Data(db => db.Memberships.AsNoTracking().Where(m => m.TeamId == ApiFactory.Team && (m.Subject == ada || m.Subject == guest)).ToList());
        Assert.All(added, m => Assert.Equal(TeamRole.Member, m.Role));
        Assert.Equal(("ada@apex.example", "Ada Lovelace"), added.Where(m => m.Subject == ada).Select(m => (m.Email, m.DisplayName)).Single());
        Assert.Equal(((string?)null, "Ada Guest"), added.Where(m => m.Subject == guest).Select(m => (m.Email, m.DisplayName)).Single());
        Assert.Empty(factory.Directory.Invites);
        // Platform people are verified locally; only unknown subjects are looked up, with the caller's token.
        Assert.Equal(new[] { guest, unknown, foreign }, factory.Directory.PersonLookups.Select(l => l.Id).ToArray());
        Assert.All(factory.Directory.PersonLookups, l => Assert.Equal(new InvitingUser(Tenant, UserToken), l.Caller));
        Assert.Equal(2, Data(db => db.Audit.Count(a => a.TeamId == ApiFactory.Team && a.Action == "MemberAdded")));
        Assert.Equal(2, Data(db => db.PlatformAudit.Count(a => a.TenantId == Tenant && a.Action == "TeamMemberAdded")));
        // Directory lookups that aren't available for the caller are per-entry errors.
        factory.Directory.DirectoryAvailable = false;
        var error = Assert.Single(Items(await Json(await AddPeople([new { subject = Guid.NewGuid().ToString() }]))));
        Assert.Equal(("error", DirectorySearch.LookupUnavailable), (error.GetProperty("status").GetString(), error.GetProperty("error").GetString()));
    }

    [Fact] public async Task AddingByEmailAddsDirectoryUsersAndInvitesNewExternalPeople()
    {
        SeedPeople();
        var colleague = Guid.NewGuid().ToString();
        factory.Directory.Users["colleague@apex.example"] = new DirectoryUser(colleague, "Member");
        As(ApiFactory.Administrator);
        var response = await AddPeople([new { email = " Colleague@Apex.Example " }, new { email = "guest@partner.example" }, new { email = "adam@apex.example" }]);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = Items(await Json(response));
        Assert.Equal(new[] { "added", "invited", "already-member" }, items.Select(i => i.GetProperty("status").GetString()).ToArray());
        Assert.Equal(colleague, items[0].GetProperty("subject").GetString());
        Assert.Equal("colleague@apex.example", Data(db => db.Memberships.AsNoTracking().Single(m => m.TeamId == ApiFactory.Team && m.Subject == colleague).Email));
        Assert.Equal("guest@partner.example", items[1].GetProperty("email").GetString());
        Assert.Equal("app", items[1].GetProperty("sentAs").GetString());
        Assert.StartsWith("https://login.microsoftonline.com/redeem", items[1].GetProperty("redeemUrl").GetString());
        var sent = Assert.Single(factory.Directory.Invites);
        var stored = Data(db => db.Invitations.AsNoTracking().Single());
        Assert.Equal(("guest@partner.example", (Guid?)ApiFactory.Team, ApiFactory.Administrator, InvitationDelivery.Email, factory.Directory.Guests["guest@partner.example"]),
            (stored.Email, stored.SuggestedTeamId, stored.InvitedBy, stored.Delivery, stored.InvitedSubject));
        Assert.Equal(new InvitingUser(Tenant, UserToken), sent.Inviter);
        Assert.Equal($"{ApiFactory.RedirectUrl}?invitation={stored.Id}#evaluation", sent.Redirect.AbsoluteUri);
        Assert.Contains("Authorized fixture", sent.Message);
        Assert.False(Data(db => db.Memberships.Any(m => m.Subject == stored.InvitedSubject)));
        Assert.True(Data(db => db.Audit.Any(a => a.TeamId == ApiFactory.Team && a.Action == "TeamInvitationCreated")));
        Assert.True(Data(db => db.PlatformAudit.Any(a => a.Action == "InvitationCreated" && a.Resource == stored.Id.ToString())));
        Assert.DoesNotContain(UserToken, Data(db => string.Join("|", db.PlatformAudit.Select(a => a.Detail))));
        // Inviting the same address again refreshes the pending invitation instead of duplicating it.
        Assert.Equal("invited", Items(await Json(await AddPeople([new { email = "GUEST@partner.example" }])))[0].GetProperty("status").GetString());
        Assert.Equal(1, Data(db => db.Invitations.Count()));
        // The invitee accepts into any team, as before.
        As(stored.InvitedSubject);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/invitations/{stored.Id}/accept", new { teamId = ApiFactory.OtherTeam })).StatusCode);
    }

    [Fact] public async Task OneFailingEntryDoesNotStopTheOthers()
    {
        var (_, guest, _) = SeedPeople();
        factory.Directory.FailingInvites["bad@partner.example"] = true;
        As(ApiFactory.Administrator);
        var items = Items(await Json(await AddPeople([new { email = "bad@partner.example" }, new { email = "good@partner.example" }, new { subject = guest }])));
        Assert.Equal(new[] { "error", "invited", "added" }, items.Select(i => i.GetProperty("status").GetString()).ToArray());
        Assert.Contains("Microsoft Graph rejected", items[0].GetProperty("error").GetString());
        Assert.DoesNotContain("bad@partner.example", items[0].GetProperty("error").GetString());
        Assert.Equal("good@partner.example", Data(db => db.Invitations.AsNoTracking().Single().Email));
        Assert.True(Data(db => db.Memberships.Any(m => m.TeamId == ApiFactory.Team && m.Subject == guest)));
    }

    [Fact] public async Task AddPeopleRequestsAreValidatedBeforeAnything()
    {
        As(ApiFactory.Administrator);
        var tooMany = Enumerable.Range(0, 21).Select(i => (object)new { email = $"p{i}@partner.example" }).ToArray();
        foreach (var people in new[] { Array.Empty<object>(), tooMany, [new { subject = Guid.NewGuid().ToString(), email = "x@partner.example" }],
            [new { subject = (string?)null, email = (string?)null }], [new { subject = "not-a-guid" }], [new { email = "not-an-email" }],
            [new { email = "ok@partner.example" }, new { email = "two@@partner.example" }] })
            Assert.Equal(HttpStatusCode.BadRequest, (await AddPeople(people)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{TeamPath}/invitations", new { })).StatusCode);
        Assert.Empty(factory.Directory.Invites); Assert.Equal(0, factory.Directory.Lookups); Assert.Empty(factory.Directory.PersonLookups);
        Assert.Equal(0, Data(db => db.Invitations.Count()));
        Assert.Equal(3, Data(db => db.Memberships.Count(m => m.TeamId == ApiFactory.Team)));
        var twenty = Enumerable.Range(0, 20).Select(i => (object)new { email = $"p{i}@partner.example" }).ToArray();
        Assert.Equal(20, Items(await Json(await AddPeople(twenty))).Count(i => i.GetProperty("status").GetString() == "invited"));
    }

    [Fact] public async Task MissingRedirectUrlOnlyBlocksRequestsThatNeedAnEmailInvitation()
    {
        using var unconfigured = new ApiFactory(invitations: false);
        using var http = unconfigured.CreateClient();
        unconfigured.Initialize();
        var person = Guid.NewGuid().ToString();
        unconfigured.Directory.People[person] = new DirectoryPerson(person, "Directory Person", "person@apex.example", "person@apex.example");
        unconfigured.Directory.Users["colleague@apex.example"] = new DirectoryUser(Guid.NewGuid().ToString(), "Member");
        As(http, ApiFactory.Administrator);
        var path = $"/api/teams/{ApiFactory.Team}/invitations";
        var blocked = await http.PostAsJsonAsync(path, new { people = new object[] { new { subject = person }, new { email = "new@partner.example" } } });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
        Assert.Contains("Invitations:RedirectUrl", await blocked.Content.ReadAsStringAsync());
        using (var scope = unconfigured.Services.CreateScope())
            Assert.False(scope.ServiceProvider.GetRequiredService<EvaluationDb>().Memberships.Any(m => m.Subject == person));
        Assert.Empty(unconfigured.Directory.Invites);
        var ok = await http.PostAsJsonAsync(path, new { people = new object[] { new { subject = person }, new { email = "colleague@apex.example" } } });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.All(Items(await Json(ok)), i => Assert.Equal("added", i.GetProperty("status").GetString()));
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"/api/teams/{ApiFactory.Team}/invitations")).StatusCode);
    }

    [Fact] public async Task TeamInvitationListAndRevokeAreScopedToTheTeam()
    {
        As(ApiFactory.Administrator);
        await AddPeople([new { email = "mine@partner.example" }]);
        var mine = Data(db => db.Invitations.AsNoTracking().Single()).Id;
        var other = Pending(Guid.NewGuid().ToString(), TimeSpan.FromDays(3), suggested: ApiFactory.OtherTeam);
        var accepted = Pending(Guid.NewGuid().ToString(), TimeSpan.FromDays(3), InvitationStatus.Accepted, ApiFactory.Team);
        var expired = Pending(Guid.NewGuid().ToString(), TimeSpan.FromDays(-1), suggested: ApiFactory.Team);
        Seed(db => db.Invitations.AddRange(other, accepted, expired));
        var list = (await Json(await client.GetAsync($"{TeamPath}/invitations"))).EnumerateArray().ToArray();
        Assert.Equal(new[] { mine, expired.Id }.Order(), list.Select(i => i.GetProperty("id").GetGuid()).Order());
        var pending = list.Single(i => i.GetProperty("id").GetGuid() == mine);
        Assert.Equal(("mine@partner.example", "Pending"), (pending.GetProperty("email").GetString(), pending.GetProperty("status").GetString()));
        Assert.StartsWith("https://login.microsoftonline.com/redeem", pending.GetProperty("redeemUrl").GetString());
        var old = list.Single(i => i.GetProperty("id").GetGuid() == expired.Id);
        Assert.Equal(("Expired", JsonValueKind.Null), (old.GetProperty("status").GetString(), old.GetProperty("redeemUrl").ValueKind));
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{TeamPath}/invitations/{other.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"{TeamPath}/invitations/{accepted.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{TeamPath}/invitations/{mine}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{TeamPath}/invitations/{mine}")).StatusCode);
        Assert.Equal(InvitationStatus.Revoked, Data(db => db.Invitations.AsNoTracking().Single(i => i.Id == mine).Status));
        Assert.Equal(InvitationStatus.Pending, Data(db => db.Invitations.AsNoTracking().Single(i => i.Id == other.Id).Status));
        Assert.Equal(expired.Id, Assert.Single((await Json(await client.GetAsync($"{TeamPath}/invitations"))).EnumerateArray()).GetProperty("id").GetGuid());
        Assert.True(Data(db => db.Audit.Any(a => a.Action == "TeamInvitationRevoked") && db.PlatformAudit.Any(a => a.Action == "InvitationRevoked")));
        As(ApiFactory.Member);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{TeamPath}/invitations/{expired.Id}")).StatusCode);
    }

    [Fact] public async Task PlatformAdministratorsWhoJoinedATeamCanInviteIntoIt()
    {
        var (ada, _, _) = SeedPeople();
        As(ApiFactory.PlatformAdministrator);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/teams/{ApiFactory.OtherTeam}/people?q=ada")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/teams/{ApiFactory.OtherTeam}/join", null)).StatusCode);
        var results = Items(await Json(await client.GetAsync($"/api/teams/{ApiFactory.OtherTeam}/people?q=ada")));
        Assert.True(results.Single(r => r.GetProperty("subject").GetString() == ada).GetProperty("member").GetBoolean());
        Assert.Equal("added", Items(await Json(await AddPeople([new { subject = ApiFactory.Member }], $"/api/teams/{ApiFactory.OtherTeam}")))[0]
            .GetProperty("status").GetString());
    }

    [Fact] public async Task PlatformAdministratorsChangeOrClearSuggestedTeamOfPendingInvitations()
    {
        var pending = Pending(Guid.NewGuid().ToString(), TimeSpan.FromDays(3), suggested: ApiFactory.Team);
        var accepted = Pending(Guid.NewGuid().ToString(), TimeSpan.FromDays(3), InvitationStatus.Accepted, ApiFactory.Team);
        Data(db => { db.Invitations.AddRange(pending, accepted); return db.SaveChanges(); });
        As(ApiFactory.PlatformAdministrator);
        var moved = await client.PatchAsJsonAsync($"/api/admin/invitations/{pending.Id}", new { suggestedTeamId = ApiFactory.OtherTeam });
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var body = await Json(moved);
        Assert.Equal(ApiFactory.OtherTeam, body.GetProperty("suggestedTeamId").GetGuid());
        Assert.Equal("Forbidden fixture", body.GetProperty("suggestedTeamName").GetString());
        Assert.Equal(ApiFactory.OtherTeam, Data(db => db.Invitations.AsNoTracking().Single(i => i.Id == pending.Id).SuggestedTeamId));
        // The invitee's Join screen now preselects the new team.
        As(pending.InvitedSubject);
        var mine = (await Json(await client.GetAsync("/api/invitations/mine"))).GetProperty("invitations").EnumerateArray().Single();
        Assert.Equal("Forbidden fixture", mine.GetProperty("suggestedTeamName").GetString());
        As(ApiFactory.PlatformAdministrator);
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync($"/api/admin/invitations/{pending.Id}", new { suggestedTeamId = (Guid?)null })).StatusCode);
        Assert.Null(Data(db => db.Invitations.AsNoTracking().Single(i => i.Id == pending.Id).SuggestedTeamId));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PatchAsJsonAsync($"/api/admin/invitations/{pending.Id}", new { suggestedTeamId = ApiFactory.AdminTeam })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PatchAsJsonAsync($"/api/admin/invitations/{pending.Id}", new { suggestedTeamId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PatchAsJsonAsync($"/api/admin/invitations/{accepted.Id}", new { suggestedTeamId = ApiFactory.OtherTeam })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PatchAsJsonAsync($"/api/admin/invitations/{Guid.NewGuid()}", new { suggestedTeamId = ApiFactory.OtherTeam })).StatusCode);
        Assert.Equal(2, Data(db => db.PlatformAudit.Count(a => a.Action == "InvitationSuggestionChanged" && a.Resource == pending.Id.ToString())));
        As(ApiFactory.Administrator);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PatchAsJsonAsync($"/api/admin/invitations/{pending.Id}", new { suggestedTeamId = ApiFactory.Team })).StatusCode);
    }

    [Fact] public async Task TeamManagersMoveTheirInvitationsOnlyToTeamsTheyManage()
    {
        var mine = Pending(Guid.NewGuid().ToString(), TimeSpan.FromDays(3), suggested: ApiFactory.Team);
        var others = Pending(Guid.NewGuid().ToString(), TimeSpan.FromDays(3), suggested: ApiFactory.OtherTeam);
        Data(db => { db.Invitations.AddRange(mine, others); return db.SaveChanges(); });
        As(ApiFactory.Administrator);
        var path = $"/api/teams/{ApiFactory.Team}/invitations";
        // Not a manager of the target team → refused; another team's invitation is hidden.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PatchAsJsonAsync($"{path}/{mine.Id}", new { suggestedTeamId = ApiFactory.OtherTeam })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PatchAsJsonAsync($"{path}/{others.Id}", new { suggestedTeamId = ApiFactory.Team })).StatusCode);
        // After becoming Administrator of the other team, moving works and the invitation leaves this team's list.
        Data(db => { db.Memberships.Add(new Membership { TeamId = ApiFactory.OtherTeam, Subject = ApiFactory.Administrator, Role = TeamRole.Administrator }); return db.SaveChanges(); });
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync($"{path}/{mine.Id}", new { suggestedTeamId = ApiFactory.OtherTeam })).StatusCode);
        Assert.DoesNotContain((await Json(await client.GetAsync(path))).EnumerateArray(), i => i.GetProperty("id").GetGuid() == mine.Id);
        Assert.Contains((await Json(await client.GetAsync($"/api/teams/{ApiFactory.OtherTeam}/invitations"))).EnumerateArray(), i => i.GetProperty("id").GetGuid() == mine.Id);
        // Clearing the suggestion is allowed from the owning team.
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync($"/api/teams/{ApiFactory.OtherTeam}/invitations/{mine.Id}", new { suggestedTeamId = (Guid?)null })).StatusCode);
        Assert.Null(Data(db => db.Invitations.AsNoTracking().Single(i => i.Id == mine.Id).SuggestedTeamId));
        // Members can't change suggestions.
        As(ApiFactory.Member);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PatchAsJsonAsync($"{path}/{others.Id}", new { suggestedTeamId = ApiFactory.Team })).StatusCode);
    }

    public void Dispose() { client.Dispose(); factory.Dispose(); }
}
