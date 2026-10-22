using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Evaluation.Core.Migrations;

[DbContext(typeof(EvaluationDb))]
[Migration("202610200001_InitialSchema")]
public sealed class InitialSchema : Migration
{
    protected override void Up(MigrationBuilder m)
    {
        m.CreateTable("Teams", c => new
        {
            Id = c.Column<Guid>(nullable: false), TenantId = c.Column<string>(nullable: false),
            Name = c.Column<string>(nullable: false), PolicyVersion = c.Column<int>(nullable: false)
        }, constraints: t => t.PrimaryKey("PK_Teams", x => x.Id));
        m.CreateTable("Memberships", c => new
        {
            TeamId = c.Column<Guid>(nullable: false), Subject = c.Column<string>(maxLength: 128, nullable: false),
            Role = c.Column<int>(nullable: false)
        }, constraints: t => { t.PrimaryKey("PK_Memberships", x => new { x.TeamId, x.Subject }); t.ForeignKey("FK_Memberships_Teams", x => x.TeamId, "Teams", "Id", onDelete: ReferentialAction.Restrict); });
        m.CreateTable("Projects", c => new
        {
            Id = c.Column<Guid>(nullable: false), TeamId = c.Column<Guid>(nullable: false), Name = c.Column<string>(nullable: false)
        }, constraints: t => { t.PrimaryKey("PK_Projects", x => x.Id); t.UniqueConstraint("AK_Projects_TeamId_Id", x => new { x.TeamId, x.Id }); t.ForeignKey("FK_Projects_Teams", x => x.TeamId, "Teams", "Id", onDelete: ReferentialAction.Restrict); });
        m.CreateTable("Policies", c => new
        {
            Id = c.Column<Guid>(nullable: false), TeamId = c.Column<Guid>(nullable: false), Version = c.Column<int>(nullable: false),
            Snapshot = c.Column<string>(nullable: false), Actor = c.Column<string>(nullable: false), CreatedAt = c.Column<DateTimeOffset>(nullable: false)
        }, constraints: t => { t.PrimaryKey("PK_Policies", x => x.Id); t.ForeignKey("FK_Policies_Teams", x => x.TeamId, "Teams", "Id", onDelete: ReferentialAction.Restrict); });
        m.CreateIndex("IX_Policies_TeamId_Version", "Policies", new[] { "TeamId", "Version" }, unique: true);
        m.CreateTable("Audit", c => new
        {
            Id = c.Column<Guid>(nullable: false), TeamId = c.Column<Guid>(nullable: false), Actor = c.Column<string>(nullable: false),
            Action = c.Column<string>(nullable: false), Resource = c.Column<string>(nullable: false), CreatedAt = c.Column<DateTimeOffset>(nullable: false)
        }, constraints: t => { t.PrimaryKey("PK_Audit", x => x.Id); t.ForeignKey("FK_Audit_Teams", x => x.TeamId, "Teams", "Id", onDelete: ReferentialAction.Restrict); });
        m.CreateIndex("IX_Audit_TeamId", "Audit", "TeamId");
        m.CreateTable("Submissions", c => new
        {
            Id = c.Column<Guid>(nullable: false), TeamId = c.Column<Guid>(nullable: false), ProjectId = c.Column<Guid>(nullable: false),
            Name = c.Column<string>(nullable: false), Description = c.Column<string>(nullable: false)
        }, constraints: t =>
        {
            t.PrimaryKey("PK_Submissions", x => x.Id); t.UniqueConstraint("AK_Submissions_TeamId_Id", x => new { x.TeamId, x.Id });
            t.ForeignKey("FK_Submissions_Projects", x => new { x.TeamId, x.ProjectId }, "Projects", new[] { "TeamId", "Id" }, onDelete: ReferentialAction.Restrict);
        });
        m.CreateIndex("IX_Submissions_TeamId_ProjectId", "Submissions", new[] { "TeamId", "ProjectId" });
        m.CreateTable("Documents", c => new
        {
            Id = c.Column<Guid>(nullable: false), TeamId = c.Column<Guid>(nullable: false), SubmissionId = c.Column<Guid>(nullable: false),
            Role = c.Column<int>(nullable: false), FileName = c.Column<string>(nullable: false), BlobKey = c.Column<string>(nullable: false),
            Hash = c.Column<string>(nullable: false), Size = c.Column<long>(nullable: false), Status = c.Column<string>(nullable: false),
            Error = c.Column<string>(nullable: true), TextKey = c.Column<string>(nullable: true), TextLength = c.Column<int>(nullable: false),
            LeaseUntil = c.Column<long>(nullable: false), LeaseOwner = c.Column<string>(nullable: true), CreatedAt = c.Column<DateTimeOffset>(nullable: false)
        }, constraints: t =>
        {
            t.PrimaryKey("PK_Documents", x => x.Id); t.UniqueConstraint("AK_Documents_TeamId_Id", x => new { x.TeamId, x.Id });
            t.ForeignKey("FK_Documents_Submissions", x => new { x.TeamId, x.SubmissionId }, "Submissions", new[] { "TeamId", "Id" }, onDelete: ReferentialAction.Restrict);
        });
        m.CreateIndex("IX_Documents_TeamId_SubmissionId", "Documents", new[] { "TeamId", "SubmissionId" });
        m.CreateTable("Scans", c => new
        {
            Id = c.Column<Guid>(nullable: false), TeamId = c.Column<Guid>(nullable: false), SubmissionId = c.Column<Guid>(nullable: false),
            Requester = c.Column<string>(nullable: false), IdempotencyKey = c.Column<string>(maxLength: 128, nullable: false),
            RequestHash = c.Column<string>(nullable: false), Inputs = c.Column<string>(nullable: false), Policy = c.Column<string>(nullable: false),
            Rubric = c.Column<string>(nullable: false), Status = c.Column<string>(nullable: false), Outcome = c.Column<string>(nullable: false),
            Result = c.Column<string>(nullable: true), Assessments = c.Column<string>(nullable: true), Error = c.Column<string>(nullable: true),
            RemoteInvocationId = c.Column<string>(nullable: true), CreatedAt = c.Column<DateTimeOffset>(nullable: false)
        }, constraints: t =>
        {
            t.PrimaryKey("PK_Scans", x => x.Id); t.UniqueConstraint("AK_Scans_TeamId_Id", x => new { x.TeamId, x.Id });
            t.ForeignKey("FK_Scans_Submissions", x => new { x.TeamId, x.SubmissionId }, "Submissions", new[] { "TeamId", "Id" }, onDelete: ReferentialAction.Restrict);
        });
        m.CreateIndex("IX_Scans_TeamId_SubmissionId", "Scans", new[] { "TeamId", "SubmissionId" });
        m.CreateIndex("IX_Scans_TeamId_IdempotencyKey", "Scans", new[] { "TeamId", "IdempotencyKey" }, unique: true);
        m.CreateTable("ScanInputs", c => new
        {
            TeamId = c.Column<Guid>(nullable: false), ScanId = c.Column<Guid>(nullable: false), VersionId = c.Column<Guid>(nullable: false)
        }, constraints: t =>
        {
            t.PrimaryKey("PK_ScanInputs", x => new { x.TeamId, x.ScanId, x.VersionId });
            t.ForeignKey("FK_ScanInputs_Scans", x => new { x.TeamId, x.ScanId }, "Scans", new[] { "TeamId", "Id" }, onDelete: ReferentialAction.Restrict);
            t.ForeignKey("FK_ScanInputs_Documents", x => new { x.TeamId, x.VersionId }, "Documents", new[] { "TeamId", "Id" }, onDelete: ReferentialAction.Restrict);
        });
        m.CreateIndex("IX_ScanInputs_TeamId_VersionId", "ScanInputs", new[] { "TeamId", "VersionId" });
        m.CreateTable("Outbox", c => new
        {
            Id = c.Column<Guid>(nullable: false), TeamId = c.Column<Guid>(nullable: false), ScanId = c.Column<Guid>(nullable: false),
            State = c.Column<string>(nullable: false), Attempts = c.Column<int>(nullable: false), AvailableAt = c.Column<long>(nullable: false),
            LeaseUntil = c.Column<long>(nullable: false), LeaseOwner = c.Column<string>(maxLength: 64, nullable: true)
        }, constraints: t =>
        {
            t.PrimaryKey("PK_Outbox", x => x.Id);
            t.ForeignKey("FK_Outbox_Scans", x => new { x.TeamId, x.ScanId }, "Scans", new[] { "TeamId", "Id" }, onDelete: ReferentialAction.Restrict);
        });
        m.CreateIndex("IX_Outbox_ScanId", "Outbox", "ScanId", unique: true);
        m.CreateIndex("IX_Outbox_TeamId_ScanId", "Outbox", new[] { "TeamId", "ScanId" });
    }
    protected override void Down(MigrationBuilder m)
    {
        foreach (var table in new[] { "Outbox", "ScanInputs", "Scans", "Documents", "Submissions", "Projects", "Policies", "Audit", "Memberships", "Teams" })
            m.DropTable(table);
    }
}
