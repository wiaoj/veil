using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Veil.EdgeNodes.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UseHexStringForTokenHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "TokenHash",
                schema: "edge_nodes",
                table: "edge_nodes",
                type: "character(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "TokenHash",
                schema: "edge_nodes",
                table: "edge_nodes",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character(64)",
                oldFixedLength: true,
                oldMaxLength: 64);
        }
    }
}
