using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Veil.Zones.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialZones : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "zones");

            migrationBuilder.CreateTable(
                name: "zones",
                schema: "zones",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    upstream = table.Column<string>(type: "jsonb", nullable: false),
                    challenge = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    hostname = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_zones", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "rules",
                schema: "zones",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    Action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Conditions = table.Column<string>(type: "jsonb", nullable: false),
                    rate_limit = table.Column<string>(type: "jsonb", nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ZoneId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_rules_zones_ZoneId",
                        column: x => x.ZoneId,
                        principalSchema: "zones",
                        principalTable: "zones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_rules_ZoneId",
                schema: "zones",
                table: "rules",
                column: "ZoneId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rules",
                schema: "zones");

            migrationBuilder.DropTable(
                name: "zones",
                schema: "zones");
        }
    }
}
