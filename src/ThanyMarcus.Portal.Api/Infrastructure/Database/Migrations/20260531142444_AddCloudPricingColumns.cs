using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace ThanyMarcus.Portal.Api.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddCloudPricingColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "price_currency",
                table: "clouds",
                type: "text",
                nullable: true,
                defaultValue: "USD");

            migrationBuilder.AddColumn<decimal>(
                name: "price_hourly_usd",
                table: "clouds",
                type: "numeric(10,6)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "price_monthly_usd",
                table: "clouds",
                type: "numeric(10,4)",
                nullable: true);

            migrationBuilder.AddColumn<Instant>(
                name: "priced_at",
                table: "clouds",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "priced_source",
                table: "clouds",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "price_currency",
                table: "clouds");

            migrationBuilder.DropColumn(
                name: "price_hourly_usd",
                table: "clouds");

            migrationBuilder.DropColumn(
                name: "price_monthly_usd",
                table: "clouds");

            migrationBuilder.DropColumn(
                name: "priced_at",
                table: "clouds");

            migrationBuilder.DropColumn(
                name: "priced_source",
                table: "clouds");
        }
    }
}
