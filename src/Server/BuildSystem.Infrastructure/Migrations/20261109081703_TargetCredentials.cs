using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildSystem.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TargetCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TargetCredentials",
                columns: table => new
                {
                    CredentialId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CanonicalClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CanonicalSqlServerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EndpointMappingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ciphertext = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    State = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TargetCredentials", x => x.CredentialId);
                    table.CheckConstraint("CK_TargetCredentials_Ciphertext", "[Ciphertext] IS NOT NULL AND LEN([Ciphertext]) > 0 AND LEN([Ciphertext]) <= 262144 AND DATALENGTH([Ciphertext]) > 0 AND DATALENGTH([Ciphertext]) <= 524288");
                    table.CheckConstraint("CK_TargetCredentials_Ids", "[CredentialId] <> '00000000-0000-0000-0000-000000000000' AND [CanonicalClusterId] <> '00000000-0000-0000-0000-000000000000' AND [CanonicalSqlServerId] <> '00000000-0000-0000-0000-000000000000' AND [EndpointMappingId] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_TargetCredentials_State", "([State] = 'Stored' AND [Revision] = 1 AND [RevokedAt] IS NULL) OR ([State] = 'Revoked' AND [Revision] = 2 AND [RevokedAt] IS NOT NULL)");
                    table.CheckConstraint("CK_TargetCredentials_Time", "DATEPART(TZOFFSET, [CreatedAt]) = 0 AND ([RevokedAt] IS NULL OR (DATEPART(TZOFFSET, [RevokedAt]) = 0 AND [RevokedAt] >= [CreatedAt]))");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TargetCredentials");
        }
    }
}
