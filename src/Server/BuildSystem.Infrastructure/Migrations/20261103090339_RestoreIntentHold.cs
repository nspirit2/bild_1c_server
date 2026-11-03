using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildSystem.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RestoreIntentHold : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RestoreIntents",
                columns: table => new
                {
                    RestoreIntentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StageJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PoolId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RootId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArchiveVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Format = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    SnapshotSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PreparationProfileVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ScheduledJobsMustRemainBlocked = table.Column<bool>(type: "bit", nullable: false),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FencingToken = table.Column<long>(type: "bigint", nullable: false),
                    TargetIncarnation = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CanonicalClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CanonicalSqlServerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InfobaseName = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false, collation: "Latin1_General_100_BIN2"),
                    SqlDbName = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false, collation: "Latin1_General_100_BIN2"),
                    RequestedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Executable = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RestoreIntents", x => x.RestoreIntentId);
                    table.CheckConstraint("CK_RestoreIntents_NonExecutable", "[Executable] = 0");
                    table.CheckConstraint("CK_RestoreIntents_ScheduledJobsBlocked", "[ScheduledJobsMustRemainBlocked] = 1");
                    table.ForeignKey(
                        name: "FK_RestoreIntents_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RestoreIntents_AspNetUsers_RequestedBy",
                        column: x => x.RequestedBy,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RestoreIntents_EnvironmentJobs_StageJobId",
                        column: x => x.StageJobId,
                        principalTable: "EnvironmentJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RestoreIntents_EnvironmentReservations_ReservationId",
                        column: x => x.ReservationId,
                        principalTable: "EnvironmentReservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RestoreIntents_StagedArtifacts_ArtifactId",
                        column: x => x.ArtifactId,
                        principalTable: "StagedArtifacts",
                        principalColumn: "ArtifactId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RestoreIntents_TestEnvironments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "TestEnvironments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RestoreIntents_AgentId",
                table: "RestoreIntents",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_RestoreIntents_ArtifactId",
                table: "RestoreIntents",
                column: "ArtifactId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RestoreIntents_CanonicalClusterId_InfobaseName",
                table: "RestoreIntents",
                columns: new[] { "CanonicalClusterId", "InfobaseName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RestoreIntents_CanonicalSqlServerId_SqlDbName",
                table: "RestoreIntents",
                columns: new[] { "CanonicalSqlServerId", "SqlDbName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RestoreIntents_EnvironmentId",
                table: "RestoreIntents",
                column: "EnvironmentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RestoreIntents_RequestedBy",
                table: "RestoreIntents",
                column: "RequestedBy");

            migrationBuilder.CreateIndex(
                name: "IX_RestoreIntents_ReservationId",
                table: "RestoreIntents",
                column: "ReservationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RestoreIntents_StageJobId",
                table: "RestoreIntents",
                column: "StageJobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RestoreIntents_TargetIncarnation",
                table: "RestoreIntents",
                column: "TargetIncarnation",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RestoreIntents");
        }
    }
}
