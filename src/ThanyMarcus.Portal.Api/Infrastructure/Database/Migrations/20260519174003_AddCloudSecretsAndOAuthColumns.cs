using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace ThanyMarcus.Portal.Api.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddCloudSecretsAndOAuthColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "connection_status",
                table: "clouds",
                type: "text",
                nullable: false,
                defaultValue: "connected");

            migrationBuilder.AddColumn<Instant>(
                name: "minting_spaces_started_at",
                table: "clouds",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "cloud_secrets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    cloud_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    ciphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    tag = table.Column<byte[]>(type: "bytea", nullable: false),
                    expires_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cloud_secrets", x => x.id);
                    table.ForeignKey(
                        name: "fk_cloud_secrets_clouds_cloud_id",
                        column: x => x.cloud_id,
                        principalTable: "clouds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_cloud_secrets_cloud_id_kind",
                table: "cloud_secrets",
                columns: new[] { "cloud_id", "kind" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cloud_secrets");

            migrationBuilder.DropColumn(
                name: "connection_status",
                table: "clouds");

            migrationBuilder.DropColumn(
                name: "minting_spaces_started_at",
                table: "clouds");
        }
    }
}
