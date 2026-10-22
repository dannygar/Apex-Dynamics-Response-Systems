using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Evaluation.Core;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;

const long UploadRequestLimit = 100L * 1024 * 1024 + 1024 * 1024;
// People picker: search results and people added per request.
const int PeopleLimit = 20;
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEvaluation(builder.Configuration);
// Azure Container Apps ingress terminates TLS and forwards X-Forwarded-For/Proto from its own internal network.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear(); options.KnownProxies.Clear();
});
builder.Services.Configure<JsonOptions>(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    var tenant = builder.Configuration["Entra:TenantId"];
    var audience = builder.Configuration["Entra:Audience"];
    // No local bypass. Missing identity configuration fails closed without preventing health checks.
    options.Authority = Guid.TryParse(tenant, out _) ? $"https://login.microsoftonline.com/{tenant}/v2.0" : null;
    options.Audience = audience;
    options.MapInboundClaims = false;
    options.RequireHttpsMetadata = true;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateAudience = true, ValidateIssuer = true, ValidateLifetime = true, ValidateIssuerSigningKey = true,
        ValidIssuer = Guid.TryParse(tenant, out _) ? $"https://login.microsoftonline.com/{tenant}/v2.0" : "urn:unconfigured",
        ValidAudience = audience ?? "urn:unconfigured", ClockSkew = TimeSpan.FromMinutes(1)
    };
});
builder.Services.AddAuthorization(options => options.AddPolicy("EvaluationDelegated", policy =>
    policy.RequireAuthenticatedUser().RequireAssertion(context =>
    {
        var required = builder.Configuration["Entra:RequiredScope"] ?? "Evaluation.Access";
        return !string.IsNullOrWhiteSpace(required) &&
            (context.User.FindFirstValue("scp") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(required, StringComparer.Ordinal);
    })));
builder.Services.AddScoped<TeamAccess>();
builder.Services.AddHttpContextAccessor();
// Microsoft Graph B2B invitations use the API's user-assigned managed identity (AZURE_CLIENT_ID) via DefaultAzureCredential.
builder.Services.AddHttpClient("graph", client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<Azure.Core.TokenCredential>(_ => GraphDirectoryInvitations.CreateCredential(builder.Configuration["AZURE_CLIENT_ID"]));
// Optional: Invitations:DelegatedClientId (the API app registration's client ID, which trusts the managed identity
// through a federated credential) lets Microsoft send invitations as the signed-in administrator.
builder.Services.AddTransient<IDirectoryInvitations>(sp =>
{
    var managedIdentity = sp.GetRequiredService<Azure.Core.TokenCredential>();
    var delegatedClientId = builder.Configuration["Invitations:DelegatedClientId"];
    return new GraphDirectoryInvitations(sp.GetRequiredService<IHttpClientFactory>().CreateClient("graph"), managedIdentity,
        Guid.TryParse(delegatedClientId, out _) ? GraphDirectoryInvitations.CreateDelegatedCredential(delegatedClientId!, managedIdentity) : null);
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("team", context => RateLimitPartition.GetFixedWindowLimiter(
        $"{context.User.FindFirstValue("tid")}:{context.User.FindFirstValue("oid")}",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    // Invitation creation/resend triggers Microsoft email delivery; limited separately per user.
    options.AddPolicy("invitations", context => RateLimitPartition.GetFixedWindowLimiter(
        $"{context.User.FindFirstValue("tid")}:{context.User.FindFirstValue("oid")}",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromHours(1), QueueLimit = 0 }));
    // Self-service team creation (any signed-in tenant user).
    options.AddPolicy("teams-create", context => RateLimitPartition.GetFixedWindowLimiter(
        $"{context.User.FindFirstValue("tid")}:{context.User.FindFirstValue("oid")}",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromHours(1), QueueLimit = 0 }));
});
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = PrivateFiles.MaximumBytes + 65536);
// Per-command SQL and per-request hosting logs cost Log Analytics ingestion and add latency; keep warnings and errors.
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
// Static assets and JSON are text; compression cuts the ~700 KB SPA download by roughly 70%. Bearer tokens travel in
// headers (no cookies), so cross-site BREACH-style probing of compressed responses isn't possible.
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
    options.MimeTypes = Microsoft.AspNetCore.ResponseCompression.ResponseCompressionDefaults.MimeTypes.Concat(["image/svg+xml"]);
});
builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);
builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);
// Opens the first database connection (managed-identity token + TLS login) before the replica takes traffic.
builder.Services.AddHostedService<DatabaseWarmup>();
var app = builder.Build();
if (args.Contains("--initialize-database"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<EvaluationDb>().Database.MigrateAsync();
    return;
}
app.UseForwardedHeaders();
app.UseResponseCompression();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Cache-Control"] = "no-store";
    try { await next(); }
    catch (AccessDeniedException) { context.Response.StatusCode = 404; }
    catch (DirectoryUnavailableException e) { await Error(context, e.StatusCode, e.Message); }
    catch (ArgumentException e) { await Error(context, 400, e.Message); }
    catch (InvalidDataException e) { await Error(context, 422, e.Message); }
    catch (DbUpdateConcurrencyException) { await Error(context, 409, "Concurrent update; refresh and retry."); }
    catch (DbUpdateException) { await Error(context, 409, "Database conflict; refresh and retry with the same idempotency key."); }
});
// Built SPA (Vite dist copied to wwwroot) is served same-origin with the API when present.
var spaRoot = app.Configuration["Spa:Root"] is { Length: > 0 } configuredRoot ? Path.GetFullPath(configuredRoot) :
    Path.Combine(app.Environment.ContentRootPath, "wwwroot");
