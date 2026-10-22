using Evaluation.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Evaluation.Tests;

public class PersistenceTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly EvaluationDb db;
    private readonly Guid team = Guid.NewGuid(), submission = Guid.NewGuid();
    public PersistenceTests()
    {
        connection.Open();
        db = new EvaluationDb(new DbContextOptionsBuilder<EvaluationDb>().UseSqlite(connection).Options);
        db.Database.Migrate();
        var project = Guid.NewGuid();
        db.Teams.Add(new Team { Id = team, TenantId = Guid.NewGuid().ToString(), Name = "Test fixture" });
        db.Projects.Add(new Project { Id = project, TeamId = team });
        db.Submissions.Add(new Submission { Id = submission, TeamId = team, ProjectId = project });
        db.SaveChanges();
    }
    private Scan AddScan()
    {
        var scan = new Scan { Id = Guid.NewGuid(), TeamId = team, SubmissionId = submission,
            IdempotencyKey = Guid.NewGuid().ToString(), Inputs = "immutable", Policy = "policy-1", Judge = "judge-1" };
        db.Scans.Add(scan);
        db.Outbox.Add(new OutboxEvent { Id = Guid.NewGuid(), TeamId = team, ScanId = scan.Id });
        db.SaveChanges();
        return scan;
    }
    [Fact] public void ScanSnapshotsImmutable()
    {
        var scan = AddScan(); scan.Policy = "changed";
        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
    }
    [Fact] public void PolicyVersionsImmutable()
    {
        var policy = new PolicyVersion { Id = Guid.NewGuid(), TeamId = team, Version = 1, Snapshot = "original" };
        db.Policies.Add(policy); db.SaveChanges(); policy.Snapshot = "changed";
        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
    }
    [Fact] public void DocumentsImmutable()
    {
        var doc = new DocumentVersion { Id = Guid.NewGuid(), TeamId = team, SubmissionId = submission, Hash = "original" };
        db.Documents.Add(doc); db.SaveChanges(); doc.Hash = "changed";
        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
    }
    [Fact] public void OwnershipForeignKeysRejectCrossTeamDocument()
    {
        db.Teams.Add(new Team { Id = Guid.NewGuid(), Name = "Other" }); db.SaveChanges();
        db.Documents.Add(new DocumentVersion { Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), SubmissionId = submission });
        Assert.Throws<DbUpdateException>(() => db.SaveChanges());
    }
    [Fact] public void IdempotencyUniquePerTeam()
    {
        var first = AddScan();
        db.Scans.Add(new Scan { Id = Guid.NewGuid(), TeamId = team, SubmissionId = submission, IdempotencyKey = first.IdempotencyKey });
        Assert.Throws<DbUpdateException>(() => db.SaveChanges());
    }
    [Fact] public async Task DuplicateDeliveryAndStaleOwnerCannotCommit()
    {
        AddScan(); var id = db.Outbox.Single().Id;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.True(await JobLease.TryClaim(db, id, "first", now));
        Assert.False(await JobLease.TryClaim(db, id, "duplicate", now));
        Assert.False(await JobLease.Finish(db, id, "forged", "Dispatched"));
        await db.Outbox.ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseUntil, now - 1));
        Assert.True(await JobLease.TryClaim(db, id, "recovery", now));
        Assert.False(await JobLease.Finish(db, id, "first", "Dispatched"));
        Assert.True(await JobLease.Finish(db, id, "recovery", "Dispatched"));
        Assert.False(await JobLease.TryClaim(db, id, "again", now));
    }
    [Fact] public async Task DeferredRetryIsNotClaimedEarly()
    {
        AddScan(); var id = db.Outbox.Single().Id; var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.Outbox.ExecuteUpdateAsync(s => s.SetProperty(x => x.AvailableAt, now + 60));
        Assert.False(await JobLease.TryClaim(db, id, "early", now));
    }
    [Fact] public async Task ScanAndOutboxRollbackTogether()
    {
        await using (var transaction = await db.Database.BeginTransactionAsync())
        { AddScan(); await transaction.RollbackAsync(); }
        db.ChangeTracker.Clear();
        Assert.Empty(db.Scans); Assert.Empty(db.Outbox);
    }
    [Fact] public void SqlServerMigrationCanBeGeneratedWithoutAConnection()
    {
        using var sql = new EvaluationDb(new DbContextOptionsBuilder<EvaluationDb>()
            .UseSqlServer("Server=not-contacted;Database=fixture;Integrated Security=true;TrustServerCertificate=false").Options);
        var script = sql.GetService<IMigrator>().GenerateScript();
        Assert.Contains("CREATE TABLE [Teams]", script);
        Assert.Contains("FK_ScanInputs_Documents", script);
        Assert.Contains("nvarchar(128)", script);
    }
    [Fact] public void SqlServerScriptIncludesLiveJudgeMigration()
    {
        using var sql = new EvaluationDb(new DbContextOptionsBuilder<EvaluationDb>()
            .UseSqlServer("Server=not-contacted;Database=fixture;Integrated Security=true;TrustServerCertificate=false").Options);
        var script = sql.GetService<IMigrator>().GenerateScript();
        Assert.Contains("CREATE TABLE [JudgeRegistrations]", script);
        Assert.Contains("[RelativePath] nvarchar(260)", script);
        Assert.Contains("EXEC sp_rename N'[Scans].[Rubric]', N'Judge', N'COLUMN';", script);
        Assert.Contains("UPDATE Documents SET RelativePath = FileName, BundleId = Id;", script);
        Assert.DoesNotContain("INSERT INTO [Teams]", script);
    }
    [Fact] public void SqlServerScriptIncludesPlatformTeamsMigration()
    {
        using var sql = new EvaluationDb(new DbContextOptionsBuilder<EvaluationDb>()
            .UseSqlServer("Server=not-contacted;Database=fixture;Integrated Security=true;TrustServerCertificate=false").Options);
        var script = sql.GetService<IMigrator>().GenerateScript();
        // Operator bootstrap SQL depends on these exact column names and types.
        Assert.Contains("ALTER TABLE [Teams] ADD [IsPlatformAdmin] bit NOT NULL DEFAULT CAST(0 AS bit);", script);
        Assert.Contains("ALTER TABLE [Teams] ADD [CreatedAt] datetimeoffset NOT NULL DEFAULT", script);
        Assert.Contains("ALTER TABLE [Memberships] ADD [Email] nvarchar(254) NULL;", script);
        Assert.Contains("ALTER TABLE [Memberships] ADD [DisplayName] nvarchar(200) NULL;", script);
        Assert.Contains("CREATE UNIQUE INDEX [IX_Teams_TenantId_Name] ON [Teams] ([TenantId], [Name])", script);
        Assert.Contains("CREATE UNIQUE INDEX [IX_Teams_TenantId_PlatformAdmin] ON [Teams] ([TenantId]) WHERE IsPlatformAdmin = 1;", script);
        Assert.Contains("CREATE TABLE [Invitations]", script);
        Assert.Contains("[SuggestedTeamId] uniqueidentifier NULL", script);
        Assert.Contains("[RedeemUrl] nvarchar(2048) NULL", script);
        Assert.Contains("CREATE INDEX [IX_Invitations_TenantId_InvitedSubject_Status] ON [Invitations] ([TenantId], [InvitedSubject], [Status]);", script);
        Assert.Contains("CREATE INDEX [IX_Invitations_TenantId_Email] ON [Invitations] ([TenantId], [Email]);", script);
        Assert.Contains("CREATE TABLE [PlatformAudit]", script);
        Assert.Contains("[Detail] nvarchar(2000) NOT NULL", script);
        var platform = script[script.IndexOf("20261022000000_PlatformTeams", StringComparison.Ordinal)..];
        Assert.DoesNotContain("FOREIGN KEY", platform);
        Assert.DoesNotContain("INSERT INTO [Teams]", script);
    }
    [Fact] public void PlatformTeamsMigrationKeepsOperatorSqlWorking()
    {
        using var legacy = new SqliteConnection("Data Source=:memory:");
        legacy.Open();
        using var old = new EvaluationDb(new DbContextOptionsBuilder<EvaluationDb>().UseSqlite(legacy).Options);
        old.GetService<IMigrator>().Migrate("20261021000000_LiveJudge");
        var tenant = Guid.NewGuid().ToString(); var existing = Guid.NewGuid(); var created = Guid.NewGuid(); var admin = Guid.NewGuid().ToString();
        old.Database.ExecuteSql($"INSERT INTO Teams (Id, TenantId, Name, PolicyVersion) VALUES ({existing}, {tenant}, 'Apex Workshop', 0)");
        old.Database.Migrate();
        var migrated = old.Teams.AsNoTracking().Single();
        Assert.False(migrated.IsPlatformAdmin);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero), migrated.CreatedAt);
        // The deployment's operator statements, verbatim in shape.
        old.Database.ExecuteSql($"UPDATE Teams SET Name={"Platform administrators"}, IsPlatformAdmin=1 WHERE Id={existing}");
        old.Database.ExecuteSql($"INSERT INTO Memberships (TeamId, Subject, Role) VALUES ({existing}, {admin}, 0)");
        old.Database.ExecuteSql($"INSERT INTO Audit (Id, TeamId, Actor, Action, Resource, CreatedAt) VALUES ({Guid.NewGuid()}, {existing}, 'operator-bootstrap', 'MembershipBootstrap', {admin}, {DateTimeOffset.UtcNow})");
        old.Database.ExecuteSql($"INSERT INTO Teams (Id, TenantId, Name, PolicyVersion, IsPlatformAdmin, CreatedAt) VALUES ({created}, {Guid.NewGuid().ToString()}, 'Admins', 0, 1, {DateTimeOffset.UtcNow})");
        var membership = old.Memberships.AsNoTracking().Single();
        Assert.Null(membership.Email); Assert.Null(membership.DisplayName);
        Assert.True(old.Teams.AsNoTracking().Single(t => t.Id == existing).IsPlatformAdmin);
        // At most one platform administration team per tenant.
        Assert.Throws<SqliteException>(() => old.Database.ExecuteSql(
            $"INSERT INTO Teams (Id, TenantId, Name, PolicyVersion, IsPlatformAdmin, CreatedAt) VALUES ({Guid.NewGuid()}, {tenant}, 'Second', 0, 1, {DateTimeOffset.UtcNow})"));
        Assert.Throws<SqliteException>(() => old.Database.ExecuteSql(
            $"INSERT INTO Teams (Id, TenantId, Name, PolicyVersion, IsPlatformAdmin, CreatedAt) VALUES ({Guid.NewGuid()}, {tenant}, 'Platform administrators', 0, 0, {DateTimeOffset.UtcNow})"));
    }
    [Fact] public void HistoricalRowsStillRequireApprovedDeletion()
    {
        var scan = AddScan();
        var document = new DocumentVersion { Id = Guid.NewGuid(), TeamId = team, SubmissionId = submission, Hash = "h" };
        var audit = new AuditEvent { Id = Guid.NewGuid(), TeamId = team, Actor = "a", Action = "x", Resource = "r" };
        var platform = new PlatformAuditEvent { Id = Guid.NewGuid(), TenantId = "t", Actor = "a", Action = "x", Resource = "r" };
        db.AddRange(document, audit, platform); db.SaveChanges();
        foreach (var entity in new object[] { scan, document, audit, platform })
        {
            db.ChangeTracker.Clear();
            db.Attach(entity);
            db.Remove(entity);
            Assert.Contains("approved retention/deletion process", Assert.Throws<InvalidOperationException>(() => db.SaveChanges()).Message);
        }
        db.ChangeTracker.Clear();
        db.Attach(platform).Entity.Detail = "changed";
        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        db.ChangeTracker.Clear();
        var teamEntity = db.Teams.Single(t => t.Id == team);
        teamEntity.IsPlatformAdmin = true;
        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        db.ChangeTracker.Clear();
        Assert.True(db.Scans.Any(x => x.Id == scan.Id) && db.Documents.Any(x => x.Id == document.Id) && db.Audit.Any() && db.PlatformAudit.Any());
    }
    [Fact] public void SqlServerManagedIdentityConnectionStringIsSupported()
    {
        const string connection = "Server=tcp:fixture.database.windows.net,1433;Database=evaluation;Encrypt=True;" +
            "Authentication=Active Directory Managed Identity;User Id=00000000-0000-0000-0000-000000000001";
        var builder = new SqlConnectionStringBuilder(connection);
        Assert.Equal(SqlAuthenticationMethod.ActiveDirectoryManagedIdentity, builder.Authentication);
        Assert.NotNull(SqlAuthenticationProvider.GetProvider(SqlAuthenticationMethod.ActiveDirectoryManagedIdentity));
        using var sql = new EvaluationDb(new DbContextOptionsBuilder<EvaluationDb>().UseSqlServer(connection).Options);
        Assert.Contains("CREATE TABLE [Scans]", sql.GetService<IMigrator>().GenerateScript()); // No connection is opened.
    }
    [Fact] public void LiveJudgeMigrationBackfillsExistingUploads()
    {
        using var legacy = new SqliteConnection("Data Source=:memory:");
        legacy.Open();
        using var old = new EvaluationDb(new DbContextOptionsBuilder<EvaluationDb>().UseSqlite(legacy).Options);
        // EF derives migration names from 14-digit ids; the initial id is shorter, so target it by its derived name.
        old.GetService<IMigrator>().Migrate(typeof(Evaluation.Core.Migrations.InitialSchema).GetCustomAttributes(typeof(MigrationAttribute), false)
            .Cast<MigrationAttribute>().Single().Id[15..]);
        var team = Guid.NewGuid(); var project = Guid.NewGuid(); var submission = Guid.NewGuid(); var document = Guid.NewGuid();
        old.Database.ExecuteSql($"INSERT INTO Teams (Id, TenantId, Name, PolicyVersion) VALUES ({team}, 't', 'n', 0)");
        old.Database.ExecuteSql($"INSERT INTO Projects (Id, TeamId, Name) VALUES ({project}, {team}, 'p')");
        old.Database.ExecuteSql($"INSERT INTO Submissions (Id, TeamId, ProjectId, Name, Description) VALUES ({submission}, {team}, {project}, 's', '')");
        old.Database.ExecuteSql($"""
            INSERT INTO Documents (Id, TeamId, SubmissionId, Role, FileName, BlobKey, Hash, Size, Status, TextLength, LeaseUntil, CreatedAt)
            VALUES ({document}, {team}, {submission}, 2, 'brd.md', 'k', 'h', 1, 'Ready', 1, 0, {DateTimeOffset.UtcNow})
            """);
        old.Database.Migrate();
        old.ChangeTracker.Clear();
        var migrated = old.Documents.AsNoTracking().Single();
        Assert.Equal("brd.md", migrated.RelativePath);
        Assert.Equal(migrated.Id, migrated.BundleId);
        Assert.Empty(old.JudgeRegistrations);
    }
    [Fact] public async Task JudgeRegistrationFreshness()
    {
        var now = DateTimeOffset.UtcNow;
        var missing = await JudgeRegistry.Current(db, now);
        Assert.False(missing.Ready); Assert.Equal([JudgeRegistry.NotRegistered], missing.Blockers);
        await JudgeRegistry.Register(db, JudgeFixtures.Contract, now.AddMinutes(-4));
        var fresh = await JudgeRegistry.Current(db, now);
        Assert.True(fresh.Ready); Assert.Empty(fresh.Blockers); Assert.Equal(JudgeFixtures.Contract.Version, fresh.Contract!.Version);
        Assert.Equal(JudgeFixtures.Contract.LayerWeights, fresh.Contract.LayerWeights);
        var stale = await JudgeRegistry.Current(db, now.AddMinutes(2));
        Assert.False(stale.Ready); Assert.Equal([JudgeRegistry.NotRegistered], stale.Blockers); Assert.NotNull(stale.Contract);
        await JudgeRegistry.Register(db, JudgeFixtures.Contract with { Version = "v2" }, now.AddMinutes(2));
        var heartbeat = await JudgeRegistry.Current(db, now.AddMinutes(2));
        Assert.True(heartbeat.Ready); Assert.Equal("v2", heartbeat.Contract!.Version);
        Assert.Single(db.JudgeRegistrations.AsNoTracking());
    }
    [Fact] public async Task FutureHeartbeatIsNotTrusted()
    {
        await JudgeRegistry.Register(db, JudgeFixtures.Contract, DateTimeOffset.UtcNow.AddHours(1));
        Assert.False((await JudgeRegistry.Current(db, DateTimeOffset.UtcNow)).Ready);
    }
    [Fact] public async Task InvalidContractIsNeverRegistered()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => JudgeRegistry.Register(db, JudgeFixtures.Contract with { Scale = new(0, 1) }, DateTimeOffset.UtcNow));
        Assert.False(await JudgeRegistry.Refresh(db, new FakeJudge { Contract = JudgeFixtures.Contract with { LayerWeights = new(0.4, 0.3, 0) } }, default));
        Assert.False(await JudgeRegistry.Refresh(db, new UnavailableJudgeAdapter(), default));
        Assert.Empty(db.JudgeRegistrations);
        Assert.True(await JudgeRegistry.Refresh(db, new FakeJudge(), default));
        Assert.True((await JudgeRegistry.Current(db, DateTimeOffset.UtcNow)).Ready);
    }
    [Fact] public async Task LeaseKeeperRenewsAndDetectsLoss()
    {
        var renewals = 0;
        await using (var healthy = LeaseKeeper.Start(_ => { Interlocked.Increment(ref renewals); return Task.FromResult(true); },
            TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(10), default))
        {
            await Task.Delay(200);
            Assert.False(healthy.Lost); Assert.False(healthy.Token.IsCancellationRequested);
        }
        Assert.True(renewals >= 2);
        await using var lost = LeaseKeeper.Start(_ => Task.FromResult(false), TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(10), default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(5000, lost.Token));
        Assert.True(lost.Lost);
        // Transient renewal errors are retried, but the lease is treated as lost before it could expire.
        await using var failing = LeaseKeeper.Start(_ => throw new TimeoutException(), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100), default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(5000, failing.Token));
        Assert.True(failing.Lost);
    }
    public void Dispose() { db.Dispose(); connection.Dispose(); }
}
