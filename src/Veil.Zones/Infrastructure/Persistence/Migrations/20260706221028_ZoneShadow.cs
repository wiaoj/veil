using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Veil.Zones.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ZoneShadow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "shadow",
                schema: "zones",
                table: "zones",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "shadow",
                schema: "zones",
                table: "zones");
        }
    }
}