var spa = File.Exists(Path.Combine(spaRoot, "index.html")) ? new PhysicalFileProvider(spaRoot) : null;
// Vite builds with base /Apex-Dynamics-Response-Systems/; assets are served there and at the root.
var spaBase = app.Configuration["Spa:BasePath"] ?? "/Apex-Dynamics-Response-Systems";
if (spa is not null)
{
    foreach (var prefix in new[] { "", spaBase.TrimEnd('/') }.Distinct())
    {
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = spa, RequestPath = prefix });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = spa, RequestPath = prefix, ContentTypeProvider = new FileExtensionContentTypeProvider(),
            OnPrepareResponse = file =>
            {
                if (file.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) HtmlHeaders(file.Context, file.File.Name);
                else file.Context.Response.Headers["Cache-Control"] = file.Context.Request.Path.Value?.Contains("/assets/", StringComparison.Ordinal) == true
                    ? "public, max-age=31536000, immutable" : "no-cache";
            }
        });
    }
}
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapGet("/health", () => Results.Ok(new { status = "running" }));
if (spa is not null)
{
    // Client-side fallback only for page navigations. /api and /health never fall back to HTML.
    app.MapFallback(async context =>
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/api") || path.StartsWithSegments("/health") ||
            !(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)) ||
            Path.HasExtension(path.Value ?? ""))
        { context.Response.StatusCode = 404; return; }
        HtmlHeaders(context, "index.html");
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(spa.GetFileInfo("index.html"));
    });
}
var api = app.MapGroup("/api").RequireAuthorization("EvaluationDelegated").RequireRateLimiting("team");
api.MapGet("/me", async (ClaimsPrincipal user, TeamAccess access, EvaluationDb db, CancellationToken ct) =>
{
    var (tenant, subject) = access.Caller();
    var (email, name) = Profile(user);
    if (email is not null || name is not null)
    {
        // Cheap conditional refresh of display claims; absent claims never clear stored values.
        var tenantTeams = db.Teams.Where(t => t.TenantId == tenant).Select(t => t.Id);
        await db.Memberships.Where(m => m.Subject == subject && tenantTeams.Contains(m.TeamId) &&
                ((email != null && m.Email != email) || (name != null && m.DisplayName != name)))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Email, m => email ?? m.Email).SetProperty(m => m.DisplayName, m => name ?? m.DisplayName), ct);
    }
    var memberships = await (from t in db.Teams join m in db.Memberships on t.Id equals m.TeamId
                             where t.TenantId == tenant && m.Subject == subject select new { t.Id, t.Name, m.Role, t.IsPlatformAdmin }).ToListAsync(ct);
    var adminTeam = memberships.FirstOrDefault(m => m.IsPlatformAdmin)?.Id;
    var now = DateTimeOffset.UtcNow;
    var pending = (await db.Invitations.Where(i => i.TenantId == tenant && i.InvitedSubject == subject && i.Status == InvitationStatus.Pending)
        .Select(i => i.ExpiresAt).ToListAsync(ct)).Count(expires => expires > now);
    return Results.Ok(new
    {
        subject, tenantId = tenant, name, isPlatformAdmin = adminTeam is not null, platformAdminTeamId = adminTeam,
        memberships = memberships.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(m => new { teamId = m.Id, teamName = m.Name, role = m.Role.ToString(), isPlatformAdmin = m.IsPlatformAdmin }),
        pendingInvitationCount = pending
    });
});
api.MapGet("/teams", async (TeamAccess access, EvaluationDb db, CancellationToken ct) =>
{
    var (tenant, subject) = access.Caller();
    var mine = await (from t in db.Teams join m in db.Memberships on t.Id equals m.TeamId
                      where t.TenantId == tenant && m.Subject == subject select new { t.Id, t.Name, m.Role, t.IsPlatformAdmin }).ToListAsync(ct);
    var platformAdmin = mine.Any(x => x.IsPlatformAdmin);
    // canManage: may edit thresholds and memberships. Team Administrators always; platform administrators in every
    // non-administration team they belong to, whatever their stored role.
    var teams = mine.Select(x => new TeamDto(x.Id, x.Name, x.Role.ToString(), x.IsPlatformAdmin,
        x.Role == TeamRole.Administrator || (platformAdmin && !x.IsPlatformAdmin))).ToList();
    // Platform administrators read every tenant team; without a membership their access is read-only.
    if (platformAdmin)
        teams.AddRange((await db.Teams.Where(t => t.TenantId == tenant).Select(t => new { t.Id, t.Name, t.IsPlatformAdmin }).ToListAsync(ct))
            .Where(t => mine.All(m => m.Id != t.Id)).Select(t => new TeamDto(t.Id, t.Name, Platform.ReadOnlyRole, t.IsPlatformAdmin)));
    return Results.Ok(teams.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase));
});
// Self-service: every signed-in tenant user may join any team except the platform administration team. Names and
// member counts only; team content stays behind membership.
api.MapGet("/teams/joinable", async (TeamAccess access, EvaluationDb db, CancellationToken ct) =>
{
    var (tenant, subject) = access.Caller();
    var memberOf = db.Memberships.Where(m => m.Subject == subject).Select(m => m.TeamId);
    var teams = await db.Teams.Where(t => t.TenantId == tenant && !t.IsPlatformAdmin && !memberOf.Contains(t.Id))
        .Select(t => new { t.Id, t.Name, members = db.Memberships.Count(m => m.TeamId == t.Id) }).ToListAsync(ct);
    return Results.Ok(teams.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase));
});
// Self-service team creation: the creator becomes the team's Administrator (never a platform administrator).
api.MapPost("/teams", async (NameRequest request, ClaimsPrincipal user, TeamAccess access, EvaluationDb db, CancellationToken ct) =>
{
    var (tenant, actor) = access.Caller();
    var (created, conflict) = await CreateTeam(db, tenant, actor, user, request.Name, "self-service", ct);
    if (created is null) return conflict!;
    return Results.Created($"/api/teams/{created.Id}", new { created.Id, created.Name, role = nameof(TeamRole.Administrator) });
}).RequireRateLimiting("teams-create");
var team = api.MapGroup("/teams/{teamId:guid}");
team.MapGet("/readiness", async (Guid teamId, TeamAccess access, EvaluationDb db, CancellationToken ct) =>
{
    await access.Require(teamId);
    var judge = await JudgeRegistry.Current(db, DateTimeOffset.UtcNow, ct);
    return Results.Ok(new { ready = judge.Ready, blockers = judge.Blockers, judge = judge.Contract, lastHeartbeat = judge.LastHeartbeat,
        scale = new { minimum = ScoreScale.Minimum, maximum = ScoreScale.Maximum },
        scanner = "Uploads stay Quarantined until the worker's ClamAV scanner answers and reports a clean verdict.",
        imports = "Blocked: Graph consent/sites, storage scope, or reviewed public-ingestion connector required.",
        pdf = "PDF text extraction: malware-clean files only, 500 pages / 1M characters / 30s / 256MB child-process budget; no OCR or encrypted PDFs." });
});
team.MapGet("/projects", async (Guid teamId, TeamAccess access, EvaluationDb db) =>
{ await access.Require(teamId); return Results.Ok(await db.Projects.Where(x => x.TeamId == teamId).ToListAsync()); });
team.MapGet("/projects/{projectId:guid}", async (Guid teamId, Guid projectId, TeamAccess access, EvaluationDb db) =>
{
    await access.Require(teamId);
    var project = await db.Projects.SingleOrDefaultAsync(x => x.TeamId == teamId && x.Id == projectId);
    return project is null ? Results.NotFound() : Results.Ok(project);
});
team.MapPost("/projects", async (Guid teamId, NameRequest request, TeamAccess access, EvaluationDb db) =>
{
    var actor = await access.Require(teamId, true); ValidateName(request.Name);
    var project = new Project { Id = Guid.NewGuid(), TeamId = teamId, Name = request.Name.Trim() };
    db.Projects.Add(project); Audit(db, teamId, actor, "ProjectCreated", project.Id); await db.SaveChangesAsync();
    return Results.Created($"/api/teams/{teamId}/projects/{project.Id}", project);
});
team.MapGet("/projects/{projectId:guid}/submissions", async (Guid teamId, Guid projectId, TeamAccess access, EvaluationDb db) =>
{ await access.Require(teamId); return Results.Ok(await db.Submissions.Where(x => x.TeamId == teamId && x.ProjectId == projectId).ToListAsync()); });
team.MapPost("/projects/{projectId:guid}/submissions", async (Guid teamId, Guid projectId, SubmissionRequest request, TeamAccess access, EvaluationDb db) =>
{
    var actor = await access.Require(teamId, true); ValidateName(request.Name);
    if (request.Description is null || request.Description.Length > 4000) throw new ArgumentException("Description is required, maximum 4000 characters.");
    if (!await db.Projects.AnyAsync(x => x.Id == projectId && x.TeamId == teamId)) return Results.NotFound();
    var submission = new Submission { Id = Guid.NewGuid(), TeamId = teamId, ProjectId = projectId, Name = request.Name.Trim(), Description = request.Description };
    db.Submissions.Add(submission); Audit(db, teamId, actor, "SubmissionCreated", submission.Id); await db.SaveChangesAsync();
    return Results.Created($"/api/teams/{teamId}/submissions/{submission.Id}", submission);
});
team.MapGet("/submissions/{submissionId:guid}", async (Guid teamId, Guid submissionId, TeamAccess access, EvaluationDb db) =>
{
    await access.Require(teamId);
    var submission = await db.Submissions.SingleOrDefaultAsync(x => x.TeamId == teamId && x.Id == submissionId);
    return submission is null ? Results.NotFound() : Results.Ok(new { submission,
        documents = (await db.Documents.Where(x => x.TeamId == teamId && x.SubmissionId == submissionId).ToListAsync())
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.RelativePath, StringComparer.Ordinal).Select(DocumentDto),
        scans = await db.Scans.Where(x => x.TeamId == teamId && x.SubmissionId == submissionId)
            .Select(x => new { x.Id, x.Status, x.Outcome, x.Error, x.CreatedAt }).ToListAsync() });
});
team.MapPost("/submissions/{submissionId:guid}/documents", async (Guid teamId, Guid submissionId, HttpRequest request,
    TeamAccess access, EvaluationDb db, PrivateFiles files, CancellationToken ct) =>
{
    var actor = await access.Require(teamId, true);
    if (!await db.Submissions.AnyAsync(x => x.Id == submissionId && x.TeamId == teamId, ct)) return Results.NotFound();
    if (!request.HasFormContentType) return Results.BadRequest(new { error = "Use a multipart form with role, file part(s) and, for DT or Source bundles, paths." });
    var form = await request.ReadFormAsync(ct);
    if (!Enum.TryParse<DocumentRole>(form["role"], false, out var role) || !Enum.IsDefined(role))
        return Results.BadRequest(new { error = "Role Source/DT/BRD/PRD required." });
    var uploads = form.Files.GetFiles("file");
    if (uploads.Count == 0 || uploads.Count != form.Files.Count) return Results.BadRequest(new { error = "Send each document in a 'file' part." });
    string[] paths;
    // DT and Source accept a folder or several files that share one bundle ID; each file is its own version.
    var multiFile = role is DocumentRole.DT or DocumentRole.Source;
    if (role == DocumentRole.DT)
    {
        if (uploads.Count > Artifacts.MaximumDtFiles || form["paths"].Count != uploads.Count)
            return Results.BadRequest(new { error = $"DT bundles need 1-{Artifacts.MaximumDtFiles} Markdown files and one 'paths' value per file." });
        paths = form["paths"].Select(p => Artifacts.NormalizePath(p, markdownOnly: true)).ToArray();
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
            return Results.BadRequest(new { error = "DT bundle paths must be unique (case-insensitive)." });
    }
    else if (role == DocumentRole.Source)
    {
        // Paths are optional for a single file (its name is used), required one-per-file for folders and multi-file uploads.
        if (uploads.Count > Artifacts.MaximumSourceFiles || (form["paths"].Count != uploads.Count && !(uploads.Count == 1 && form["paths"].Count == 0)))
            return Results.BadRequest(new { error = $"Source evidence uploads take 1-{Artifacts.MaximumSourceFiles} files and one 'paths' value per file." });
        paths = form["paths"].Count == 0 ? [Artifacts.NormalizePath(Path.GetFileName(uploads[0].FileName), markdownOnly: false)]
            : form["paths"].Select(p => Artifacts.NormalizePath(p, markdownOnly: false)).ToArray();
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
            return Results.BadRequest(new { error = "Source evidence paths must be unique (case-insensitive)." });
    }
    else
    {
        if (uploads.Count != 1 || form["paths"].Count != 0)
            return Results.BadRequest(new { error = "BRD and PRD uploads take exactly one file." });
        paths = [Path.GetFileName(uploads[0].FileName)];
    }
    var names = paths.Select(p => p.Split('/')[^1]).ToArray();
    if (names.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 255 || name.Any(char.IsControl) ||
        !Artifacts.SupportedExtensions.Contains(Path.GetExtension(name).ToLowerInvariant())))
        return Results.BadRequest(new { error = "Supported extensions: PDF, DOCX, md, txt (DT bundles: md only)." });
    if (uploads.Any(f => f.Length is <= 0 or > PrivateFiles.MaximumBytes))
        return Results.BadRequest(new { error = "Each file must be non-empty and at most 20 MB." });
    var incoming = uploads.Sum(f => f.Length);
    // Serialize quota admission per team across API instances using a concurrency token.
    await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
    var documents = db.Documents.Where(x => x.TeamId == teamId && x.SubmissionId == submissionId);
    if (await documents.Select(x => x.BundleId).Distinct().CountAsync(ct) >= 50 || await documents.CountAsync(ct) + uploads.Count > 1000 ||
        (await documents.SumAsync(x => (long?)x.Size, ct) ?? 0) + incoming > 100 * 1024 * 1024)
        return Results.Conflict(new { error = "Submission quota: 50 versions/bundles, 1000 files and 100 MB." });
    var stored = new List<string>();
    var versions = new List<DocumentVersion>();
    var bundleId = Guid.NewGuid();
    try
    {
        for (var i = 0; i < uploads.Count; i++)
        {
            await using var source = uploads[i].OpenReadStream();
            var blob = await files.Store(source, ct);
            stored.Add(blob.Key);
            // Single-file uploads keep version ID == bundle ID (existing behavior); multi-file uploads get one ID per file.
            var id = multiFile && uploads.Count > 1 || role == DocumentRole.DT ? Guid.NewGuid() : bundleId;
            versions.Add(new DocumentVersion { Id = id, TeamId = teamId, SubmissionId = submissionId, Role = role, FileName = names[i],
                RelativePath = paths[i], BundleId = bundleId, BlobKey = blob.Key, Hash = blob.Hash, Size = blob.Size });
        }
        db.Documents.AddRange(versions);
        foreach (var version in versions) Audit(db, teamId, actor, "DocumentQuarantined", version.Id);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
    catch
    {
        foreach (var key in stored) { try { files.Delete(key); } catch (Exception) { } }
        throw;
    }
    return Results.Accepted($"/api/teams/{teamId}/submissions/{submissionId}", new { bundleId, role, versions = versions.Select(DocumentDto) });
}).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(UploadRequestLimit));
team.MapGet("/documents/{versionId:guid}/download", async (Guid teamId, Guid versionId, TeamAccess access, EvaluationDb db, PrivateFiles files) =>
{
    var actor = await access.Require(teamId);
    var version = await db.Documents.SingleOrDefaultAsync(x => x.TeamId == teamId && x.Id == versionId);
    if (version is null) return Results.NotFound();
    if (version.Status != "Ready") return Results.Conflict(new { error = "Quarantined or rejected content cannot be downloaded." });
    Audit(db, teamId, actor, "DocumentDownloaded", version.Id); await db.SaveChangesAsync();
    return Results.File(files.Open(version.BlobKey), "application/octet-stream", version.FileName);
});
team.MapPost("/submissions/{submissionId:guid}/imports", async (Guid teamId, Guid submissionId, TeamAccess access, EvaluationDb db) =>
{
    await access.Require(teamId, true);
    if (!await db.Submissions.AnyAsync(x => x.TeamId == teamId && x.Id == submissionId)) return Results.NotFound();
    return Results.Conflict(new { error = "Imports disabled. Configure and review provider-specific authorization, consent, scope and SSRF/egress controls first. No URL is fetched." });
});
team.MapGet("/policy", async (Guid teamId, TeamAccess access, EvaluationDb db) =>
{
    await access.Require(teamId);
    var policy = await db.Policies.Where(x => x.TeamId == teamId).OrderByDescending(x => x.Version).FirstOrDefaultAsync();
    return Results.Ok(new { version = policy?.Version ?? 0, thresholds = policy is null ? null : Json.Read<PolicySnapshot>(policy.Snapshot),
        actor = policy?.Actor, createdAt = policy?.CreatedAt });
});
team.MapPut("/policy", async (Guid teamId, PolicyRequest request, TeamAccess access, EvaluationDb db) =>
{
    var actor = await access.Require(teamId, true, true);
    var entity = await db.Teams.SingleAsync(x => x.Id == teamId);
    if (entity.PolicyVersion != request.ExpectedVersion) return Results.Conflict(new { error = "Policy version changed." });
    var policy = new PolicySnapshot(entity.PolicyVersion + 1, request.DT, request.BRD, request.PRD);
    policy.Validate(); entity.PolicyVersion++;
    db.Policies.Add(new PolicyVersion { Id = Guid.NewGuid(), TeamId = teamId, Version = policy.Version, Snapshot = Json.Write(policy), Actor = actor });
    Audit(db, teamId, actor, "PolicyCreated", entity.Id); await db.SaveChangesAsync();
    return Results.Ok(policy);
});
team.MapGet("/members", async (Guid teamId, TeamAccess access, EvaluationDb db) =>
{ await access.Require(teamId, true, true); return Results.Ok(await db.Memberships.Where(x => x.TeamId == teamId).ToListAsync()); });
team.MapPut("/members", async (Guid teamId, MembershipRequest request, TeamAccess access, EvaluationDb db) =>
{
    var actor = await access.Require(teamId, true, true);
    return await SetMembership(db, teamId, request.Subject, request.Role, actor, null);
});
team.MapDelete("/members/{subject}", async (Guid teamId, string subject, TeamAccess access, EvaluationDb db) =>
{
    var actor = await access.Require(teamId, true, true);
    return await RemoveMembership(db, teamId, subject, actor, null);
});
// Open joining: any signed-in tenant user may join any team except the platform administration team, as Member,
// immediately. Pending invitations are marked Accepted for this team (the person has onboarded).
team.MapPost("/join", async (Guid teamId, ClaimsPrincipal user, TeamAccess access, EvaluationDb db, CancellationToken ct) =>
{
    var (tenant, actor) = access.Caller();
    var target = await db.Teams.AsNoTracking().SingleOrDefaultAsync(t => t.Id == teamId && t.TenantId == tenant, ct);
    if (target is null) return Results.NotFound();
    if (target.IsPlatformAdmin) return Results.Conflict(new { error = "The platform administration team can't be joined this way." });
    if (await db.Memberships.AnyAsync(m => m.TeamId == teamId && m.Subject == actor, ct))
        return Results.Conflict(new { error = "You are already a member of this team." });
    var platformAdmin = await Platform.IsAdministrator(db, tenant, actor, ct);
    var (email, name) = Profile(user);
    var now = DateTimeOffset.UtcNow;
    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    var pending = (await db.Invitations.Where(x => x.TenantId == tenant && x.InvitedSubject == actor && x.Status == InvitationStatus.Pending)
        .Select(x => new { x.Id, x.ExpiresAt }).ToListAsync(ct)).Where(x => x.ExpiresAt > now).Select(x => x.Id).ToList();
    if (pending.Count > 0)
    {
        await db.Invitations.Where(x => pending.Contains(x.Id) && x.Status == InvitationStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, InvitationStatus.Accepted).SetProperty(x => x.AcceptedAt, (DateTimeOffset?)now)
                .SetProperty(x => x.AcceptedTeamId, (Guid?)teamId), ct);
        foreach (var id in pending) Platform.Audit(db, tenant, actor, "InvitationAccepted", id, $"team={teamId}, via=join");
    }
    db.Memberships.Add(new Membership { TeamId = teamId, Subject = actor, Role = TeamRole.Member, Email = email, DisplayName = name });
    Audit(db, teamId, actor, platformAdmin ? "PlatformAdminJoined" : "MemberJoined", actor);
    if (platformAdmin) Platform.Audit(db, tenant, actor, "PlatformAdminJoined", teamId, $"role={TeamRole.Member}");
    await db.SaveChangesAsync(ct);
    await transaction.CommitAsync(ct);
    return Results.Ok(new { teamId, teamName = target.Name, role = nameof(TeamRole.Member) });
});
// Any member may leave a non-administration team; the last Administrator can't leave.
team.MapPost("/leave", async (Guid teamId, TeamAccess access, EvaluationDb db, CancellationToken ct) =>
{
    var (tenant, actor) = access.Caller();
    var target = await db.Teams.AsNoTracking().SingleOrDefaultAsync(t => t.Id == teamId && t.TenantId == tenant, ct);
    if (target is null) return Results.NotFound();
    if (target.IsPlatformAdmin) return Results.Conflict(new { error = "Leave the platform administration team from Administration → Team members." });
    var platformAdmin = await Platform.IsAdministrator(db, tenant, actor, ct);
    var result = await RemoveMembership(db, teamId, actor, actor, platformAdmin ? tenant : null);
    if (platformAdmin && result is IStatusCodeHttpResult { StatusCode: StatusCodes.Status204NoContent })
    { Platform.Audit(db, tenant, actor, "PlatformAdminLeft", teamId); await db.SaveChangesAsync(ct); }
    return result;
});
// People picker for team administrators (and platform administrators who joined the team): platform people from
// this tenant's memberships, plus directory people found with the caller's own delegated Graph permissions.
team.MapGet("/people", async (Guid teamId, string? q, HttpContext http, TeamAccess access, EvaluationDb db, IDirectoryInvitations directory,
    CancellationToken ct) =>
{
    var (tenant, _, _, blocked) = await InvitableTeam(access, db, teamId, ct);
    if (blocked is not null) return blocked;
    var query = (q ?? "").Trim();
    if (query.Length is < 2 or > 100 || query.Any(char.IsControl)) throw new ArgumentException("Search text must contain 2-100 characters.");
    var lowered = query.ToLowerInvariant();
    var tenantTeams = db.Teams.Where(t => t.TenantId == tenant).Select(t => t.Id);
    var candidates = await db.Memberships.Where(m => tenantTeams.Contains(m.TeamId) &&
            ((m.DisplayName != null && m.DisplayName.ToLower().Contains(lowered)) || (m.Email != null && m.Email.ToLower().Contains(lowered))))
        .Select(m => new { m.Subject, m.Email, m.DisplayName }).Take(500).ToListAsync(ct);
    var people = candidates.GroupBy(m => m.Subject, StringComparer.OrdinalIgnoreCase)
        .Select(g => new PersonDto(g.Key, g.Select(m => m.DisplayName).FirstOrDefault(n => n is not null), g.Select(m => m.Email).FirstOrDefault(e => e is not null),
            "platform", false, false))
        .OrderBy(p => p.DisplayName ?? p.Email ?? p.Subject, StringComparer.OrdinalIgnoreCase).Take(PeopleLimit).ToList();
    bool available; string? note = null;
    IReadOnlyList<DirectoryPerson> found;
    try
    {
        found = await directory.SearchPeople(query, Inviter(http, tenant), ct);
        available = DirectoryPeople.IsAvailable(found);
        if (!available) note = DirectorySearch.Unavailable;
    }
    catch (DirectoryUnavailableException e) { found = []; available = false; note = e.Message; }
    foreach (var person in found)
    {
        var index = people.FindIndex(p => string.Equals(p.Subject, person.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) people[index] = people[index] with { Guest = person.Guest };
        else people.Add(new PersonDto(person.Id, person.DisplayName, person.Email, "directory", person.Guest, false));
    }
    people = people.Take(PeopleLimit).ToList();
    var subjects = people.Select(p => p.Subject).ToList();
    var inTeam = (await db.Memberships.Where(m => m.TeamId == teamId && subjects.Contains(m.Subject)).Select(m => m.Subject).ToListAsync(ct))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    return Results.Ok(new { results = people.Select(p => p with { Member = inTeam.Contains(p.Subject) }), directoryAvailable = available, directoryNote = note });
});
// Adds existing people (platform or directory) as Members directly, and sends Microsoft B2B invitations to new
// external addresses. Each entry is processed independently; nothing is written before every entry is resolved.
team.MapPost("/invitations", async (Guid teamId, TeamInvitationRequest request, HttpContext http, TeamAccess access, EvaluationDb db,
    IDirectoryInvitations directory, IConfiguration config, CancellationToken ct) =>
{
    var (tenant, actor, teamName, blocked) = await InvitableTeam(access, db, teamId, ct);
    if (blocked is not null) return blocked;
    var entries = request.People ?? [];
    if (entries.Length is < 1 or > PeopleLimit) throw new ArgumentException($"Add 1-{PeopleLimit} people at a time.");
    var normalized = entries.Select(e =>
    {
        var hasSubject = !string.IsNullOrWhiteSpace(e?.Subject); var hasEmail = !string.IsNullOrWhiteSpace(e?.Email);
        if (hasSubject == hasEmail) throw new ArgumentException("Each person needs exactly one of subject (Entra object ID) or email.");
        return hasSubject ? (Subject: NormalizeSubject(e!.Subject!.Trim()), Email: (string?)null) : (Subject: (string?)null, Email: Platform.NormalizeEmail(e!.Email));
    }).Distinct().ToList();
    var inviter = Inviter(http, tenant);
    var tenantTeams = db.Teams.Where(t => t.TenantId == tenant).Select(t => t.Id);
    // Phase 1: resolve every entry (lookups only).
    var plans = new List<PersonPlan>();
    foreach (var (subject, email) in normalized)
    {
        try
        {
            if (subject is not null)
            {
                var known = await db.Memberships.Where(m => m.Subject == subject && tenantTeams.Contains(m.TeamId))
                    .Select(m => new { m.Email, m.DisplayName }).Take(50).ToListAsync(ct);
                if (known.Count > 0)
                {
                    plans.Add(new PersonPlan(subject, known.Select(k => k.Email).FirstOrDefault(x => x is not null),
                        known.Select(k => k.DisplayName).FirstOrDefault(x => x is not null), "platform"));
                    continue;
                }
                var person = await directory.GetPerson(subject, inviter, ct);
                plans.Add(person is null ? new PersonPlan(subject, null, null, "not-found") : new PersonPlan(person.Id, person.Email, person.DisplayName, "directory"));
                continue;
            }
            var byEmail = await db.Memberships.Where(m => tenantTeams.Contains(m.TeamId) && m.Email == email)
                .Select(m => new { m.Subject, m.DisplayName }).FirstOrDefaultAsync(ct);
            if (byEmail is not null) { plans.Add(new PersonPlan(byEmail.Subject, email, byEmail.DisplayName, "platform", email)); continue; }
            var user = await directory.FindUser(email!, ct);
            plans.Add(user is null ? new PersonPlan(null, email, null, "invite", email) : new PersonPlan(DirectorySubject(user.Id), email, null, "directory", email));
        }
        catch (DirectoryUnavailableException e) { plans.Add(new PersonPlan(subject, email, null, "error", email, e.Message)); }
    }
    Uri? redirect = null;
    if (plans.Any(p => p.Kind == "invite")) redirect = RequireInvitations(config);
    // Phase 2: write each entry on its own, so one failure doesn't undo the others.
    var results = new List<TeamInvitationItem>();
    foreach (var plan in plans)
    {
        var requested = plan.RequestedEmail;
        if (plan.Kind == "error") { results.Add(new TeamInvitationItem(plan.Subject, requested, null, "error", Error: plan.Error)); continue; }
        if (plan.Kind == "not-found") { results.Add(new TeamInvitationItem(plan.Subject, null, null, "not-found")); continue; }
        if (plan.Kind == "invite")
        {
            try
            {
                var outcome = await UpsertInvitation(db, directory, http, tenant, actor, plan.Email!, teamId, teamName, redirect!, null, ct);
                Audit(db, teamId, actor, outcome.Reused ? "TeamInvitationRefreshed" : "TeamInvitationCreated", outcome.Invitation.Id);
                await db.SaveChangesAsync(ct);
                results.Add(new TeamInvitationItem(null, plan.Email, null, "invited", outcome.SentAs, outcome.Note, outcome.Invitation.RedeemUrl));
            }
            catch (DirectoryUnavailableException e) { db.ChangeTracker.Clear(); results.Add(new TeamInvitationItem(null, plan.Email, null, "error", Error: e.Message)); }
            continue;
        }
        var subject = plan.Subject!;
        if (await db.Memberships.AnyAsync(m => m.TeamId == teamId && m.Subject == subject, ct))
        { results.Add(new TeamInvitationItem(subject, plan.Email, plan.Name, "already-member")); continue; }
        db.Memberships.Add(new Membership { TeamId = teamId, Subject = subject, Role = TeamRole.Member,
            Email = plan.Email is null ? null : Platform.Bound(plan.Email, 254), DisplayName = plan.Name is null ? null : Platform.Bound(plan.Name, 200) });
        Audit(db, teamId, actor, "MemberAdded", subject);
        Platform.Audit(db, tenant, actor, "TeamMemberAdded", teamId, $"subject={subject}, source={plan.Kind}, role={TeamRole.Member}");
        try { await db.SaveChangesAsync(ct); results.Add(new TeamInvitationItem(subject, plan.Email, plan.Name, "added")); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); results.Add(new TeamInvitationItem(subject, plan.Email, plan.Name, "already-member")); }
    }
    return Results.Ok(new { results });
}).RequireRateLimiting("invitations");
team.MapGet("/invitations", async (Guid teamId, TeamAccess access, EvaluationDb db, CancellationToken ct) =>
{
    var (tenant, _, _, blocked) = await InvitableTeam(access, db, teamId, ct);
    if (blocked is not null) return blocked;
    var now = DateTimeOffset.UtcNow;
    var invitations = await db.Invitations.Where(x => x.TenantId == tenant && x.SuggestedTeamId == teamId && x.Status == InvitationStatus.Pending).ToListAsync(ct);
    return Results.Ok(invitations.OrderByDescending(x => x.CreatedAt).Select(x =>
    {
        var status = x.EffectiveStatus(now);
        return new { x.Id, x.Email, status, x.ExpiresAt,
            redeemUrl = status == InvitationStatus.Pending && x.Delivery == InvitationDelivery.Email ? x.RedeemUrl : null };
    }));
});
team.MapDelete("/invitations/{invitationId:guid}", async (Guid teamId, Guid invitationId, TeamAccess access, EvaluationDb db, CancellationToken ct) =>
{
    var (tenant, actor, _, blocked) = await InvitableTeam(access, db, teamId, ct);
    if (blocked is not null) return blocked;
    var invitation = await db.Invitations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == invitationId && x.TenantId == tenant && x.SuggestedTeamId == teamId, ct);
    if (invitation is null) return Results.NotFound();
    if (invitation.Status == InvitationStatus.Accepted) return Results.Conflict(new { error = "Accepted invitations cannot be revoked; remove the membership instead." });
    if (invitation.Status == InvitationStatus.Revoked) return Results.NoContent();
    var revoked = await db.Invitations.Where(x => x.Id == invitationId && x.SuggestedTeamId == teamId && x.Status == InvitationStatus.Pending)
        .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, InvitationStatus.Revoked), ct);
    if (revoked != 1) return Results.Conflict(new { error = "The invitation changed; refresh and retry." });
    Audit(db, teamId, actor, "TeamInvitationRevoked", invitationId);
    Platform.Audit(db, tenant, actor, "InvitationRevoked", invitationId, $"team={teamId}");
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});
// Team managers move one of their team's pending invitations to another team they manage, or clear the suggestion.
team.MapPatch("/invitations/{invitationId:guid}", async (Guid teamId, Guid invitationId, SuggestionRequest request, TeamAccess access,
    EvaluationDb db, CancellationToken ct) =>
{
    var (tenant, actor, _, blocked) = await InvitableTeam(access, db, teamId, ct);
    if (blocked is not null) return blocked;
    if (request.SuggestedTeamId is Guid target && target != teamId)
    {
        try { await access.Require(target, true, true); }
        catch (AccessDeniedException) { throw new ArgumentException("You can only move invitations to teams you manage."); }
    }
    return await ChangeSuggestion(db, tenant, actor, invitationId, teamId, request.SuggestedTeamId, ct);
});
team.MapGet("/submissions/{submissionId:guid}/scans", async (Guid teamId, Guid submissionId, TeamAccess access, EvaluationDb db) =>
{ await access.Require(teamId); return Results.Ok(await db.Scans.Where(x => x.TeamId == teamId && x.SubmissionId == submissionId).Select(x => new { x.Id, x.Status, x.Outcome, x.Error, x.CreatedAt }).ToListAsync()); });
team.MapPost("/submissions/{submissionId:guid}/scans", async (Guid teamId, Guid submissionId, ScanRequest request, HttpRequest http,
    TeamAccess access, EvaluationDb db, CancellationToken ct) =>
{
    var actor = await access.Require(teamId, true);
    if (!await db.Submissions.AnyAsync(x => x.TeamId == teamId && x.Id == submissionId, ct)) return Results.NotFound();
    var key = http.Headers["Idempotency-Key"].ToString();
    if (key.Length is < 1 or > 128 || key.Any(char.IsControl)) throw new ArgumentException("Idempotency-Key of 1-128 characters is required.");
    var targets = request.TargetVersionIds ?? []; var evidence = request.EvidenceVersionIds ?? [];
    if (targets.Length == 0 || targets.Length > Artifacts.MaximumDtFiles + 2 || evidence.Length > Artifacts.MaximumSourceFiles ||
        targets.Concat(evidence).Distinct().Count() != targets.Length + evidence.Length)
        throw new ArgumentException($"Select one DT bundle and/or one BRD and/or one PRD version, plus up to {Artifacts.MaximumSourceFiles} unique Source evidence versions.");
    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json.Write(new { submissionId,
        targets = targets.Order().ToArray(), evidence = evidence.Order().ToArray() }))));
    var existing = await db.Scans.SingleOrDefaultAsync(x => x.TeamId == teamId && x.IdempotencyKey == key, ct);
    if (existing is not null) return existing.RequestHash == hash ? Results.Accepted($"/api/teams/{teamId}/scans/{existing.Id}", new { existing.Id, existing.Status }) :
        Results.Conflict(new { error = "Idempotency key was already used for different inputs." });
    var judge = await JudgeRegistry.Current(db, DateTimeOffset.UtcNow, ct);
    if (!judge.Ready || judge.Contract is null) return Results.Conflict(new { error = "Judge integration unavailable.", blockers = judge.Blockers });
    await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
    if (await db.Scans.CountAsync(x => x.TeamId == teamId && (x.Status == "Queued" || x.Status == "Preparing" || x.Status == "Evaluating"), ct) >= 2)
        return Results.Conflict(new { error = "Team concurrency budget reached (2 active scans)." });
    var ids = targets.Concat(evidence).ToArray();
    var versions = await db.Documents.Where(x => x.TeamId == teamId && x.SubmissionId == submissionId && ids.Contains(x.Id)).ToListAsync(ct);
    if (versions.Count != ids.Length || versions.Any(x => x.Status != "Ready"))
        throw new ArgumentException("All selected versions must be ready and owned by this submission.");
    if (versions.Any(x => evidence.Contains(x.Id) && x.Role != DocumentRole.Source))
        throw new ArgumentException("Evidence versions must be Source documents.");
    var inputs = versions.OrderBy(x => x.Role).ThenBy(x => x.RelativePath, StringComparer.Ordinal)
        .Select(x => new InputSnapshot(x.Id, x.Role, x.Hash, x.TextLength, x.BundleId, x.RelativePath)).ToArray();
    var bundles = inputs.Where(x => targets.Contains(x.VersionId) && x.Role == DocumentRole.DT).Select(x => x.BundleId).Distinct().ToArray();
    var members = await db.Documents.Where(x => x.TeamId == teamId && x.SubmissionId == submissionId && bundles.Contains(x.BundleId))
        .Select(x => new { x.Id, x.BundleId }).ToListAsync(ct);
    Artifacts.Group(inputs.Where(x => targets.Contains(x.VersionId)).ToArray(), members.Select(x => (x.Id, x.BundleId)).ToArray());
    var policyEntity = await db.Policies.Where(x => x.TeamId == teamId).OrderByDescending(x => x.Version).FirstOrDefaultAsync(ct);
    if (policyEntity is null) return Results.Conflict(new { error = "Configure all three thresholds first." });
    var scan = new Scan { Id = Guid.NewGuid(), TeamId = teamId, SubmissionId = submissionId, Requester = actor,
        IdempotencyKey = key, RequestHash = hash, Policy = policyEntity.Snapshot, Judge = Json.Write(judge.Contract), Inputs = Json.Write(inputs) };
    db.Scans.Add(scan);
    db.ScanInputs.AddRange(versions.Select(x => new ScanInput { TeamId = teamId, ScanId = scan.Id, VersionId = x.Id }));
    db.Outbox.Add(new OutboxEvent { Id = Guid.NewGuid(), TeamId = teamId, ScanId = scan.Id });
    Audit(db, teamId, actor, "ScanRequested", scan.Id); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
    return Results.Accepted($"/api/teams/{teamId}/scans/{scan.Id}", new { scan.Id, scan.Status });
});
team.MapGet("/scans/{scanId:guid}", async (Guid teamId, Guid scanId, TeamAccess access, EvaluationDb db) =>
{
    await access.Require(teamId);
    var scan = await db.Scans.SingleOrDefaultAsync(x => x.TeamId == teamId && x.Id == scanId);
    return scan is null ? Results.NotFound() : Results.Ok(new { scan.Id, scan.SubmissionId, scan.Status, scan.Outcome, scan.Error, scan.CreatedAt,
        inputs = Json.Read<InputSnapshot[]>(scan.Inputs), policy = Json.Read<PolicySnapshot>(scan.Policy), judge = Json.Read<JudgeContractSnapshot>(scan.Judge),
        result = scan.Result is null ? null : Json.Read<ScanResult>(scan.Result),
        invocationIds = scan.RemoteInvocationId is null ? [] : Json.Read<string[]>(scan.RemoteInvocationId) });
});

