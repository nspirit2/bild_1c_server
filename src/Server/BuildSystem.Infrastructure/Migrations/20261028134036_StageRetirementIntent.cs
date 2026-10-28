using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildSystem.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class StageRetirementIntent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StageRetirementIntents",
                columns: table => new
                {
                    RetirementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FencingToken = table.Column<long>(type: "bigint", nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StageRetirementIntents", x => x.RetirementId);
                    table.ForeignKey(
                        name: "FK_StageRetirementIntents_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StageRetirementIntents_AspNetUsers_RequestedBy",
                        column: x => x.RequestedBy,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StageRetirementIntents_EnvironmentJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "EnvironmentJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StageRetirementIntents_EnvironmentReservations_ReservationId",
                        column: x => x.ReservationId,
                        principalTable: "EnvironmentReservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StageRetirementIntents_StagedArtifacts_ArtifactId",
                        column: x => x.ArtifactId,
                        principalTable: "StagedArtifacts",
                        principalColumn: "ArtifactId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StageRetirementIntents_TestEnvironments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "TestEnvironments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StageRetirementIntents_AgentId",
                table: "StageRetirementIntents",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_StageRetirementIntents_ArtifactId",
                table: "StageRetirementIntents",
                column: "ArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_StageRetirementIntents_EnvironmentId",
                table: "StageRetirementIntents",
                column: "EnvironmentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StageRetirementIntents_JobId",
                table: "StageRetirementIntents",
                column: "JobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StageRetirementIntents_RequestedBy",
                table: "StageRetirementIntents",
                column: "RequestedBy");

            migrationBuilder.CreateIndex(
                name: "IX_StageRetirementIntents_ReservationId",
                table: "StageRetirementIntents",
                column: "ReservationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StageRetirementIntents");
        }
    }
}
