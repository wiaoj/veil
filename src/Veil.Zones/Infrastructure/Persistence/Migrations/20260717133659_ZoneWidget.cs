using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Veil.Zones.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ZoneWidget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "widget",
                schema: "zones",
                table: "zones",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{\"Enabled\":false,\"SiteKey\":\"\",\"Secret\":\"\",\"Theme\":\"auto\"}'::jsonb");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "widget",
                schema: "zones",
                table: "zones");
        }
    }
}
