using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThanyMarcus.Portal.Api.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class Phase0TrustBoundary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "encrypted_cloud_admin_token",
                table: "clouds");

            migrationBuilder.AddColumn<byte[]>(
                name: "admin_token_ciphertext",
                table: "provisioning_jobs",
                type: "bytea",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "admin_token_ciphertext",
                table: "provisioning_jobs");

            migrationBuilder.AddColumn<byte[]>(
                name: "encrypted_cloud_admin_token",
                table: "clouds",
                type: "bytea",
                nullable: true);
        }
    }
}
