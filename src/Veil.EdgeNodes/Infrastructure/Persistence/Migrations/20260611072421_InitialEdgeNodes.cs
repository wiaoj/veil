using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Veil.EdgeNodes.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialEdgeNodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "edge_nodes");

            migrationBuilder.CreateTable(
                name: "edge_nodes",
                schema: "edge_nodes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Address = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RegisteredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_edge_nodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "config_push_log",
                schema: "edge_nodes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    EdgeNodeId = table.Column<long>(type: "bigint", nullable: false),
                    Succeeded = table.Column<bool>(type: "boolean", nullable: false),
                    Error = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    PushedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_config_push_log", x => x.Id);
                    table.ForeignKey(
                        name: "FK_config_push_log_edge_nodes_EdgeNodeId",
                        column: x => x.EdgeNodeId,
                        principalSchema: "edge_nodes",
                        principalTable: "edge_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_config_push_log_EdgeNodeId",
                schema: "edge_nodes",
                table: "config_push_log",
                column: "EdgeNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_edge_nodes_TokenHash",
                schema: "edge_nodes",
                table: "edge_nodes",
                column: "TokenHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "config_push_log",
                schema: "edge_nodes");

            migrationBuilder.DropTable(
                name: "edge_nodes",
                schema: "edge_nodes");
        }
    }
}
