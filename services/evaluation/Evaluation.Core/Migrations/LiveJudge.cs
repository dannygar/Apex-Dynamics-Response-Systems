using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Evaluation.Core.Migrations;

// Live agent-eval judge: worker registration heartbeat, DT bundles and judge-contract scan snapshots.
// Existing rows are back-filled deterministically; no data is seeded.
[DbContext(typeof(EvaluationDb))]
[Migration("20261021000000_LiveJudge")]
public sealed class LiveJudge : Migration
{
    protected override void Up(MigrationBuilder m)
    {
        m.CreateTable("JudgeRegistrations", c => new
        {
            Id = c.Column<int>(nullable: false), Version = c.Column<string>(maxLength: 512, nullable: false),
            Contract = c.Column<string>(nullable: false), UpdatedAt = c.Column<DateTimeOffset>(nullable: false)
        }, constraints: t => t.PrimaryKey("PK_JudgeRegistrations", x => x.Id));
        m.AddColumn<string>("RelativePath", "Documents", maxLength: 260, nullable: false, defaultValue: "");
        m.AddColumn<Guid>("BundleId", "Documents", nullable: false, defaultValue: Guid.Empty);
        // Every pre-existing upload is a single-file version: its bundle is itself and its path is its file name.
        m.Sql("UPDATE Documents SET RelativePath = FileName, BundleId = Id;");
        m.CreateIndex("IX_Documents_TeamId_BundleId", "Documents", new[] { "TeamId", "BundleId" });
        m.RenameColumn("Rubric", "Scans", "Judge");
    }
    protected override void Down(MigrationBuilder m)
    {
        m.RenameColumn("Judge", "Scans", "Rubric");
        m.DropIndex("IX_Documents_TeamId_BundleId", "Documents");
        m.DropColumn("BundleId", "Documents");
        m.DropColumn("RelativePath", "Documents");
        m.DropTable("JudgeRegistrations");
    }
}