// Platform administration: members (any role) of the operator-flagged platform admin team. Others get 404.
var admin = api.MapGroup("/admin");
admin.MapGet("/teams", async (TeamAccess access, EvaluationDb db, CancellationToken ct) =>
{
    var (tenant, _, _) = await access.RequirePlatformAdmin();
    var teams = await db.Teams.Where(t => t.TenantId == tenant).Select(t => new
    {
        t.Id, t.Name, t.IsPlatformAdmin, t.CreatedAt,
        members = db.Memberships.Count(m => m.TeamId == t.Id), projects = db.Projects.Count(p => p.TeamId == t.Id),
        submissions = db.Submissions.Count(s => s.TeamId == t.Id), documents = db.Documents.Count(d => d.TeamId == t.Id),
        scans = db.Scans.Count(s => s.TeamId == t.Id)
    }).ToListAsync(ct);
    return Results.Ok(teams.OrderByDescending(t => t.IsPlatformAdmin).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase));
});
admin.MapPost("/teams", async (NameRequest request, ClaimsPrincipal user, TeamAccess access, EvaluationDb db, CancellationToken ct) =>
{
    var (tenant, actor, _) = await access.RequirePlatformAdmin();
    var (created, conflict) = await CreateTeam(db, tenant, actor, user, request.Name, "admin", ct);
    if (created is null) return conflict!;
    return Results.Created($"/api/admin/teams/{created.Id}", new { created.Id, created.Name, created.IsPlatformAdmin, created.CreatedAt,
        members = 1, projects = 0, submissions = 0, documents = 0, scans = 0, role = nameof(TeamRole.Administrator) });
});
admin.MapDelete("/teams/{teamId:guid}", async (Guid teamId, [Microsoft.AspNetCore.Mvc.FromBody] DeleteTeamRequest? request, TeamAccess access,
    EvaluationDb db, PrivateFiles files, ILoggerFactory loggers, CancellationToken ct) =>
{
    var (tenant, actor, _) = await access.RequirePlatformAdmin();
    var target = await db.Teams.AsNoTracking().SingleOrDefaultAsync(t => t.Id == teamId && t.TenantId == tenant, ct);
    if (target is null) return Results.NotFound();
    if (target.IsPlatformAdmin) return Results.Conflict(new { error = "The platform administration team cannot be deleted." });
    if (!string.Equals(request?.ConfirmName, target.Name, StringComparison.Ordinal))
        throw new ArgumentException("Type the exact team name to confirm permanent deletion.");
    TeamDeletionResult result;
    try { result = await TeamDeletion.Delete(db, tenant, teamId, actor, ct); }
    catch (TeamDeletionConflictException) { return Results.Conflict(new { error = "The team changed during deletion; refresh and retry." }); }
    if (result.Busy) return Results.Conflict(new { error = "The team has queued or running scans. Wait until they finish, then delete the team." });
    // Rows are committed; private objects are removed best effort. Keys and contents are never logged.
    int removed = 0, failed = 0;
    foreach (var key in result.ObjectKeys)
    {
        try { files.Delete(key); removed++; }
        catch (Exception) { failed++; }
    }
    if (failed > 0) loggers.CreateLogger("TeamDeletion").LogWarning(
        "Team {TeamId} deleted; {Failed} of {Total} private objects could not be removed and need operator cleanup.", teamId, failed, result.ObjectKeys.Count);
    Platform.Audit(db, tenant, actor, "TeamObjectsDeleted", teamId, $"deleted={removed}, failed={failed}");
    await db.SaveChangesAsync(CancellationToken.None);
    return Results.Ok(new { id = teamId, rows = result.Rows, objectsDeleted = removed, objectDeleteFailures = failed });
});
admin.MapGet("/teams/{teamId:guid}/members", async (Guid teamId, TeamAccess access, EvaluationDb db, CancellationToken ct) =>
{
    var (tenant, _, _) = await access.RequirePlatformAdmin();
    if (!await db.Teams.AnyAsync(t => t.Id == teamId && t.TenantId == tenant, ct)) return Results.NotFound();
    return Results.Ok(await db.Memberships.Where(x => x.TeamId == teamId).ToListAsync(ct));
});
admin.MapPut("/teams/{teamId:guid}/members/{subject}", async (Guid teamId, string subject, AdminMembershipRequest request, TeamAccess access, EvaluationDb db, CancellationToken ct) =>
{
    var (tenant, actor, _) = await access.RequirePlatformAdmin();
    if (!await db.Teams.AnyAsync(t => t.Id == teamId && t.TenantId == tenant, ct)) return Results.NotFound();
    return await SetMembership(db, teamId, subject, request.Role, actor, tenant);
});
admin.MapDelete("/teams/{teamId:guid}/members/{subject}", async (Guid teamId, string subject, TeamAccess access, EvaluationDb db, CancellationToken ct) =>
{
    var (tenant, actor, _) = await access.RequirePlatformAdmin();
    if (!await db.Teams.AnyAsync(t => t.Id == teamId && t.TenantId == tenant, ct)) return Results.NotFound();
    return await RemoveMembership(db, teamId, subject, actor, tenant);
});
admin.MapGet("/invitations", async (TeamAccess access, EvaluationDb db, IConfiguration config, CancellationToken ct) =>
{
    var (tenant, _, _) = await access.RequirePlatformAdmin();
    RequireInvitations(config);
    var invitations = await db.Invitations.Where(x => x.TenantId == tenant).ToListAsync(ct);
    var names = await TeamNames(db, tenant, invitations.SelectMany(x => new[] { x.SuggestedTeamId, x.AcceptedTeamId }), ct);
    var now = DateTimeOffset.UtcNow;
    return Results.Ok(invitations.OrderByDescending(x => x.CreatedAt).Select(x => InvitationDto(x, names, now)));
});
admin.MapPost("/invitations", async (InvitationRequest request, TeamAccess access, EvaluationDb db, IDirectoryInvitations directory,
    IConfiguration config, HttpContext http, CancellationToken ct) =>
{
    var (tenant, actor, _) = await access.RequirePlatformAdmin();
    var redirect = RequireInvitations(config);
    var email = Platform.NormalizeEmail(request.Email);
    var suggestedName = await SuggestedTeam(db, tenant, request.SuggestedTeamId, ct);
    var user = await directory.FindUser(email, ct);
    // Existing workforce members get no Microsoft email; the administrator shares the workspace link.
    var outcome = await UpsertInvitation(db, directory, http, tenant, actor, email, request.SuggestedTeamId, suggestedName, redirect,
        user is not null && string.Equals(user.UserType, "Member", StringComparison.OrdinalIgnoreCase) ? user : null, ct);
    await db.SaveChangesAsync(ct);
    var names = await TeamNames(db, tenant, [outcome.Invitation.SuggestedTeamId], ct);
    return Results.Ok(new { delivery = outcome.Delivery, reused = outcome.Reused, sentAs = outcome.SentAs, note = outcome.Note,
        invitation = InvitationDto(outcome.Invitation, names, DateTimeOffset.UtcNow) });
}).RequireRateLimiting("invitations");
admin.MapPost("/invitations/{invitationId:guid}/resend", async (Guid invitationId, TeamAccess access, EvaluationDb db, IDirectoryInvitations directory,
    IConfiguration config, HttpContext http, CancellationToken ct) =>
{
    var (tenant, actor, _) = await access.RequirePlatformAdmin();
    var redirect = RequireInvitations(config);
    var invitation = await db.Invitations.SingleOrDefaultAsync(x => x.Id == invitationId && x.TenantId == tenant, ct);
    if (invitation is null) return Results.NotFound();
    if (invitation.Status != InvitationStatus.Pending) return Results.Conflict(new { error = "Only pending or expired invitations can be resent." });
    var now = DateTimeOffset.UtcNow;
    var subjectInvitations = await db.Invitations.Where(x => x.TenantId == tenant && x.InvitedSubject == invitation.InvitedSubject &&
        x.Status == InvitationStatus.Pending && x.Id != invitation.Id).Select(x => x.ExpiresAt).ToListAsync(ct);
    if (subjectInvitations.Any(expires => expires > now)) return Results.Conflict(new { error = "A newer pending invitation exists for this person." });
    string? sentAs = null, note = null;
    if (invitation.Delivery == InvitationDelivery.Email)
    {
        var suggestedName = (await TeamNames(db, tenant, [invitation.SuggestedTeamId], ct)).GetValueOrDefault(invitation.SuggestedTeamId ?? Guid.Empty);
        var invited = await directory.Invite(invitation.Email, InvitationSettings.ForInvitation(redirect, invitation.Id), InvitationSettings.Message(suggestedName), Inviter(http, tenant), ct);
        invitation.InvitedSubject = DirectorySubject(invited.UserId); invitation.RedeemUrl = invited.RedeemUrl; sentAs = invited.SentAs; note = invited.Note;
    }
    invitation.ExpiresAt = now + Invitation.Lifetime;
    Platform.Audit(db, tenant, actor, "InvitationResent", invitation.Id, $"delivery={invitation.Delivery}, sentAs={sentAs ?? "none"}");
    await db.SaveChangesAsync(ct);
    var names = await TeamNames(db, tenant, [invitation.SuggestedTeamId], ct);
    return Results.Ok(new { delivery = invitation.Delivery, reused = true, sentAs, note, invitation = InvitationDto(invitation, names, now) });
}).RequireRateLimiting("invitations");
// Platform administrators change (or clear) the suggested team of any pending invitation. No email is sent; the
// invitee's Join screen preselects the new team, and Resend or an Outlook draft can notify them.
admin.MapPatch("/invitations/{invitationId:guid}", async (Guid invitationId, SuggestionRequest request, TeamAccess access, EvaluationDb db,
    CancellationToken ct) =>
{
    var (tenant, actor, _) = await access.RequirePlatformAdmin();
    return await ChangeSuggestion(db, tenant, actor, invitationId, null, request.SuggestedTeamId, ct);
});
admin.MapDelete("/invitations/{invitationId:guid}", async (Guid invitationId, TeamAccess access, EvaluationDb db, IConfiguration config, CancellationToken ct) =>
{
    var (tenant, actor, _) = await access.RequirePlatformAdmin();
    RequireInvitations(config);
    var invitation = await db.Invitations.SingleOrDefaultAsync(x => x.Id == invitationId && x.TenantId == tenant, ct);
    if (invitation is null) return Results.NotFound();
    if (invitation.Status == InvitationStatus.Accepted) return Results.Conflict(new { error = "Accepted invitations cannot be revoked; remove the membership instead." });
    if (invitation.Status == InvitationStatus.Revoked) return Results.NoContent();
    // Conditional update so a concurrent acceptance and revocation cannot both succeed.
    var revoked = await db.Invitations.Where(x => x.Id == invitationId && x.Status == InvitationStatus.Pending)
        .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, InvitationStatus.Revoked), ct);
    if (revoked != 1) return Results.Conflict(new { error = "The invitation changed; refresh and retry." });
    Platform.Audit(db, tenant, actor, "InvitationRevoked", invitationId);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

