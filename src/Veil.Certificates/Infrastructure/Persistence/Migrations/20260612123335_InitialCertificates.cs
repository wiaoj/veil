using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Veil.Certificates.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCertificates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "certificates");

            migrationBuilder.CreateTable(
                name: "certificates",
                schema: "certificates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    Hostname = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ChainPem = table.Column<string>(type: "text", nullable: true),
                    EncryptedPrivateKey = table.Column<string>(type: "text", nullable: true),
                    RequestedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IssuedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_certificates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                schema: "certificates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ProcessedBy = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    PartitionKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LockId = table.Column<string>(type: "text", nullable: true),
                    LockExpiration = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_certificates_Hostname",
                schema: "certificates",
                table: "certificates",
                column: "Hostname");

            migrationBuilder.CreateIndex(
                name: "IX_certificates_Hostname_Status",
                schema: "certificates",
                table: "certificates",
                columns: new[] { "Hostname", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_PartitionKey_ProcessedAt",
                schema: "certificates",
                table: "OutboxMessages",
                columns: new[] { "PartitionKey", "ProcessedAt" },
                filter: "\"ProcessedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_ProcessedAt_LockId_LockExpiration",
                schema: "certificates",
                table: "OutboxMessages",
                columns: new[] { "ProcessedAt", "LockId", "LockExpiration" },
                filter: "\"ProcessedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_ProcessedAt_OccurredAt",
                schema: "certificates",
                table: "OutboxMessages",
                columns: new[] { "ProcessedAt", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "certificates",
                schema: "certificates");

            migrationBuilder.DropTable(
                name: "OutboxMessages",
                schema: "certificates");
        }
    }
}
