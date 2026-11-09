using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildSystem.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TargetCreationExecutionClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TargetCreationExecutionClaims",
                columns: table => new
                {
                    ClaimId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PermitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CredentialId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EndpointMappingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TargetCreationExecutionClaims", x => x.ClaimId);
                    table.CheckConstraint("CK_TargetCreationExecutionClaims_Ids", "[ClaimId] <> '00000000-0000-0000-0000-000000000000' AND [PermitId] <> '00000000-0000-0000-0000-000000000000' AND [OperationId] <> '00000000-0000-0000-0000-000000000000' AND [AttemptId] <> '00000000-0000-0000-0000-000000000000' AND [AgentId] <> '00000000-0000-0000-0000-000000000000' AND [CredentialId] <> '00000000-0000-0000-0000-000000000000' AND [EndpointMappingId] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_TargetCreationExecutionClaims_Window", "DATEPART(TZOFFSET, [IssuedAt]) = 0 AND DATEPART(TZOFFSET, [ExpiresAt]) = 0 AND [IssuedAt] < [ExpiresAt] AND [IssuedAt] >= DATEADD(minute, -5, [ExpiresAt])");
                    table.ForeignKey(
                        name: "FK_TargetCreationExecutionClaims_TargetCreationOperations_OperationId",
                        column: x => x.OperationId,
                        principalTable: "TargetCreationOperations",
                        principalColumn: "OperationId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TargetCreationExecutionClaims_TargetCreationPermits_PermitId",
                        column: x => x.PermitId,
                        principalTable: "TargetCreationPermits",
                        principalColumn: "PermitId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TargetCreationExecutionClaims_TargetCredentials_CredentialId",
                        column: x => x.CredentialId,
                        principalTable: "TargetCredentials",
                        principalColumn: "CredentialId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TargetCreationExecutionClaims_AttemptId",
                table: "TargetCreationExecutionClaims",
                column: "AttemptId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TargetCreationExecutionClaims_CredentialId",
                table: "TargetCreationExecutionClaims",
                column: "CredentialId");

            migrationBuilder.CreateIndex(
                name: "IX_TargetCreationExecutionClaims_OperationId",
                table: "TargetCreationExecutionClaims",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TargetCreationExecutionClaims_PermitId",
                table: "TargetCreationExecutionClaims",
                column: "PermitId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TargetCreationExecutionClaims");
        }
    }
}
