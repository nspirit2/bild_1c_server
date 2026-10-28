using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildSystem.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class StageCleanupResult : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StageCleanupResults",
                columns: table => new
                {
                    ResultId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AuthorizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StageFencingToken = table.Column<long>(type: "bigint", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StageCleanupResults", x => x.ResultId);
                    table.ForeignKey(
                        name: "FK_StageCleanupResults_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StageCleanupResults_StageCleanupAuthorizations_AuthorizationId",
                        column: x => x.AuthorizationId,
                        principalTable: "StageCleanupAuthorizations",
                        principalColumn: "AuthorizationId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StageCleanupResults_StagedArtifacts_ArtifactId",
                        column: x => x.ArtifactId,
                        principalTable: "StagedArtifacts",
                        principalColumn: "ArtifactId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StageCleanupResults_AgentId",
                table: "StageCleanupResults",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_StageCleanupResults_ArtifactId",
                table: "StageCleanupResults",
                column: "ArtifactId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StageCleanupResults_AuthorizationId",
                table: "StageCleanupResults",
                column: "AuthorizationId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StageCleanupResults");
        }
    }
}
