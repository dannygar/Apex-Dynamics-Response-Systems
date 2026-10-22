using System.Net;
using System.Text;
using Azure.Core;
using Azure.Identity;
using Evaluation.Core;
using Xunit;

namespace Evaluation.Tests;

// Exercises the Graph adapter against an in-process handler; no request leaves the test process.
public sealed class GraphDirectoryTests
{
    private sealed class StaticToken : TokenCredential
    {
        public int Calls;
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken ct) => Next(context);
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken ct) => new(Next(context));
        private AccessToken Next(TokenRequestContext context)
        {
            Assert.Equal(new[] { "https://graph.microsoft.com/.default" }, context.Scopes);
            Interlocked.Increment(ref Calls);
            return new AccessToken("fixture-token", DateTimeOffset.UtcNow.AddHours(1));
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly List<(HttpRequestMessage Request, string? Body)> Requests = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request, body));
            return respond(request, body);
        }
    }
    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static (GraphDirectoryInvitations Directory, Handler Handler) Create(Func<HttpRequestMessage, string?, HttpResponseMessage> respond)
    {
        var handler = new Handler(respond);
        return (new GraphDirectoryInvitations(new HttpClient(handler), new StaticToken()), handler);
    }

    [Fact] public async Task FindUserUsesAdvancedQueryWithEscapedEmail()
    {
        var id = Guid.NewGuid();
        var (directory, handler) = Create((_, _) => Json(HttpStatusCode.OK, $"{{\"value\":[{{\"id\":\"{id}\",\"userType\":\"Member\",\"userPrincipalName\":\"o'brien@apex.example\"}}]}}"));
        var user = await directory.FindUser("o'brien@apex.example", default);
        Assert.Equal(new DirectoryUser(id.ToString(), "Member"), user);
        var (request, _) = Assert.Single(handler.Requests);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("eventual", Assert.Single(request.Headers.GetValues("ConsistencyLevel")));
        var query = Uri.UnescapeDataString(request.RequestUri!.Query);
        Assert.StartsWith("https://graph.microsoft.com/v1.0/users?", request.RequestUri.AbsoluteUri);
        Assert.Contains("mail eq 'o''brien@apex.example' or userPrincipalName eq 'o''brien@apex.example' or otherMails/any(x:x eq 'o''brien@apex.example')", query);
        Assert.Contains("$count=true", query);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task FindUserFallsBackToBasicProfileAndDetectsGuests(HttpStatusCode advancedFailure)
    {
        var id = Guid.NewGuid();
        var (directory, handler) = Create((request, _) => request.Headers.Contains("ConsistencyLevel") ? Json(advancedFailure, "{}") :
            Json(HttpStatusCode.OK, $"{{\"value\":[{{\"id\":\"{id}\",\"userPrincipalName\":\"guest_partner.example#EXT#@apex.example\"}}]}}"));
        Assert.Equal(new DirectoryUser(id.ToString(), "Guest"), await directory.FindUser("guest@partner.example", default));
        Assert.Equal(2, handler.Requests.Count);
        var basic = Uri.UnescapeDataString(handler.Requests[1].Request.RequestUri!.Query);
        Assert.DoesNotContain("otherMails", basic); Assert.DoesNotContain("userType", basic);
    }

    [Fact] public async Task FindUserReturnsNullWhenNobodyMatches()
    {
        var (directory, _) = Create((_, _) => Json(HttpStatusCode.OK, "{\"value\":[]}"));
        Assert.Null(await directory.FindUser("nobody@partner.example", default));
    }

    [Fact] public async Task InviteSendsMicrosoftEmailAndReturnsInvitedUser()
    {
        var id = Guid.NewGuid();
        var (directory, handler) = Create((_, _) => Json(HttpStatusCode.Created,
            $"{{\"inviteRedeemUrl\":\"https://login.microsoftonline.com/redeem?x=1\",\"invitedUser\":{{\"id\":\"{id}\"}}}}"));
        var redirect = InvitationSettings.ForInvitation(new Uri("https://evaluation.example.test/"), id);
        var result = await directory.Invite("guest@partner.example", redirect, InvitationSettings.Message("Response"), null, default);
        Assert.Equal(new DirectoryInvitationResult(id.ToString(), "https://login.microsoftonline.com/redeem?x=1"), result);
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://graph.microsoft.com/v1.0/invitations", request.RequestUri!.AbsoluteUri);
        Assert.Contains("\"invitedUserEmailAddress\":\"guest@partner.example\"", body);
        Assert.Contains($"\"inviteRedirectUrl\":\"https://evaluation.example.test/?invitation={id}#evaluation\"", body);
        Assert.Contains("\"sendInvitationMessage\":true", body);
        Assert.Contains("Apex Evaluation workspace", body); Assert.Contains("Suggested team: Response.", body);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 503)]
    [InlineData(HttpStatusCode.Forbidden, 503)]
    [InlineData(HttpStatusCode.InternalServerError, 502)]
    [InlineData(HttpStatusCode.BadRequest, 502)]
    public async Task GraphFailuresBecomeSafeErrors(HttpStatusCode status, int expected)
    {
        var (directory, _) = Create((_, _) => Json(status, "{\"error\":{\"message\":\"secret detail guest@partner.example\"}}"));
        var error = await Assert.ThrowsAsync<DirectoryUnavailableException>(() =>
            directory.Invite("guest@partner.example", new Uri("https://evaluation.example.test/"), "m", null, default));
        Assert.Equal(expected, error.StatusCode);
        Assert.DoesNotContain("guest@partner.example", error.Message); Assert.DoesNotContain("secret", error.Message);
        if (expected == 503) Assert.Equal(InvitationSettings.NotPermitted, error.Message);
    }

    private sealed class FailingToken : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken ct) => throw new AuthenticationFailedException("AADSTS fixture detail");
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken ct) => throw new AuthenticationFailedException("AADSTS fixture detail");
    }
    private sealed class NamedToken(string value) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken ct) => new(value, DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken ct) => new(GetToken(context, ct));
    }
    private const string InvitationJson = "{\"inviteRedeemUrl\":\"https://login.microsoftonline.com/redeem?x=1\",\"invitedUser\":{\"id\":\"11111111-1111-1111-1111-111111111111\"}}";
    private static readonly InvitingUser Admin = new("tenant-fixture", "user-api-token");

    [Fact] public async Task InviteSendsAsSignedInAdministratorWhenDelegationWorks()
    {
        var handler = new Handler((_, _) => Json(HttpStatusCode.Created, InvitationJson));
        InvitingUser? seen = null;
        var directory = new GraphDirectoryInvitations(new HttpClient(handler), new NamedToken("app-token"), u => { seen = u; return new NamedToken("delegated-token"); });
        var result = await directory.Invite("guest@partner.example", new Uri("https://evaluation.example.test/"), "m", Admin, default);
        Assert.Equal(InvitationSender.Admin, result.SentAs); Assert.Null(result.Note);
        Assert.Equal(Admin, seen);
        var (request, _) = Assert.Single(handler.Requests);
        Assert.Equal("delegated-token", request.Headers.Authorization!.Parameter);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, InvitationSender.NotAllowed)]
    [InlineData(HttpStatusCode.Unauthorized, InvitationSender.Unavailable)]
    public async Task InviteFallsBackToAppIdentityWhenAdministratorCannotInvite(HttpStatusCode delegatedStatus, string note)
    {
        var handler = new Handler((request, _) => request.Headers.Authorization!.Parameter == "delegated-token"
            ? Json(delegatedStatus, "{}") : Json(HttpStatusCode.Created, InvitationJson));
        var directory = new GraphDirectoryInvitations(new HttpClient(handler), new NamedToken("app-token"), _ => new NamedToken("delegated-token"));
        var result = await directory.Invite("guest@partner.example", new Uri("https://evaluation.example.test/"), "m", Admin, default);
        Assert.Equal(InvitationSender.App, result.SentAs); Assert.Equal(note, result.Note);
        Assert.Equal(["delegated-token", "app-token"], handler.Requests.Select(r => r.Request.Headers.Authorization!.Parameter));
    }

    [Fact] public async Task InviteFallsBackWhenTokenExchangeFailsWithoutLeakingDetails()
    {
        var handler = new Handler((_, _) => Json(HttpStatusCode.Created, InvitationJson));
        var directory = new GraphDirectoryInvitations(new HttpClient(handler), new NamedToken("app-token"), _ => new FailingToken());
        var result = await directory.Invite("guest@partner.example", new Uri("https://evaluation.example.test/"), "m", Admin, default);
        Assert.Equal(InvitationSender.App, result.SentAs); Assert.Equal(InvitationSender.Unavailable, result.Note);
        Assert.DoesNotContain("AADSTS", result.Note);
        Assert.Equal("app-token", Assert.Single(handler.Requests).Request.Headers.Authorization!.Parameter);
    }

    [Fact] public async Task DelegatedServerErrorsAreNotRetriedAsTheApp()
    {
        var handler = new Handler((_, _) => Json(HttpStatusCode.InternalServerError, "{}"));
        var directory = new GraphDirectoryInvitations(new HttpClient(handler), new NamedToken("app-token"), _ => new NamedToken("delegated-token"));
        var error = await Assert.ThrowsAsync<DirectoryUnavailableException>(() =>
            directory.Invite("guest@partner.example", new Uri("https://evaluation.example.test/"), "m", Admin, default));
        Assert.Equal(502, error.StatusCode); Assert.Single(handler.Requests);
    }

    [Fact] public async Task WithoutInviterOrDelegationTheAppIdentitySends()
    {
        var handler = new Handler((_, _) => Json(HttpStatusCode.Created, InvitationJson));
        var delegated = new GraphDirectoryInvitations(new HttpClient(handler), new NamedToken("app-token"), _ => throw new InvalidOperationException());
        Assert.Equal(InvitationSender.App, (await delegated.Invite("guest@partner.example", new Uri("https://evaluation.example.test/"), "m", null, default)).SentAs);
        var appOnly = new GraphDirectoryInvitations(new HttpClient(handler), new NamedToken("app-token"));
        Assert.Equal(InvitationSender.App, (await appOnly.Invite("guest@partner.example", new Uri("https://evaluation.example.test/"), "m", Admin, default)).SentAs);
    }

    [Theory]
    [InlineData("https://evaluation.example.test/", true)]
    [InlineData("http://localhost:5173/", true)]
    [InlineData("http://evaluation.example.test/", false)]
    [InlineData("https://evaluation.example.test/?x=1", false)]
    [InlineData("https://user@evaluation.example.test/", false)]
    [InlineData("relative/path", false)]
    [InlineData("", false)]
    public void RedirectUrlMustBeHttpsOrLoopback(string configured, bool valid) =>
        Assert.Equal(valid, InvitationSettings.RedirectUrl(configured) is not null);

    [Fact] public void MessageIsBounded()
    {
        var message = InvitationSettings.Message(new string('x', 1000));
        Assert.True(message.Length <= 500);
        Assert.StartsWith("You have been invited to the Apex Evaluation workspace", message);
    }

    private static string PeopleJson(params (Guid Id, string? Name, string? Mail, string? Upn)[] people) =>
        "{\"value\":[" + string.Join(",", people.Select(p => System.Text.Json.JsonSerializer.Serialize(new
        { id = p.Id.ToString(), displayName = p.Name, mail = p.Mail, userPrincipalName = p.Upn }))) + ",{\"id\":\"not-a-guid\",\"displayName\":\"Ignored\"}]}";
    private static GraphDirectoryInvitations Delegated(Handler handler, Func<InvitingUser, TokenCredential>? delegated = null) =>
        new(new HttpClient(handler), new NamedToken("app-token"), delegated ?? (_ => new NamedToken("delegated-token")));

    [Fact] public async Task SearchPeopleUsesTheCallersDelegatedTokenWithEscapedSearch()
    {
        var member = Guid.NewGuid(); var guest = Guid.NewGuid();
        var handler = new Handler((_, _) => Json(HttpStatusCode.OK, PeopleJson((member, "Ada Lovelace", "Ada@Apex.Example", "ada@apex.example"),
            (guest, "Ada Guest", null, "ada_partner.example#EXT#@apex.example"))));
        InvitingUser? seen = null;
        var directory = Delegated(handler, u => { seen = u; return new NamedToken("delegated-token"); });
        var people = await directory.SearchPeople("Ada \"Lo\\ve", Admin, default);
        Assert.Equal(Admin, seen);
        Assert.True(DirectoryPeople.IsAvailable(people));
        Assert.Equal(new[] { member.ToString(), guest.ToString() }, people.Select(p => p.Id).ToArray());
        Assert.Equal(("ada@apex.example", false), (people[0].Email, people[0].Guest));
        Assert.Equal(((string?)null, true), (people[1].Email, people[1].Guest));
        var (request, _) = Assert.Single(handler.Requests);
        Assert.Equal("delegated-token", request.Headers.Authorization!.Parameter);
        Assert.Equal("eventual", Assert.Single(request.Headers.GetValues("ConsistencyLevel")));
        Assert.StartsWith("https://graph.microsoft.com/v1.0/users?", request.RequestUri!.AbsoluteUri);
        var query = Uri.UnescapeDataString(request.RequestUri.Query);
        Assert.Contains("$search=\"displayName:Ada \\\"Lo\\\\ve\" OR \"mail:Ada \\\"Lo\\\\ve\" OR \"userPrincipalName:Ada \\\"Lo\\\\ve\"", query);
        Assert.Contains("$select=id,displayName,mail,userPrincipalName", query);
        Assert.Contains("$top=15", query);
    }

    [Theory]
    [InlineData("ada:(x)*", "ada x")]
    [InlineData("  o'brien@partner.example ", "o'brien@partner.example")]
    [InlineData("#EXT# \u0001 smith", "EXT smith")]
    public void SearchTermStripsCharactersGraphRejects(string input, string expected) => Assert.Equal(expected, GraphDirectoryInvitations.SearchTerm(input));

    [Fact] public async Task SearchPeopleFallsBackOnceToStartsWithWhenSearchIsRejected()
    {
        var id = Guid.NewGuid();
        var handler = new Handler((request, _) => request.Headers.Contains("ConsistencyLevel") ? Json(HttpStatusCode.BadRequest, "{}") :
            Json(HttpStatusCode.OK, PeopleJson((id, "Pat O'Brien", "pat@apex.example", "pat@apex.example"))));
        var people = await Delegated(handler).SearchPeople("o'brien", Admin, default);
        Assert.Equal(id.ToString(), Assert.Single(people).Id);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal("delegated-token", r.Request.Headers.Authorization!.Parameter));
        var fallback = Uri.UnescapeDataString(handler.Requests[1].Request.RequestUri!.Query);
        Assert.Contains("$filter=startswith(displayName,'o''brien') or startswith(mail,'o''brien') or startswith(userPrincipalName,'o''brien')", fallback);
        Assert.DoesNotContain("$search", fallback);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task SearchPeopleIsUnavailableWhenGraphRefusesTheCaller(HttpStatusCode status)
    {
        var handler = new Handler((_, _) => Json(status, "{\"error\":{\"message\":\"secret\"}}"));
        var people = await Delegated(handler).SearchPeople("ada", Admin, default);
        Assert.False(DirectoryPeople.IsAvailable(people)); Assert.Empty(people);
        Assert.Equal("delegated-token", Assert.Single(handler.Requests).Request.Headers.Authorization!.Parameter);
    }

    [Fact] public async Task SearchPeopleNeverFallsBackToTheAppIdentity()
    {
        var handler = new Handler((_, _) => Json(HttpStatusCode.OK, "{\"value\":[]}"));
        Assert.False(DirectoryPeople.IsAvailable(await new GraphDirectoryInvitations(new HttpClient(handler), new NamedToken("app-token"))
            .SearchPeople("ada", Admin, default)));
        Assert.False(DirectoryPeople.IsAvailable(await Delegated(handler).SearchPeople("ada", null, default)));
        Assert.False(DirectoryPeople.IsAvailable(await Delegated(handler, _ => new FailingToken()).SearchPeople("ada", Admin, default)));
        Assert.False(DirectoryPeople.IsAvailable(await Delegated(handler, _ => throw new ArgumentException("bad")).SearchPeople("ada", Admin, default)));
        Assert.Empty(handler.Requests);
        Assert.True(DirectoryPeople.IsAvailable(await Delegated(handler).SearchPeople("ada", Admin, default)));
        Assert.Equal("delegated-token", Assert.Single(handler.Requests).Request.Headers.Authorization!.Parameter);
    }

    [Fact] public async Task GraphServerErrorsDuringSearchAreSafeErrors()
    {
        var handler = new Handler((_, _) => Json(HttpStatusCode.InternalServerError, "{\"error\":{\"message\":\"secret ada@apex.example\"}}"));
        var error = await Assert.ThrowsAsync<DirectoryUnavailableException>(() => Delegated(handler).SearchPeople("ada", Admin, default));
        Assert.Equal(502, error.StatusCode); Assert.DoesNotContain("secret", error.Message);
    }

    [Fact] public async Task GetPersonUsesDelegatedLookupAndReturnsNullWhenMissing()
    {
        var id = Guid.NewGuid();
        var handler = new Handler((request, _) => request.RequestUri!.AbsolutePath.EndsWith(id.ToString(), StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, $"{{\"id\":\"{id}\",\"displayName\":\"Ada\",\"mail\":null,\"userPrincipalName\":\"ada@apex.example\"}}")
            : Json(HttpStatusCode.NotFound, "{}"));
        var directory = Delegated(handler);
        Assert.Equal(new DirectoryPerson(id.ToString(), "Ada", null, "ada@apex.example"), await directory.GetPerson(id.ToString().ToUpperInvariant(), Admin, default));
        Assert.Null(await directory.GetPerson(Guid.NewGuid().ToString(), Admin, default));
        Assert.Null(await directory.GetPerson("not-a-guid", Admin, default));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal($"https://graph.microsoft.com/v1.0/users/{id}?$select=id,displayName,mail,userPrincipalName",
            Uri.UnescapeDataString(handler.Requests[0].Request.RequestUri!.AbsoluteUri));
        Assert.All(handler.Requests, r => Assert.Equal("delegated-token", r.Request.Headers.Authorization!.Parameter));
    }

    [Fact] public async Task GetPersonFailsSafelyWithoutDelegatedAccess()
    {
        var handler = new Handler((_, _) => Json(HttpStatusCode.Forbidden, "{}"));
        var id = Guid.NewGuid().ToString();
        foreach (var lookup in new Func<Task<DirectoryPerson?>>[] {
            () => Delegated(handler).GetPerson(id, Admin, default),
            () => Delegated(handler).GetPerson(id, null, default),
            () => new GraphDirectoryInvitations(new HttpClient(handler), new NamedToken("app-token")).GetPerson(id, Admin, default) })
        {
            var error = await Assert.ThrowsAsync<DirectoryUnavailableException>(lookup);
            Assert.Equal((503, DirectorySearch.LookupUnavailable), (error.StatusCode, error.Message));
        }
        Assert.Equal("delegated-token", Assert.Single(handler.Requests).Request.Headers.Authorization!.Parameter);
    }
}
