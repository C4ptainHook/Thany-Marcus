using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace ThanyMarcus.Cloud.Api.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class TrustBoundaryHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Instant>(
                name: "bootstrap_consumed_at",
                table: "cloud_settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "recovery_anchor_hash",
                table: "cloud_settings",
                type: "bytea",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "cloud_settings",
                keyColumn: "id",
                keyValue: 1,
                columns: new[] { "bootstrap_consumed_at", "recovery_anchor_hash" },
                values: new object[] { null, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bootstrap_consumed_at",
                table: "cloud_settings");

            migrationBuilder.DropColumn(
                name: "recovery_anchor_hash",
                table: "cloud_settings");
        }
    }
}
