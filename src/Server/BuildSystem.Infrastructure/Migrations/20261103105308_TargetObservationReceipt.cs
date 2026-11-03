using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildSystem.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TargetObservationReceipt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TargetObservationReceipts",
                columns: table => new
                {
                    ReceiptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObservationRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RacObservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SqlObservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Outcome = table.Column<int>(type: "int", nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Executable = table.Column<bool>(type: "bit", nullable: false),
                    ScheduledJobsMustRemainBlocked = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TargetObservationReceipts", x => x.ReceiptId);
                    table.CheckConstraint("CK_TargetObservationReceipts_DistinctObservationIds", "[RacObservationId] <> [SqlObservationId]");
                    table.CheckConstraint("CK_TargetObservationReceipts_NonemptyIds", "[ReceiptId] <> '00000000-0000-0000-0000-000000000000' AND [ObservationRequestId] <> '00000000-0000-0000-0000-000000000000' AND [RacObservationId] <> '00000000-0000-0000-0000-000000000000' AND [SqlObservationId] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_TargetObservationReceipts_NonExecutable", "[Executable] = 0");
                    table.CheckConstraint("CK_TargetObservationReceipts_Outcome", "[Outcome] BETWEEN 0 AND 3");
                    table.CheckConstraint("CK_TargetObservationReceipts_ScheduledJobsBlocked", "[ScheduledJobsMustRemainBlocked] = 1");
                    table.ForeignKey(
                        name: "FK_TargetObservationReceipts_TargetObservations_ObservationRequestId",
                        column: x => x.ObservationRequestId,
                        principalTable: "TargetObservations",
                        principalColumn: "ObservationRequestId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TargetObservationReceipts_ObservationRequestId",
                table: "TargetObservationReceipts",
                column: "ObservationRequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TargetObservationReceipts_RacObservationId",
                table: "TargetObservationReceipts",
                column: "RacObservationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TargetObservationReceipts_SqlObservationId",
                table: "TargetObservationReceipts",
                column: "SqlObservationId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TargetObservationReceipts");
        }
    }
}
