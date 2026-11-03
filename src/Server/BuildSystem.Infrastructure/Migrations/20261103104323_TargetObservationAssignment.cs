using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildSystem.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TargetObservationAssignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TargetObservations",
                columns: table => new
                {
                    ObservationRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RestoreIntentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StageJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FencingToken = table.Column<long>(type: "bigint", nullable: false),
                    ReservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetIncarnationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CanonicalClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CanonicalSqlServerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RacEndpointMappingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SqlEndpointMappingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Executable = table.Column<bool>(type: "bit", nullable: false),
                    ScheduledJobsMustRemainBlocked = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TargetObservations", x => x.ObservationRequestId);
                    table.CheckConstraint("CK_TargetObservations_NonemptyIds", "[ObservationRequestId] <> '00000000-0000-0000-0000-000000000000' AND [RestoreIntentId] <> '00000000-0000-0000-0000-000000000000' AND [AgentId] <> '00000000-0000-0000-0000-000000000000' AND [OperationId] <> '00000000-0000-0000-0000-000000000000' AND [EnvironmentId] <> '00000000-0000-0000-0000-000000000000' AND [StageJobId] <> '00000000-0000-0000-0000-000000000000' AND [ReservationId] <> '00000000-0000-0000-0000-000000000000' AND [TargetIncarnationId] <> '00000000-0000-0000-0000-000000000000' AND [CanonicalClusterId] <> '00000000-0000-0000-0000-000000000000' AND [CanonicalSqlServerId] <> '00000000-0000-0000-0000-000000000000' AND [RacEndpointMappingId] <> '00000000-0000-0000-0000-000000000000' AND [SqlEndpointMappingId] <> '00000000-0000-0000-0000-000000000000' AND [RequestedBy] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_TargetObservations_NonExecutable", "[Executable] = 0");
                    table.CheckConstraint("CK_TargetObservations_PositiveFence", "[FencingToken] > 0");
                    table.CheckConstraint("CK_TargetObservations_ScheduledJobsBlocked", "[ScheduledJobsMustRemainBlocked] = 1");
                    table.ForeignKey(
                        name: "FK_TargetObservations_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TargetObservations_AspNetUsers_RequestedBy",
                        column: x => x.RequestedBy,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TargetObservations_RestoreIntents_RestoreIntentId",
                        column: x => x.RestoreIntentId,
                        principalTable: "RestoreIntents",
                        principalColumn: "RestoreIntentId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TargetObservations_AgentId",
                table: "TargetObservations",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_TargetObservations_OperationId",
                table: "TargetObservations",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TargetObservations_RequestedBy",
                table: "TargetObservations",
                column: "RequestedBy");

            migrationBuilder.CreateIndex(
                name: "IX_TargetObservations_RestoreIntentId",
                table: "TargetObservations",
                column: "RestoreIntentId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TargetObservations");
        }
    }
}
