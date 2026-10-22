using System.Data;
using System.Net.Mail;
using Microsoft.EntityFrameworkCore;

namespace Evaluation.Core;

public static class Platform
{
    public const string ReadOnlyRole = "PlatformAdmin";

    public static Task<Guid?> AdminTeamId(EvaluationDb db, string tenant, string subject, CancellationToken ct = default) =>
        (from t in db.Teams join m in db.Memberships on t.Id equals m.TeamId
         where t.TenantId == tenant && t.IsPlatformAdmin && m.Subject == subject select (Guid?)t.Id).FirstOrDefaultAsync(ct);

    public static async Task<bool> IsAdministrator(EvaluationDb db, string tenant, string subject, CancellationToken ct = default) =>
        await AdminTeamId(db, tenant, subject, ct) is not null;

    public static void ValidateName(string? name)
    { if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200) throw new ArgumentException("Name must contain 1-200 characters."); }

    public static string NormalizeEmail(string? email)
    {
        var value = (email ?? "").Trim().ToLowerInvariant();
        if (value.Length is < 3 or > 254 || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ||
            !MailAddress.TryCreate(value, out var parsed) || parsed.Address != value || parsed.DisplayName.Length != 0)
            throw new ArgumentException("A valid email address (maximum 254 characters) is required.");
        var at = value.LastIndexOf('@');
        var domain = value[(at + 1)..];
        if (at < 1 || !domain.Contains('.') || domain.StartsWith('.') || domain.EndsWith('.') || value.Count(c => c == '@') != 1)
            throw new ArgumentException("A valid email address (maximum 254 characters) is required.");
        return value;
    }

    public static string Bound(string? value, int max) => value is null ? "" : value.Length <= max ? value : value[..max];

    public static void Audit(EvaluationDb db, string tenant, string actor, string action, object resource, string detail = "") =>
        db.PlatformAudit.Add(new PlatformAuditEvent { Id = Guid.NewGuid(), TenantId = tenant, Actor = Bound(actor, 128), Action = Bound(action, 64),
            Resource = Bound(resource.ToString(), 256), Detail = Bound(detail, PlatformAuditEvent.DetailLimit) });
}

public sealed record TeamDeletionResult(bool Busy, IReadOnlyDictionary<string, int> Rows, IReadOnlyList<string> ObjectKeys);
// The target team disappeared or was flagged as the platform team concurrently; the transaction is rolled back.
public sealed class TeamDeletionConflictException() : InvalidOperationException("Team deletion target changed.");

// The approved permanent deletion path for one team. Historical rows are protected from change-tracker deletes by
// EvaluationDb.GuardSnapshots; this path deliberately uses set-based deletes inside one serializable transaction, in
// foreign-key order, so it is the only code that can remove them.
public static class TeamDeletion
{
    public static readonly string[] Order = ["Outbox", "ScanInputs", "Scans", "Documents", "Submissions", "Projects", "Policies", "Audit", "Memberships", "Teams"];

    public static async Task<TeamDeletionResult> Delete(EvaluationDb db, string tenant, Guid teamId, string actor, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (await db.Scans.AnyAsync(x => x.TeamId == teamId && (x.Status == "Queued" || x.Status == "Preparing" || x.Status == "Evaluating"), ct) ||
            await db.Outbox.AnyAsync(x => x.TeamId == teamId && (x.State == "Pending" || x.State == "Leased"), ct))
            return new TeamDeletionResult(true, new Dictionary<string, int>(), []);
        var documents = await db.Documents.Where(x => x.TeamId == teamId).Select(x => new { x.BlobKey, x.TextKey }).ToListAsync(ct);
        var keys = documents.SelectMany(x => new[] { x.BlobKey, x.TextKey }).OfType<string>().Where(k => k.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var rows = new Dictionary<string, int>
        {
            ["Outbox"] = await db.Outbox.Where(x => x.TeamId == teamId).ExecuteDeleteAsync(ct),
            ["ScanInputs"] = await db.ScanInputs.Where(x => x.TeamId == teamId).ExecuteDeleteAsync(ct),
            ["Scans"] = await db.Scans.Where(x => x.TeamId == teamId).ExecuteDeleteAsync(ct),
            ["Documents"] = await db.Documents.Where(x => x.TeamId == teamId).ExecuteDeleteAsync(ct),
            ["Submissions"] = await db.Submissions.Where(x => x.TeamId == teamId).ExecuteDeleteAsync(ct),
            ["Projects"] = await db.Projects.Where(x => x.TeamId == teamId).ExecuteDeleteAsync(ct),
            ["Policies"] = await db.Policies.Where(x => x.TeamId == teamId).ExecuteDeleteAsync(ct),
            ["Audit"] = await db.Audit.Where(x => x.TeamId == teamId).ExecuteDeleteAsync(ct),
            ["Memberships"] = await db.Memberships.Where(x => x.TeamId == teamId).ExecuteDeleteAsync(ct),
            ["Teams"] = await db.Teams.Where(x => x.Id == teamId && x.TenantId == tenant && !x.IsPlatformAdmin).ExecuteDeleteAsync(ct),
        };
        if (rows["Teams"] != 1) throw new TeamDeletionConflictException();
        rows["InvitationSuggestionsCleared"] = await db.Invitations.Where(x => x.TenantId == tenant && x.SuggestedTeamId == teamId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.SuggestedTeamId, (Guid?)null), ct);
        Platform.Audit(db, tenant, actor, "TeamDeleted", teamId,
            string.Join(", ", rows.Select(r => $"{r.Key}={r.Value}")) + $", PrivateObjectsScheduled={keys.Count}");
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new TeamDeletionResult(false, rows, keys);
    }
}
