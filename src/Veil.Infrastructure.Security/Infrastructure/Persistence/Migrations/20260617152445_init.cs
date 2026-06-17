using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Veil.Infrastructure.Security.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class init : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "security");

            migrationBuilder.CreateTable(
                name: "EncryptionKeys",
                schema: "security",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContextName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    WrappedKeyMaterial = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RetiredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EncryptionKeys", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EncryptionKeys_Context_Version",
                schema: "security",
                table: "EncryptionKeys",
                columns: new[] { "ContextName", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EncryptionKeys_ContextName",
                schema: "security",
                table: "EncryptionKeys",
                column: "ContextName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EncryptionKeys",
                schema: "security");
        }
    }
}
