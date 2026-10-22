using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Azure.Identity;

namespace Evaluation.Core;

public sealed record DirectoryUser(string Id, string? UserType);
// SentAs: "admin" when Microsoft sent the email on behalf of the signed-in administrator, "app" when the API's own
// identity sent it (Microsoft then omits the personal message). Note explains a fallback, safe to show administrators.
public sealed record DirectoryInvitationResult(string UserId, string? RedeemUrl, string SentAs = InvitationSender.App, string? Note = null);
// The signed-in user's API access token, used only for an on-behalf-of exchange to Microsoft Graph.
public sealed record InvitingUser(string TenantId, string AccessToken);
// A directory user from the caller's delegated view of Microsoft Entra ID (basic profile only).
public sealed record DirectoryPerson(string Id, string? DisplayName, string? Mail, string? UserPrincipalName)
{
    public bool Guest => UserPrincipalName?.Contains("#EXT#", StringComparison.OrdinalIgnoreCase) == true;
    // Guest UPNs (alias#EXT#@tenant) are not mail addresses.
    public string? Email => Mail ?? (Guest || UserPrincipalName?.Contains('@') != true ? null : UserPrincipalName);
}

// Directory search results. Available is false when the caller's delegated directory access isn't usable (no
// delegated configuration, no caller token, token exchange failure, Graph 401/403); the list is then empty.
public sealed class DirectoryPeople(IReadOnlyList<DirectoryPerson> people, bool available) : IReadOnlyList<DirectoryPerson>
{
    public static readonly DirectoryPeople Unavailable = new([], false);
    public bool Available { get; } = available;
    public DirectoryPerson this[int index] => people[index];
    public int Count => people.Count;
    public IEnumerator<DirectoryPerson> GetEnumerator() => people.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    public static bool IsAvailable(IReadOnlyList<DirectoryPerson> people) => people is not DirectoryPeople { Available: false };
}

public static class DirectorySearch
{
    public const string Unavailable = "Directory search isn't available for your account; type an email address to invite someone new.";
    public const string LookupUnavailable = "Directory lookup isn't available for your account; invite this person by email address instead.";
    public const int MaximumResults = 15;
}

public static class InvitationSender
{
    public const string Admin = "admin";
    public const string App = "app";
    public const string NotAllowed = "Your account can't invite guests (tenant setting: only admins and Guest Inviters), so the app identity sent it and Microsoft omitted the personal message.";
    public const string Unavailable = "Sending on your behalf wasn't available, so the app identity sent it and Microsoft omitted the personal message.";
}

// Microsoft Entra B2B directory operations used by platform and team administrators. Implementations must never
// log access tokens, email addresses, search text or Graph response bodies.
public interface IDirectoryInvitations
{
    Task<DirectoryUser?> FindUser(string email, CancellationToken ct);
    Task<DirectoryInvitationResult> Invite(string email, Uri redirectUrl, string message, InvitingUser? inviter, CancellationToken ct);
    // People search with the caller's own delegated permissions only (never the app identity), so the caller's
    // directory restrictions (for example guest restrictions) apply. Returns DirectoryPeople.Unavailable when the
    // delegated path can't be used.
    Task<IReadOnlyList<DirectoryPerson>> SearchPeople(string query, InvitingUser? caller, CancellationToken ct);
    // Delegated lookup by object ID; null when the user doesn't exist. Throws DirectoryUnavailableException when
    // delegated access isn't usable.
    Task<DirectoryPerson?> GetPerson(string id, InvitingUser? caller, CancellationToken ct);
}

// Mapped by the API error middleware to its status code with a message that is safe to show administrators.
public sealed class DirectoryUnavailableException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public static class InvitationSettings
{
    public const string NotConfigured = "Invitations are not configured: set Invitations:RedirectUrl to the HTTPS workspace URL.";
    public const string NotPermitted = "Directory invitations are not permitted for the app identity (User.Invite.All/User.ReadBasic.All admin consent required).";

