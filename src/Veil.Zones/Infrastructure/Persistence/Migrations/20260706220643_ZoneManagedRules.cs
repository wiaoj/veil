using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Veil.Zones.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ZoneManagedRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "managed_rules",
                schema: "zones",
                table: "zones",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{\"SqlInjection\":false,\"Xss\":false,\"PathTraversal\":false,\"InspectBody\":false,\"Action\":0}'::jsonb");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "managed_rules",
                schema: "zones",
                table: "zones");
        }
    }
}
