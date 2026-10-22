using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Evaluation.Core.Migrations;

// Platform administration: operator-flagged admin team, team creation time, unique team names per tenant,
// member display claims, Entra B2B invitations and tenant-level platform audit. No data is seeded; the admin
// team is flagged by operator SQL after this migration.
[DbContext(typeof(EvaluationDb))]
[Migration("20261022000000_PlatformTeams")]
public sealed class PlatformTeams : Migration
{
    private static readonly DateTimeOffset ExistingTeamsCreatedAt = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
    private bool SqlServer => ActiveProvider == "Microsoft.EntityFrameworkCore.SqlServer";
    protected override void Up(MigrationBuilder m)
    {
        // SQL Server cannot index nvarchar(max). SQLite stores TEXT without length enforcement, so no table rebuild there.
        if (SqlServer)
        {
            m.AlterColumn<string>("TenantId", "Teams", maxLength: 64, nullable: false, oldClrType: typeof(string), oldNullable: false);
            m.AlterColumn<string>("Name", "Teams", maxLength: 200, nullable: false, oldClrType: typeof(string), oldNullable: false);
        }
        m.AddColumn<bool>("IsPlatformAdmin", "Teams", nullable: false, defaultValue: false);
        m.AddColumn<DateTimeOffset>("CreatedAt", "Teams", nullable: false, defaultValue: ExistingTeamsCreatedAt);
        m.CreateIndex("IX_Teams_TenantId_Name", "Teams", new[] { "TenantId", "Name" }, unique: true);
        m.CreateIndex("IX_Teams_TenantId_PlatformAdmin", "Teams", "TenantId", unique: true, filter: "IsPlatformAdmin = 1");
        m.AddColumn<string>("Email", "Memberships", maxLength: 254, nullable: true);
        m.AddColumn<string>("DisplayName", "Memberships", maxLength: 200, nullable: true);
        m.CreateTable("Invitations", c => new
        {
            Id = c.Column<Guid>(nullable: false), TenantId = c.Column<string>(maxLength: 64, nullable: false),
            Email = c.Column<string>(maxLength: 254, nullable: false), SuggestedTeamId = c.Column<Guid>(nullable: true),
            InvitedSubject = c.Column<string>(maxLength: 128, nullable: false), Delivery = c.Column<string>(maxLength: 32, nullable: false),
            Status = c.Column<string>(maxLength: 16, nullable: false), InvitedBy = c.Column<string>(maxLength: 128, nullable: false),
            CreatedAt = c.Column<DateTimeOffset>(nullable: false), ExpiresAt = c.Column<DateTimeOffset>(nullable: false),
            AcceptedAt = c.Column<DateTimeOffset>(nullable: true), AcceptedTeamId = c.Column<Guid>(nullable: true),
            RedeemUrl = c.Column<string>(maxLength: 2048, nullable: true)
        }, constraints: t => t.PrimaryKey("PK_Invitations", x => x.Id));
        m.CreateIndex("IX_Invitations_TenantId_InvitedSubject_Status", "Invitations", new[] { "TenantId", "InvitedSubject", "Status" });
        m.CreateIndex("IX_Invitations_TenantId_Email", "Invitations", new[] { "TenantId", "Email" });
        m.CreateTable("PlatformAudit", c => new
        {
            Id = c.Column<Guid>(nullable: false), TenantId = c.Column<string>(maxLength: 64, nullable: false),
            Actor = c.Column<string>(maxLength: 128, nullable: false), Action = c.Column<string>(maxLength: 64, nullable: false),
            Resource = c.Column<string>(maxLength: 256, nullable: false), Detail = c.Column<string>(maxLength: 2000, nullable: false),
            CreatedAt = c.Column<DateTimeOffset>(nullable: false)
        }, constraints: t => t.PrimaryKey("PK_PlatformAudit", x => x.Id));
        m.CreateIndex("IX_PlatformAudit_TenantId", "PlatformAudit", "TenantId");
    }
    protected override void Down(MigrationBuilder m)
    {
        m.DropTable("PlatformAudit");
        m.DropTable("Invitations");
        m.DropColumn("DisplayName", "Memberships");
        m.DropColumn("Email", "Memberships");
        m.DropIndex("IX_Teams_TenantId_PlatformAdmin", "Teams");
        m.DropIndex("IX_Teams_TenantId_Name", "Teams");
        m.DropColumn("CreatedAt", "Teams");
        m.DropColumn("IsPlatformAdmin", "Teams");
        if (SqlServer)
        {
            m.AlterColumn<string>("Name", "Teams", nullable: false, oldClrType: typeof(string), oldMaxLength: 200, oldNullable: false);
            m.AlterColumn<string>("TenantId", "Teams", nullable: false, oldClrType: typeof(string), oldMaxLength: 64, oldNullable: false);
        }
    }
}