// Invitee side: only the caller's own (tid, oid) invitations; team names only, no emails or inviter identity.
api.MapGet("/invitations/mine", async (TeamAccess access, EvaluationDb db, IConfiguration config, CancellationToken ct) =>
{
    RequireInvitations(config);
    var (tenant, subject) = access.Caller();
    var now = DateTimeOffset.UtcNow;
    var invitations = (await db.Invitations.Where(x => x.TenantId == tenant && x.InvitedSubject == subject && x.Status == InvitationStatus.Pending)
        .ToListAsync(ct)).Where(x => x.ExpiresAt > now).OrderBy(x => x.ExpiresAt).ToList();
    if (invitations.Count == 0) return Results.Ok(new { invitations = Array.Empty<object>(), joinableTeams = Array.Empty<object>() });
    var memberOf = db.Memberships.Where(m => m.Subject == subject).Select(m => m.TeamId);
    var joinable = await db.Teams.Where(t => t.TenantId == tenant && !t.IsPlatformAdmin && !memberOf.Contains(t.Id))
        .Select(t => new { t.Id, t.Name }).ToListAsync(ct);
    var names = await TeamNames(db, tenant, invitations.Select(x => x.SuggestedTeamId), ct);
    return Results.Ok(new
    {
        invitations = invitations.Select(x => new { x.Id, x.SuggestedTeamId,
            suggestedTeamName = x.SuggestedTeamId is Guid s ? names.GetValueOrDefault(s) : null, x.ExpiresAt }),
        joinableTeams = joinable.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
    });
});
api.MapPost("/invitations/{invitationId:guid}/accept", async (Guid invitationId, AcceptInvitationRequest request, ClaimsPrincipal user,
    TeamAccess access, EvaluationDb db, IConfiguration config, CancellationToken ct) =>
{
    RequireInvitations(config);
    var (tenant, subject) = access.Caller();
    var invitation = await db.Invitations.AsNoTracking()
        .SingleOrDefaultAsync(x => x.Id == invitationId && x.TenantId == tenant && x.InvitedSubject == subject, ct);
    if (invitation is null) return Results.NotFound();
    var now = DateTimeOffset.UtcNow;
    var status = invitation.EffectiveStatus(now);
    if (status != InvitationStatus.Pending) return Results.Conflict(new { error = status == InvitationStatus.Expired ?
        "This invitation has expired. Ask a platform administrator to resend it." : "This invitation has already been used or was revoked." });
    var target = await db.Teams.AsNoTracking().SingleOrDefaultAsync(t => t.Id == request.TeamId && t.TenantId == tenant && !t.IsPlatformAdmin, ct);
    if (target is null) return Results.NotFound();
    if (await db.Memberships.AnyAsync(m => m.TeamId == target.Id && m.Subject == subject, ct))
        return Results.Conflict(new { error = "You are already a member of that team." });
    var (email, name) = Profile(user);
    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    // Single use under concurrency: only one request can move the invitation out of Pending.
    var claimed = await db.Invitations.Where(x => x.Id == invitationId && x.TenantId == tenant && x.InvitedSubject == subject && x.Status == InvitationStatus.Pending)
        .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, InvitationStatus.Accepted).SetProperty(x => x.AcceptedAt, (DateTimeOffset?)now)
            .SetProperty(x => x.AcceptedTeamId, (Guid?)target.Id), ct);
    if (claimed != 1) return Results.Conflict(new { error = "This invitation has already been used or was revoked." });
    db.Memberships.Add(new Membership { TeamId = target.Id, Subject = subject, Role = TeamRole.Member, Email = email, DisplayName = name });
    Audit(db, target.Id, subject, "InvitationAccepted", invitationId);
    Platform.Audit(db, tenant, subject, "InvitationAccepted", invitationId, $"team={target.Id}");
    await db.SaveChangesAsync(ct);
    await transaction.CommitAsync(ct);
    return Results.Ok(new { teamId = target.Id, teamName = target.Name, role = nameof(TeamRole.Member) });
});
app.Run();

