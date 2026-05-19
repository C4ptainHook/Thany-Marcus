using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace ThanyMarcus.Portal.Api.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddDigitalOceanOAuthConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "digitalocean_oauth_connections",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    access_ciphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    access_nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    access_tag = table.Column<byte[]>(type: "bytea", nullable: false),
                    access_expires_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    refresh_ciphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    refresh_nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    refresh_tag = table.Column<byte[]>(type: "bytea", nullable: false),
                    connection_status = table.Column<string>(type: "text", nullable: false, defaultValue: "connected"),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_digitalocean_oauth_connections", x => x.user_id);
                    table.ForeignKey(
                        name: "fk_digitalocean_oauth_connections_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "digitalocean_oauth_connections");
        }
    }
}
