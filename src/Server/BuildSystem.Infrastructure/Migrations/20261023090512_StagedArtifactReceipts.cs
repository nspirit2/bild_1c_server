using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildSystem.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class StagedArtifactReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StagedArtifacts",
                columns: table => new
                {
                    ArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FencingToken = table.Column<long>(type: "bigint", nullable: false),
                    ReservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArchiveVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Format = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StagedArtifacts", x => x.ArtifactId);
                    table.ForeignKey(
                        name: "FK_StagedArtifacts_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StagedArtifacts_DiskArchiveVersions_ArchiveVersionId",
                        column: x => x.ArchiveVersionId,
                        principalTable: "DiskArchiveVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StagedArtifacts_EnvironmentJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "EnvironmentJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StagedArtifacts_EnvironmentReservations_ReservationId",
                        column: x => x.ReservationId,
                        principalTable: "EnvironmentReservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StagedArtifacts_AgentId",
                table: "StagedArtifacts",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_StagedArtifacts_ArchiveVersionId",
                table: "StagedArtifacts",
                column: "ArchiveVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_StagedArtifacts_JobId",
                table: "StagedArtifacts",
                column: "JobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StagedArtifacts_ReservationId_ArchiveVersionId",
                table: "StagedArtifacts",
                columns: new[] { "ReservationId", "ArchiveVersionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StagedArtifacts");
        }
    }
}
