using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Evaluation.Core;

public class Team
{
    public Guid Id { get; set; } public string TenantId { get; set; } = ""; public string Name { get; set; } = ""; public int PolicyVersion { get; set; }
    // Exactly the operator-flagged team whose members (any role) are platform administrators. Never set through the API.
    public bool IsPlatformAdmin { get; set; } public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public class Membership
{
    public Guid TeamId { get; set; } public string Subject { get; set; } = ""; public TeamRole Role { get; set; }
    public string? Email { get; set; } public string? DisplayName { get; set; }
}
public static class InvitationStatus { public const string Pending = "Pending", Accepted = "Accepted", Revoked = "Revoked", Expired = "Expired"; }
public static class InvitationDelivery { public const string Email = "email", ExistingMember = "existing-member"; }
public class Invitation
{
    public Guid Id { get; set; } public string TenantId { get; set; } = ""; public string Email { get; set; } = "";
    public Guid? SuggestedTeamId { get; set; } public string InvitedSubject { get; set; } = ""; public string Delivery { get; set; } = InvitationDelivery.Email;
    public string Status { get; set; } = InvitationStatus.Pending; public string InvitedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow; public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; } public Guid? AcceptedTeamId { get; set; } public string? RedeemUrl { get; set; }
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);
    public string EffectiveStatus(DateTimeOffset now) => Status == InvitationStatus.Pending && ExpiresAt <= now ? InvitationStatus.Expired : Status;
}
// Tenant-level audit that survives team deletion (deliberately not related to Teams).
public class PlatformAuditEvent
{
    public const int DetailLimit = 2000;
    public Guid Id { get; set; } public string TenantId { get; set; } = ""; public string Actor { get; set; } = ""; public string Action { get; set; } = "";
    public string Resource { get; set; } = ""; public string Detail { get; set; } = ""; public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public class Project { public Guid Id { get; set; } public Guid TeamId { get; set; } public string Name { get; set; } = ""; }
public class Submission { public Guid Id { get; set; } public Guid TeamId { get; set; } public Guid ProjectId { get; set; } public string Name { get; set; } = ""; public string Description { get; set; } = ""; }
public class DocumentVersion
{
    public Guid Id { get; set; } public Guid TeamId { get; set; } public Guid SubmissionId { get; set; }
    public DocumentRole Role { get; set; } public string FileName { get; set; } = ""; public string BlobKey { get; set; } = "";
    public string Hash { get; set; } = ""; public long Size { get; set; } public string Status { get; set; } = "Quarantined";
    public string? Error { get; set; } public string? TextKey { get; set; } public int TextLength { get; set; }
    public string RelativePath { get; set; } = ""; public Guid BundleId { get; set; }
    public long LeaseUntil { get; set; } public string? LeaseOwner { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public class PolicyVersion { public Guid Id { get; set; } public Guid TeamId { get; set; } public int Version { get; set; } public string Snapshot { get; set; } = ""; public string Actor { get; set; } = ""; public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow; }
public class Scan
{
    public Guid Id { get; set; } public Guid TeamId { get; set; } public Guid SubmissionId { get; set; }
    public string Requester { get; set; } = ""; public string IdempotencyKey { get; set; } = ""; public string RequestHash { get; set; } = "";
    public string Inputs { get; set; } = ""; public string Policy { get; set; } = ""; public string Judge { get; set; } = "";
    public string Status { get; set; } = "Queued"; public string Outcome { get; set; } = "Inconclusive";
    public string? Result { get; set; } public string? Assessments { get; set; } public string? Error { get; set; } public string? RemoteInvocationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public class OutboxEvent
{
    public Guid Id { get; set; } public Guid TeamId { get; set; } public Guid ScanId { get; set; }
    public string State { get; set; } = "Pending"; public int Attempts { get; set; }
    public long AvailableAt { get; set; } public long LeaseUntil { get; set; } public string? LeaseOwner { get; set; }
}
public class ScanInput
{
    public Guid TeamId { get; set; } public Guid ScanId { get; set; } public Guid VersionId { get; set; }
}
public class JudgeRegistration { public int Id { get; set; } public string Version { get; set; } = ""; public string Contract { get; set; } = ""; public DateTimeOffset UpdatedAt { get; set; } }
public class AuditEvent { public Guid Id { get; set; } public Guid TeamId { get; set; } public string Actor { get; set; } = ""; public string Action { get; set; } = ""; public string Resource { get; set; } = ""; public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow; }

public class EvaluationDb(DbContextOptions<EvaluationDb> options) : DbContext(options)
{
    public DbSet<Team> Teams => Set<Team>(); public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<Project> Projects => Set<Project>(); public DbSet<Submission> Submissions => Set<Submission>();
    public DbSet<DocumentVersion> Documents => Set<DocumentVersion>(); public DbSet<PolicyVersion> Policies => Set<PolicyVersion>();
    public DbSet<Scan> Scans => Set<Scan>(); public DbSet<OutboxEvent> Outbox => Set<OutboxEvent>(); public DbSet<AuditEvent> Audit => Set<AuditEvent>();
    public DbSet<ScanInput> ScanInputs => Set<ScanInput>(); public DbSet<JudgeRegistration> JudgeRegistrations => Set<JudgeRegistration>();
    public DbSet<Invitation> Invitations => Set<Invitation>(); public DbSet<PlatformAuditEvent> PlatformAudit => Set<PlatformAuditEvent>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Team>().Property(x => x.PolicyVersion).IsConcurrencyToken();
        b.Entity<Team>().Property(x => x.TenantId).HasMaxLength(64);
        b.Entity<Team>().Property(x => x.Name).HasMaxLength(200);
        b.Entity<Team>().HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
        // At most one platform administration team per tenant.
        b.Entity<Team>().HasIndex(x => x.TenantId).IsUnique().HasFilter("IsPlatformAdmin = 1").HasDatabaseName("IX_Teams_TenantId_PlatformAdmin");
        b.Entity<Membership>().HasKey(x => new { x.TeamId, x.Subject });
        b.Entity<Membership>().Property(x => x.Subject).HasMaxLength(128);
        b.Entity<Membership>().Property(x => x.Email).HasMaxLength(254);
        b.Entity<Membership>().Property(x => x.DisplayName).HasMaxLength(200);
        b.Entity<Invitation>().ToTable("Invitations");
        b.Entity<Invitation>().Property(x => x.TenantId).HasMaxLength(64);
        b.Entity<Invitation>().Property(x => x.Email).HasMaxLength(254);
        b.Entity<Invitation>().Property(x => x.InvitedSubject).HasMaxLength(128);
        b.Entity<Invitation>().Property(x => x.Delivery).HasMaxLength(32);
        b.Entity<Invitation>().Property(x => x.Status).HasMaxLength(16);
        b.Entity<Invitation>().Property(x => x.InvitedBy).HasMaxLength(128);
        b.Entity<Invitation>().Property(x => x.RedeemUrl).HasMaxLength(2048);
        b.Entity<Invitation>().HasIndex(x => new { x.TenantId, x.InvitedSubject, x.Status });
        b.Entity<Invitation>().HasIndex(x => new { x.TenantId, x.Email });
        b.Entity<PlatformAuditEvent>().ToTable("PlatformAudit");
        b.Entity<PlatformAuditEvent>().Property(x => x.TenantId).HasMaxLength(64);
        b.Entity<PlatformAuditEvent>().Property(x => x.Actor).HasMaxLength(128);
        b.Entity<PlatformAuditEvent>().Property(x => x.Action).HasMaxLength(64);
        b.Entity<PlatformAuditEvent>().Property(x => x.Resource).HasMaxLength(256);
        b.Entity<PlatformAuditEvent>().Property(x => x.Detail).HasMaxLength(PlatformAuditEvent.DetailLimit);
        b.Entity<PlatformAuditEvent>().HasIndex(x => x.TenantId);
        b.Entity<Membership>().HasOne<Team>().WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Project>().HasAlternateKey(x => new { x.TeamId, x.Id });
        b.Entity<Project>().HasOne<Team>().WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Submission>().HasAlternateKey(x => new { x.TeamId, x.Id });
        b.Entity<Submission>().HasOne<Project>().WithMany().HasForeignKey(x => new { x.TeamId, x.ProjectId }).HasPrincipalKey(x => new { x.TeamId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<DocumentVersion>().HasAlternateKey(x => new { x.TeamId, x.Id });
        b.Entity<DocumentVersion>().Property(x => x.RelativePath).HasMaxLength(260);
        b.Entity<DocumentVersion>().HasIndex(x => new { x.TeamId, x.BundleId });
        b.Entity<JudgeRegistration>().Property(x => x.Id).ValueGeneratedNever();
        b.Entity<JudgeRegistration>().Property(x => x.Version).HasMaxLength(512);
        b.Entity<DocumentVersion>().HasOne<Submission>().WithMany().HasForeignKey(x => new { x.TeamId, x.SubmissionId }).HasPrincipalKey(x => new { x.TeamId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<PolicyVersion>().HasIndex(x => new { x.TeamId, x.Version }).IsUnique();
        b.Entity<PolicyVersion>().HasOne<Team>().WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Scan>().HasAlternateKey(x => new { x.TeamId, x.Id });
        b.Entity<Scan>().Property(x => x.IdempotencyKey).HasMaxLength(128);
        b.Entity<Scan>().HasIndex(x => new { x.TeamId, x.IdempotencyKey }).IsUnique();
        b.Entity<Scan>().HasOne<Submission>().WithMany().HasForeignKey(x => new { x.TeamId, x.SubmissionId }).HasPrincipalKey(x => new { x.TeamId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<OutboxEvent>().HasIndex(x => x.ScanId).IsUnique();
        b.Entity<OutboxEvent>().HasOne<Scan>().WithMany().HasForeignKey(x => new { x.TeamId, x.ScanId }).HasPrincipalKey(x => new { x.TeamId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<OutboxEvent>().Property(x => x.LeaseOwner).HasMaxLength(64);
        b.Entity<ScanInput>().HasKey(x => new { x.TeamId, x.ScanId, x.VersionId });
        b.Entity<ScanInput>().HasOne<Scan>().WithMany().HasForeignKey(x => new { x.TeamId, x.ScanId }).HasPrincipalKey(x => new { x.TeamId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ScanInput>().HasOne<DocumentVersion>().WithMany().HasForeignKey(x => new { x.TeamId, x.VersionId }).HasPrincipalKey(x => new { x.TeamId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<AuditEvent>().HasOne<Team>().WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Restrict);
    }
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    { GuardSnapshots(); return base.SaveChanges(acceptAllChangesOnSuccess); }
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    { GuardSnapshots(); return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken); }
    private void GuardSnapshots()
    {
        if (ChangeTracker.Entries().Any(e => e.State == EntityState.Deleted && e.Entity is PolicyVersion or DocumentVersion or Scan or ScanInput or AuditEvent or PlatformAuditEvent))
            throw new InvalidOperationException("Historical records require an approved retention/deletion process.");
        foreach (var e in ChangeTracker.Entries().Where(e => e.State == EntityState.Modified))
        {
            if (e.Entity is PolicyVersion or ScanInput or AuditEvent or PlatformAuditEvent) throw new InvalidOperationException("Policy, audit and input snapshots are immutable.");
            var names = e.Entity is Scan ? new[] { "Inputs", "Policy", "Judge", "TeamId", "SubmissionId", "Requester", "IdempotencyKey", "RequestHash", "CreatedAt" } :
                e.Entity is DocumentVersion ? new[] { "TeamId", "SubmissionId", "Role", "BlobKey", "Hash", "FileName", "Size", "CreatedAt", "RelativePath", "BundleId" } :
                e.Entity is Team ? new[] { "TenantId", "IsPlatformAdmin", "CreatedAt" } : [];
            if (names.Any(n => e.Property(n).IsModified)) throw new InvalidOperationException("Snapshot fields are immutable.");
        }
    }
}

public static class Registration
{
    public static IServiceCollection AddEvaluation(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<EvaluationDb>(options =>
        {
            var connection = config.GetConnectionString("Evaluation") ?? "Data Source=evaluation.db";
            if (config["Database:Provider"] == "SqlServer") options.UseSqlServer(connection);
            else options.UseSqlite(connection);
        });
        services.AddSingleton<PrivateFiles>();
        return services;
    }

    // Worker only: the API never calls the judge. Without Judge:SidecarUrl the adapter fails closed.
    // Judge:Mode=FoundryAgent sends evaluations to the Foundry hosted agent; Judge:SidecarUrl then points at the
    // internal judge Container App (same image and configuration) that supplies the contract heartbeat.
    public static IServiceCollection AddJudge(this IServiceCollection services, IConfiguration config)
    {
        var url = config["Judge:SidecarUrl"];
        if (string.IsNullOrWhiteSpace(url)) services.AddSingleton<IJudgeAdapter, UnavailableJudgeAdapter>();
        else
        {
            var uri = SidecarJudgeAdapter.ValidateUrl(url);
            var timeout = SidecarJudgeAdapter.ValidateTimeout(config["Judge:RequestTimeoutMinutes"]);
            var mode = config["Judge:Mode"] ?? "Sidecar";
            if (string.Equals(mode, "FoundryAgent", StringComparison.OrdinalIgnoreCase))
            {
                var project = FoundryAgentJudgeAdapter.ValidateProjectEndpoint(config["Judge:FoundryProjectEndpoint"]);
                var agent = FoundryAgentJudgeAdapter.ValidateAgentName(config["Judge:FoundryAgentName"]);
                var session = FoundryAgentJudgeAdapter.ValidateSessionId(config["Judge:FoundrySessionId"]);
                services.AddSingleton<IJudgeAdapter>(_ => FoundryAgentJudgeAdapter.Create(SidecarJudgeAdapter.Create(uri, timeout),
                    project, agent, session, new Azure.Identity.DefaultAzureCredential(), timeout));
            }
            else if (string.Equals(mode, "Sidecar", StringComparison.OrdinalIgnoreCase))
                services.AddSingleton<IJudgeAdapter>(_ => SidecarJudgeAdapter.Create(uri, timeout));
            else throw new ArgumentException("Judge:Mode must be Sidecar or FoundryAgent.");
        }
        return services;
    }
}
