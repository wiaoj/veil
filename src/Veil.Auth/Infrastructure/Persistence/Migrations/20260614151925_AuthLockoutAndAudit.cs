using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Veil.Auth.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuthLockoutAndAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FailedLoginAttempts",
                schema: "auth",
                table: "users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LockedUntilUtc",
                schema: "auth",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "audit_events",
                schema: "auth",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TimestampUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Actor = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    ActorIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Target = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Detail = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_Action",
                schema: "auth",
                table: "audit_events",
                column: "Action");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_TimestampUtc",
                schema: "auth",
                table: "audit_events",
                column: "TimestampUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_events",
                schema: "auth");

            migrationBuilder.DropColumn(
                name: "FailedLoginAttempts",
                schema: "auth",
                table: "users");

            migrationBuilder.DropColumn(
                name: "LockedUntilUtc",
                schema: "auth",
                table: "users");
        }
    }
}