static (string? Email, string? Name) Profile(ClaimsPrincipal user)
{
    static string? Clean(string? value, int max)
    { value = value?.Trim(); return string.IsNullOrEmpty(value) || value.Length > max || value.Any(char.IsControl) ? null : value; }
    var email = user.FindFirstValue("email") is { } e && e.Contains('@') ? e :
        user.FindFirstValue("preferred_username") is { } p && p.Contains('@') ? p : null;
    return (Clean(email, 254)?.ToLowerInvariant(), Clean(user.FindFirstValue("name"), 200));
}
static string NormalizeSubject(string? subject) =>
    Guid.TryParse(subject, out var parsed) ? parsed.ToString("D") : throw new ArgumentException("A valid Entra object ID (GUID) is required.");
static string DirectorySubject(string? id) => Guid.TryParse(id, out var parsed) ? parsed.ToString("D") :
    throw new DirectoryUnavailableException(502, "Microsoft Graph returned an invalid user object ID.");
static Uri RequireInvitations(IConfiguration config) =>
    InvitationSettings.RedirectUrl(config["Invitations:RedirectUrl"]) ?? throw new DirectoryUnavailableException(503, InvitationSettings.NotConfigured);
// The caller's validated bearer token (JwtBearer already checked issuer, audience, scope and lifetime) for the
// on-behalf-of exchange. Never logged or persisted.
static InvitingUser? Inviter(HttpContext http, string tenant)
{
    var header = http.Request.Headers.Authorization.ToString();
    return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && header.Length > 7 ? new InvitingUser(tenant, header[7..].Trim()) : null;
}
static async Task<string?> SuggestedTeam(EvaluationDb db, string tenant, Guid? teamId, CancellationToken ct)
{
    if (teamId is null) return null;
    var found = await db.Teams.Where(t => t.Id == teamId && t.TenantId == tenant).Select(t => new { t.Name, t.IsPlatformAdmin }).SingleOrDefaultAsync(ct);
    if (found is null || found.IsPlatformAdmin) throw new ArgumentException("Suggested team must be an existing team other than the platform administration team.");
    return found.Name;
}
// Only stored-Pending invitations change (expired ones too, so Resend can revive them); the conditional update keeps a
// concurrent acceptance or revocation from being overwritten. scopeTeam limits team managers to their team's invitations.
static async Task<IResult> ChangeSuggestion(EvaluationDb db, string tenant, string actor, Guid invitationId, Guid? scopeTeam,
    Guid? suggestedTeamId, CancellationToken ct)
{
    var invitation = await db.Invitations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == invitationId && x.TenantId == tenant &&
        (scopeTeam == null || x.SuggestedTeamId == scopeTeam), ct);
    if (invitation is null) return Results.NotFound();
    if (invitation.Status != InvitationStatus.Pending)
        return Results.Conflict(new { error = "Only pending invitations can change their suggested team." });
    var name = await SuggestedTeam(db, tenant, suggestedTeamId, ct);
    var changed = await db.Invitations.Where(x => x.Id == invitationId && x.TenantId == tenant && x.Status == InvitationStatus.Pending &&
            (scopeTeam == null || x.SuggestedTeamId == scopeTeam))
        .ExecuteUpdateAsync(s => s.SetProperty(x => x.SuggestedTeamId, suggestedTeamId), ct);
    if (changed != 1) return Results.Conflict(new { error = "The invitation changed; refresh and retry." });
    Platform.Audit(db, tenant, actor, "InvitationSuggestionChanged", invitationId,
        $"from={invitation.SuggestedTeamId?.ToString() ?? "none"}, to={suggestedTeamId?.ToString() ?? "none"}");
    if (invitation.SuggestedTeamId is Guid from) Audit(db, from, actor, "InvitationSuggestionChanged", invitationId);
    if (suggestedTeamId is Guid to && to != invitation.SuggestedTeamId) Audit(db, to, actor, "InvitationSuggestionChanged", invitationId);
    await db.SaveChangesAsync(ct);
    var updated = await db.Invitations.AsNoTracking().SingleAsync(x => x.Id == invitationId, ct);
    var names = suggestedTeamId is Guid id && name is not null ? new Dictionary<Guid, string> { [id] = name } : new Dictionary<Guid, string>();
    return Results.Ok(InvitationDto(updated, names, DateTimeOffset.UtcNow));
}
// Shared by platform administration and self-service. The creator becomes the team's first Administrator, so it is
// usable immediately. Case-insensitive uniqueness is enforced here; the (TenantId, Name) unique index backs it up.
static async Task<(Team? Created, IResult? Conflict)> CreateTeam(EvaluationDb db, string tenant, string actor, ClaimsPrincipal user, string? rawName,
    string origin, CancellationToken ct)
{
    Platform.ValidateName(rawName);
    var name = rawName!.Trim();
    var existing = await db.Teams.Where(t => t.TenantId == tenant).Select(t => t.Name).ToListAsync(ct);
    if (existing.Any(n => string.Equals(n.Trim(), name, StringComparison.OrdinalIgnoreCase)))
        return (null, Results.Conflict(new { error = "A team with that name already exists." }));
    var created = new Team { Id = Guid.NewGuid(), TenantId = tenant, Name = name };
    db.Teams.Add(created);
    var (email, displayName) = Profile(user);
    db.Memberships.Add(new Membership { TeamId = created.Id, Subject = actor, Role = TeamRole.Administrator, Email = email, DisplayName = displayName });
    Platform.Audit(db, tenant, actor, "TeamCreated", created.Id, $"name={name}, creatorRole={TeamRole.Administrator}, via={origin}");
    Audit(db, created.Id, actor, "MembershipCreatorAdministrator", actor);
    await db.SaveChangesAsync(ct);
    return (created, null);
}
// Teams the caller may invite people into: manageable (team Administrator, or a platform administrator who joined a
// non-administration team). The platform administration team is managed from Administration only.
static async Task<(string Tenant, string Actor, string TeamName, IResult? Blocked)> InvitableTeam(TeamAccess access, EvaluationDb db, Guid teamId,
    CancellationToken ct)
{
    var actor = await access.Require(teamId, true, true);
    var (tenant, _) = access.Caller();
    var target = await db.Teams.AsNoTracking().Where(t => t.Id == teamId && t.TenantId == tenant).Select(t => new { t.Name, t.IsPlatformAdmin }).SingleAsync(ct);
    return (tenant, actor, target.Name, target.IsPlatformAdmin
        ? Results.Conflict(new { error = "Manage the platform administration team's members from Administration → Team members." }) : null);
}
// Creates or refreshes the pending invitation for one email address (one pending invitation per person). With an
// existing directory member no email is sent; otherwise Microsoft sends a B2B invitation. Changes are saved by the caller.
static async Task<InvitationOutcome> UpsertInvitation(EvaluationDb db, IDirectoryInvitations directory, HttpContext http, string tenant, string actor,
    string email, Guid? suggestedTeamId, string? suggestedName, Uri redirect, DirectoryUser? existingMember, CancellationToken ct)
{
    var now = DateTimeOffset.UtcNow;
    var byEmail = (await db.Invitations.Where(x => x.TenantId == tenant && x.Email == email && x.Status == InvitationStatus.Pending).ToListAsync(ct))
        .Where(x => x.ExpiresAt > now).OrderByDescending(x => x.CreatedAt).FirstOrDefault();
    var id = byEmail?.Id ?? Guid.NewGuid();
    string subject, delivery; string? redeem = null, sentAs = null, note = null;
    if (existingMember is not null) { subject = DirectorySubject(existingMember.Id); delivery = InvitationDelivery.ExistingMember; }
    else
    {
        var invited = await directory.Invite(email, InvitationSettings.ForInvitation(redirect, id), InvitationSettings.Message(suggestedName), Inviter(http, tenant), ct);
        subject = DirectorySubject(invited.UserId); delivery = InvitationDelivery.Email; redeem = invited.RedeemUrl; sentAs = invited.SentAs; note = invited.Note;
    }
    var invitation = (await db.Invitations.Where(x => x.TenantId == tenant && x.InvitedSubject == subject && x.Status == InvitationStatus.Pending).ToListAsync(ct))
        .Where(x => x.ExpiresAt > now).OrderBy(x => x.Id == id ? 0 : 1).ThenByDescending(x => x.CreatedAt).FirstOrDefault();
    var reused = invitation is not null;
    if (invitation is null)
    {
        invitation = new Invitation { Id = byEmail is null ? id : Guid.NewGuid(), TenantId = tenant, InvitedSubject = subject, InvitedBy = actor, CreatedAt = now };
        db.Invitations.Add(invitation);
    }
    invitation.Email = email; invitation.SuggestedTeamId = suggestedTeamId; invitation.ExpiresAt = now + Invitation.Lifetime;
    invitation.Delivery = delivery; invitation.RedeemUrl = redeem;
    Platform.Audit(db, tenant, actor, reused ? "InvitationRefreshed" : "InvitationCreated", invitation.Id,
        $"delivery={delivery}, sentAs={sentAs ?? "none"}, subject={subject}, suggestedTeam={suggestedTeamId?.ToString() ?? "none"}");
    return new InvitationOutcome(invitation, delivery, reused, sentAs, note);
}
static async Task<Dictionary<Guid, string>> TeamNames(EvaluationDb db, string tenant, IEnumerable<Guid?> ids, CancellationToken ct)
{
    var wanted = ids.OfType<Guid>().Distinct().ToArray();
    if (wanted.Length == 0) return [];
    return await db.Teams.Where(t => t.TenantId == tenant && !t.IsPlatformAdmin && wanted.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
}
static object InvitationDto(Invitation x, IReadOnlyDictionary<Guid, string> names, DateTimeOffset now)
{
    var status = x.EffectiveStatus(now);
    return new
    {
        x.Id, x.Email, status, x.Delivery, x.SuggestedTeamId,
        suggestedTeamName = x.SuggestedTeamId is Guid s ? names.GetValueOrDefault(s) : null,
        x.InvitedSubject, x.InvitedBy, x.CreatedAt, x.ExpiresAt, x.AcceptedAt, x.AcceptedTeamId,
        acceptedTeamName = x.AcceptedTeamId is Guid a ? names.GetValueOrDefault(a) : null,
        // Redeem links are shown to platform administrators only, as a fallback for undelivered emails.
        redeemUrl = status == InvitationStatus.Pending && x.Delivery == InvitationDelivery.Email ? x.RedeemUrl : null
    };
}
static async Task<IResult> SetMembership(EvaluationDb db, Guid teamId, string rawSubject, TeamRole role, string actor, string? platformTenant)
{
    if (!Enum.IsDefined(role)) throw new ArgumentException("Entra object ID and valid role required.");
    var subject = NormalizeSubject(rawSubject);
    // Administrators cannot accidentally remove their own last administrator access.
    await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
    var membership = await db.Memberships.SingleOrDefaultAsync(x => x.TeamId == teamId && (x.Subject == subject || x.Subject == rawSubject));
    if (membership?.Role == TeamRole.Administrator && role != TeamRole.Administrator &&
        await db.Memberships.CountAsync(x => x.TeamId == teamId && x.Role == TeamRole.Administrator) <= 1)
        return Results.Conflict(new { error = "At least one administrator must remain." });
    if (membership is null) db.Memberships.Add(new Membership { TeamId = teamId, Subject = subject, Role = role });
    else membership.Role = role;
    Audit(db, teamId, actor, "MembershipChanged", subject);
    if (platformTenant is not null) Platform.Audit(db, platformTenant, actor, "AdminMembershipChanged", teamId, $"subject={subject}, role={role}");
    await db.SaveChangesAsync(); await transaction.CommitAsync();
    return Results.NoContent();
}
static async Task<IResult> RemoveMembership(EvaluationDb db, Guid teamId, string rawSubject, string actor, string? platformTenant)
{
    var subject = NormalizeSubject(rawSubject);
    await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
    var membership = await db.Memberships.SingleOrDefaultAsync(x => x.TeamId == teamId && (x.Subject == subject || x.Subject == rawSubject));
    if (membership is null) return Results.NotFound();
    if (membership.Role == TeamRole.Administrator && await db.Memberships.CountAsync(x => x.TeamId == teamId && x.Role == TeamRole.Administrator) <= 1)
        return Results.Conflict(new { error = "At least one administrator must remain." });
    if (await db.Teams.AnyAsync(t => t.Id == teamId && t.IsPlatformAdmin) && await db.Memberships.CountAsync(x => x.TeamId == teamId) <= 1)
        return Results.Conflict(new { error = "The platform administration team must keep at least one member." });
    db.Memberships.Remove(membership);
    Audit(db, teamId, actor, "MembershipRemoved", membership.Subject);
    if (platformTenant is not null) Platform.Audit(db, platformTenant, actor, "AdminMembershipRemoved", teamId, $"subject={membership.Subject}");
    await db.SaveChangesAsync(); await transaction.CommitAsync();
    return Results.NoContent();
}

static object DocumentDto(DocumentVersion x) => new { x.Id, x.SubmissionId, x.Role, x.FileName, x.RelativePath, x.BundleId,
    x.Hash, x.Size, x.Status, x.Error, x.TextLength, x.CreatedAt };
// HTML documents may not be framed by other origins. The MSAL redirect bridge must stay frameable by this
// origin for silent token renewal and must not receive Cross-Origin-Opener-Policy.
static void HtmlHeaders(HttpContext context, string fileName)
{
    var bridge = fileName.Equals("redirect.html", StringComparison.OrdinalIgnoreCase);
    context.Response.Headers["Cache-Control"] = "no-store";
    context.Response.Headers["Content-Security-Policy"] = bridge ? "frame-ancestors 'self'" : "frame-ancestors 'none'";
    context.Response.Headers["X-Frame-Options"] = bridge ? "SAMEORIGIN" : "DENY";
}
static void ValidateName(string? name)
{ if (string.IsNullOrWhiteSpace(name) || name.Length > 200) throw new ArgumentException("Name must contain 1-200 characters."); }
static void Audit(EvaluationDb db, Guid teamId, string actor, string action, object resource) =>
    db.Audit.Add(new AuditEvent { Id = Guid.NewGuid(), TeamId = teamId, Actor = actor, Action = action, Resource = resource.ToString()! });
static async Task Error(HttpContext context, int status, string message)
{ context.Response.StatusCode = status; await context.Response.WriteAsJsonAsync(new { error = message }); }

public record NameRequest(string Name);
public record SubmissionRequest(string Name, string Description);
public record PolicyRequest(int ExpectedVersion, double DT, double BRD, double PRD);
public record MembershipRequest(string Subject, TeamRole Role);
public record AdminMembershipRequest(TeamRole Role);
public record DeleteTeamRequest(string? ConfirmName);
public record InvitationRequest(string Email, Guid? SuggestedTeamId);
public record SuggestionRequest(Guid? SuggestedTeamId);
public record AcceptInvitationRequest(Guid TeamId);
public record TeamInvitee(string? Subject, string? Email);
public record TeamInvitationRequest(TeamInvitee[]? People);
public record PersonDto(string Subject, string? DisplayName, string? Email, string Source, bool Guest, bool Member);
public record TeamInvitationItem(string? Subject, string? Email, string? DisplayName, string Status, string? SentAs = null, string? Note = null,
    string? RedeemUrl = null, string? Error = null);
// Kind: platform | directory (add membership), invite (B2B email), not-found, error.
public record PersonPlan(string? Subject, string? Email, string? Name, string Kind, string? RequestedEmail = null, string? Error = null);
public record InvitationOutcome(Invitation Invitation, string Delivery, bool Reused, string? SentAs, string? Note);
// Role is a TeamRole name, or "PlatformAdmin" for read-only platform administrator access (never stored).
public record TeamDto(Guid Id, string Name, string Role, bool IsPlatformAdmin, bool CanManage = false);

// Best effort: a failed warm-up is logged and the first request opens the connection instead.
public sealed class DatabaseWarmup(IServiceScopeFactory scopes, ILogger<DatabaseWarmup> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<EvaluationDb>();
            if (!await db.Database.CanConnectAsync(timeout.Token)) logger.LogWarning("Database warm-up could not connect.");
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        { logger.LogWarning("Database warm-up failed ({Type}).", e.GetType().Name); }
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
public record ScanRequest(Guid[] TargetVersionIds, Guid[] EvidenceVersionIds);
public sealed class AccessDeniedException : Exception { }
public sealed class TeamAccess(IHttpContextAccessor context, EvaluationDb db)
{
    public (string Tenant, string Subject) Caller()
    {
        var user = context.HttpContext!.User;
        var subject = user.FindFirstValue("oid"); var tenant = user.FindFirstValue("tid");
        if (!Guid.TryParse(subject, out _) || !Guid.TryParse(tenant, out _)) throw new AccessDeniedException();
        return (tenant!, subject!);
    }
    public async Task<string> Require(Guid teamId, bool mutate = false, bool administrator = false)
    {
        var (tenant, subject) = Caller();
        var membership = await (from t in db.Teams join m in db.Memberships on t.Id equals m.TeamId
                                where t.Id == teamId && t.TenantId == tenant && m.Subject == subject
                                select new { m.Role, t.IsPlatformAdmin }).SingleOrDefaultAsync();
        if (membership is null)
        {
            // Platform administrators may read any team of their tenant; mutations still require a real membership role.
            if (!mutate && !administrator && await db.Teams.AnyAsync(t => t.Id == teamId && t.TenantId == tenant) &&
                await Platform.IsAdministrator(db, tenant, subject)) return subject;
            throw new AccessDeniedException();
        }
        if (!Enum.IsDefined(membership.Role)) throw new AccessDeniedException();
        if ((mutate && membership.Role == TeamRole.Reviewer) || (administrator && membership.Role != TeamRole.Administrator))
        {
            // A platform administrator who joined a team (other than the administration team) manages its settings
            // (thresholds, memberships) and works in it like a team Administrator, whatever the stored role.
            if (!membership.IsPlatformAdmin && await Platform.IsAdministrator(db, tenant, subject)) return subject;
            throw new AccessDeniedException();
        }
        return subject;
    }
    public async Task<(string Tenant, string Subject, Guid AdminTeam)> RequirePlatformAdmin()
    {
        var (tenant, subject) = Caller();
        return await Platform.AdminTeamId(db, tenant, subject) is Guid team ? (tenant, subject, team) : throw new AccessDeniedException();
    }
}
public partial class Program { }
