using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThanyMarcus.Cloud.Api.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddIngestJobConsecutiveCrashes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<short>(
                name: "consecutive_crashes",
                table: "ingest_jobs",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "consecutive_crashes",
                table: "ingest_jobs");
        }
    }
}
