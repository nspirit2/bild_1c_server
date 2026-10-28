using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildSystem.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class StageCleanupAuthorization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StageCleanupAuthorizations",
                columns: table => new
                {
                    AuthorizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RetirementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StageFencingToken = table.Column<long>(type: "bigint", nullable: false),
                    AuthorizedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PlanJson = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StageCleanupAuthorizations", x => x.AuthorizationId);
                    table.ForeignKey(
                        name: "FK_StageCleanupAuthorizations_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StageCleanupAuthorizations_StageRetirementIntents_RetirementId",
                        column: x => x.RetirementId,
                        principalTable: "StageRetirementIntents",
                        principalColumn: "RetirementId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StageCleanupAuthorizations_StagedArtifacts_ArtifactId",
                        column: x => x.ArtifactId,
                        principalTable: "StagedArtifacts",
                        principalColumn: "ArtifactId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StageCleanupAuthorizations_AgentId",
                table: "StageCleanupAuthorizations",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_StageCleanupAuthorizations_ArtifactId",
                table: "StageCleanupAuthorizations",
                column: "ArtifactId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StageCleanupAuthorizations_RetirementId",
                table: "StageCleanupAuthorizations",
                column: "RetirementId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StageCleanupAuthorizations");
        }
    }
}
