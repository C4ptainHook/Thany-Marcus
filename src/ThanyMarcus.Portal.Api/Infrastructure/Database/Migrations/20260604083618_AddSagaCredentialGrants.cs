using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace ThanyMarcus.Portal.Api.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddSagaCredentialGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "saga_credential_grants",
                columns: table => new
                {
                    cloud_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sealed_dek = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_saga_credential_grants", x => x.cloud_id);
                    table.ForeignKey(
                        name: "fk_saga_credential_grants_clouds_cloud_id",
                        column: x => x.cloud_id,
                        principalTable: "clouds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_saga_credential_grants_expires_at",
                table: "saga_credential_grants",
                column: "expires_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "saga_credential_grants");
        }
    }
}