    // HTTPS, or plain HTTP on localhost for development. The invitation ID and #evaluation are appended per invitation.
    public static Uri? RedirectUrl(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured) || !Uri.TryCreate(configured, UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return null;
        return uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) ? uri : null;
    }

    public static Uri ForInvitation(Uri redirect, Guid invitation) =>
        new UriBuilder(redirect) { Query = $"invitation={invitation:D}", Fragment = "evaluation" }.Uri;

    public static string Message(string? suggestedTeam)
    {
        var text = "You have been invited to the Apex Evaluation workspace. After accepting, sign in and choose the team you will join.";
        if (!string.IsNullOrWhiteSpace(suggestedTeam)) text += $" Suggested team: {Platform.Bound(suggestedTeam.Trim(), 200)}.";
        return Platform.Bound(text, 500);
    }
}

public sealed class GraphDirectoryInvitations(HttpClient http, TokenCredential credential,
    Func<InvitingUser, TokenCredential>? delegatedCredential = null) : IDirectoryInvitations
{
    private static readonly string[] Scopes = ["https://graph.microsoft.com/.default"];
    private static readonly Uri Graph = new("https://graph.microsoft.com/v1.0/");
    public const string TokenExchangeScope = "api://AzureADTokenExchange/.default";

    public static TokenCredential CreateCredential(string? managedIdentityClientId) => new DefaultAzureCredential(new DefaultAzureCredentialOptions
    { ManagedIdentityClientId = string.IsNullOrWhiteSpace(managedIdentityClientId) ? null : managedIdentityClientId, ExcludeInteractiveBrowserCredential = true });

    // On-behalf-of exchange for the API app registration without a client secret: the app trusts the API's
    // user-assigned managed identity through a federated identity credential, and the managed identity's token for
    // api://AzureADTokenExchange is the client assertion. Microsoft then sends the invitation as the administrator.
    public static Func<InvitingUser, TokenCredential> CreateDelegatedCredential(string apiClientId, TokenCredential managedIdentity) =>
        inviter => new OnBehalfOfCredential(inviter.TenantId, apiClientId,
            async ct => (await managedIdentity.GetTokenAsync(new TokenRequestContext([TokenExchangeScope]), ct)).Token,
            inviter.AccessToken, new OnBehalfOfCredentialOptions());

    public async Task<DirectoryUser?> FindUser(string email, CancellationToken ct)
    {
        var e = email.Replace("'", "''", StringComparison.Ordinal);
        // otherMails filtering is an advanced query and userType is outside the User.ReadBasic.All profile; fall back to
        // a basic-profile mail/UPN lookup when the tenant or permission set rejects the advanced form.
        var advanced = $"mail eq '{e}' or userPrincipalName eq '{e}' or otherMails/any(x:x eq '{e}')";
        using var response = await Send(() => Query(advanced, true), ct);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden)
        {
            using var basic = await Send(() => Query($"mail eq '{e}' or userPrincipalName eq '{e}'", false), ct);
            return await ReadUser(basic, ct);
        }
        return await ReadUser(response, ct);
    }

    public async Task<DirectoryInvitationResult> Invite(string email, Uri redirectUrl, string message, InvitingUser? inviter, CancellationToken ct)
    {
        HttpRequestMessage Create() => new(HttpMethod.Post, new Uri(Graph, "invitations"))
        {
            Content = JsonContent.Create(new
            {
                invitedUserEmailAddress = email, inviteRedirectUrl = redirectUrl.AbsoluteUri, sendInvitationMessage = true,
                invitedUserMessageInfo = new { customizedMessageBody = message }
            })
        };
        string? note = null;
        if (inviter is not null && delegatedCredential is not null)
        {
            HttpResponseMessage? delegated = null;
            try { delegated = await Send(Create, delegatedCredential(inviter), ct); }
            catch (DirectoryUnavailableException) { note = InvitationSender.Unavailable; }
            if (delegated is not null)
            {
                using (delegated)
                {
                    if (delegated.IsSuccessStatusCode) return await Result(delegated, InvitationSender.Admin, null, ct);
                    // 401/403: the administrator isn't allowed to invite guests or the delegated grant is missing.
                    if (delegated.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)) Ensure(delegated);
                    note = delegated.StatusCode == HttpStatusCode.Forbidden ? InvitationSender.NotAllowed : InvitationSender.Unavailable;
                }
            }
        }
        using var response = await Send(Create, credential, ct);
        return await Result(response, InvitationSender.App, note, ct);
    }

    public async Task<IReadOnlyList<DirectoryPerson>> SearchPeople(string query, InvitingUser? caller, CancellationToken ct)
    {
        // Delegated only: without the caller's own token there is no directory search (never the app identity).
        var token = await DelegatedToken(caller, ct);
        if (token is null) return DirectoryPeople.Unavailable;
        var term = SearchTerm(query);
        if (term.Length == 0) return new DirectoryPeople([], true);
        using var response = await Send(() => Search(term), token, ct);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            // Tenants or terms that $search rejects fall back once to a basic-profile prefix filter.
            using var fallback = await Send(() => StartsWith(term), token, ct);
            return await People(fallback, ct);
        }
        return await People(response, ct);
    }

    public async Task<DirectoryPerson?> GetPerson(string id, InvitingUser? caller, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var objectId)) return null;
        var token = await DelegatedToken(caller, ct) ?? throw new DirectoryUnavailableException(503, DirectorySearch.LookupUnavailable);
        using var response = await Send(() => new HttpRequestMessage(HttpMethod.Get,
            new Uri(Graph, $"users/{objectId:D}?$select=id,displayName,mail,userPrincipalName")), token, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest) return null;
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new DirectoryUnavailableException(503, DirectorySearch.LookupUnavailable);
        Ensure(response);
        var person = Person(await Read<GraphUser>(response, ct));
        return person is not null && person.Id == objectId.ToString("D") ? person : null;
    }

    // Letters, digits and common name/address punctuation are kept; anything else $search tokenizes or rejects
    // (parentheses, colons, wildcards, '#', control characters...) becomes a space.
    public static string SearchTerm(string? query)
    {
        var kept = new string((query ?? "").Select(c => char.IsLetterOrDigit(c) || c is '@' or '.' or '_' or '-' or '\'' or '+' or '"' or '\\' ? c : ' ').ToArray());
        var term = string.Join(' ', kept.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return term.Length <= 100 ? term : term[..100].TrimEnd();
    }

    private static HttpRequestMessage Search(string term)
    {
        var quoted = term.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        var search = $"\"displayName:{quoted}\" OR \"mail:{quoted}\" OR \"userPrincipalName:{quoted}\"";
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Graph,
            $"users?$search={Uri.EscapeDataString(search)}&$select=id,displayName,mail,userPrincipalName&$top={DirectorySearch.MaximumResults}"));
        request.Headers.Add("ConsistencyLevel", "eventual");
        return request;
    }

    private static HttpRequestMessage StartsWith(string term)
    {
        var t = term.Replace("'", "''", StringComparison.Ordinal);
        var filter = $"startswith(displayName,'{t}') or startswith(mail,'{t}') or startswith(userPrincipalName,'{t}')";
        return new HttpRequestMessage(HttpMethod.Get, new Uri(Graph,
            $"users?$filter={Uri.EscapeDataString(filter)}&$select=id,displayName,mail,userPrincipalName&$top={DirectorySearch.MaximumResults}"));
    }

    private static async Task<IReadOnlyList<DirectoryPerson>> People(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return DirectoryPeople.Unavailable;
        Ensure(response);
        var users = (await Read<GraphUsers>(response, ct))?.Value ?? [];
        return new DirectoryPeople(users.Select(Person).OfType<DirectoryPerson>().Take(DirectorySearch.MaximumResults).ToList(), true);
    }

    private static DirectoryPerson? Person(GraphUser? user)
    {
        static string? Clean(string? value, int max)
        { value = value?.Trim(); return string.IsNullOrEmpty(value) || value.Length > max || value.Any(char.IsControl) ? null : value; }
        return user?.Id is { } id && Guid.TryParse(id, out var objectId)
            ? new DirectoryPerson(objectId.ToString("D"), Clean(user.DisplayName, 200), Clean(user.Mail, 254)?.ToLowerInvariant(), Clean(user.UserPrincipalName, 254))
            : null;
    }

    // The caller's delegated Graph token through the on-behalf-of exchange; null when delegation isn't configured,
    // there is no caller token, or the exchange fails. Failure details (AADSTS codes) are never surfaced or logged.
    private async Task<string?> DelegatedToken(InvitingUser? caller, CancellationToken ct)
    {
        if (caller is null || delegatedCredential is null) return null;
        try { return (await delegatedCredential(caller).GetTokenAsync(new TokenRequestContext(Scopes), ct)).Token; }
        catch (Exception e) when (e is not OperationCanceledException) { return null; }
    }

    private static async Task<DirectoryInvitationResult> Result(HttpResponseMessage response, string sentAs, string? note, CancellationToken ct)
    {
        Ensure(response);
        var body = await Read<GraphInvitation>(response, ct);
        if (body?.InvitedUser?.Id is not { Length: > 0 and <= 128 } id || !Guid.TryParse(id, out _))
            throw new DirectoryUnavailableException(502, "Microsoft Graph returned an invitation without an invited user.");
        var redeem = body.InviteRedeemUrl is { Length: > 0 and <= 2048 } url && Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
            parsed.Scheme == Uri.UriSchemeHttps ? url : null;
        return new DirectoryInvitationResult(id, redeem, sentAs, note);
    }

    private static HttpRequestMessage Query(string filter, bool advanced)
    {
        var select = advanced ? "id,userType,userPrincipalName" : "id,userPrincipalName";
        var url = new Uri(Graph, $"users?$filter={Uri.EscapeDataString(filter)}&$select={select}&$top=2" + (advanced ? "&$count=true" : ""));
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (advanced) request.Headers.Add("ConsistencyLevel", "eventual");
        return request;
    }

    private Task<HttpResponseMessage> Send(Func<HttpRequestMessage> create, CancellationToken ct) => Send(create, credential, ct);

    private async Task<HttpResponseMessage> Send(Func<HttpRequestMessage> create, TokenCredential tokenSource, CancellationToken ct)
    {
        AccessToken token;
        try { token = await tokenSource.GetTokenAsync(new TokenRequestContext(Scopes), ct); }
        catch (Exception e) when (e is AuthenticationFailedException or CredentialUnavailableException)
        { throw new DirectoryUnavailableException(503, "The app identity could not obtain a Microsoft Graph token."); }
        return await Send(create, token.Token, ct);
    }

    private async Task<HttpResponseMessage> Send(Func<HttpRequestMessage> create, string token, CancellationToken ct)
    {
        using var request = create();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try { return await http.SendAsync(request, ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        { throw new DirectoryUnavailableException(502, "Microsoft Graph could not be reached."); }
    }

    private static async Task<DirectoryUser?> ReadUser(HttpResponseMessage response, CancellationToken ct)
    {
        Ensure(response);
        var users = (await Read<GraphUsers>(response, ct))?.Value ?? [];
        var user = users.FirstOrDefault(u => Guid.TryParse(u.Id, out _));
        if (user is null) return null;
        // userType is outside the basic profile; a missing value falls back to the guest UPN marker.
        var type = user.UserType ?? (user.UserPrincipalName?.Contains("#EXT#", StringComparison.OrdinalIgnoreCase) == true ? "Guest" :
            user.UserPrincipalName is null ? null : "Member");
        return new DirectoryUser(user.Id!, type);
    }

    private static void Ensure(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new DirectoryUnavailableException(503, InvitationSettings.NotPermitted),
            HttpStatusCode.TooManyRequests => new DirectoryUnavailableException(503, "Microsoft Graph is throttling directory requests; retry later."),
            _ => new DirectoryUnavailableException(502, $"Microsoft Graph rejected the directory request ({(int)response.StatusCode})."),
        };
    }

    private static async Task<T?> Read<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > 1024 * 1024) throw new DirectoryUnavailableException(502, "Microsoft Graph response was too large.");
        try { return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct); }
        catch (JsonException) { throw new DirectoryUnavailableException(502, "Microsoft Graph returned an unreadable response."); }
    }

    private sealed record GraphUsers([property: JsonPropertyName("value")] GraphUser[]? Value);
    private sealed record GraphUser([property: JsonPropertyName("id")] string? Id, [property: JsonPropertyName("userType")] string? UserType,
        [property: JsonPropertyName("userPrincipalName")] string? UserPrincipalName,
        [property: JsonPropertyName("displayName")] string? DisplayName = null, [property: JsonPropertyName("mail")] string? Mail = null);
    private sealed record GraphInvitation([property: JsonPropertyName("inviteRedeemUrl")] string? InviteRedeemUrl,
        [property: JsonPropertyName("invitedUser")] GraphInvitedUser? InvitedUser);
    private sealed record GraphInvitedUser([property: JsonPropertyName("id")] string? Id);
}
