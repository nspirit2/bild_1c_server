using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildSystem.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PendingArchiveCancellations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PendingArchiveCancellations",
                columns: table => new
                {
                    EnvironmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RootId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PoolId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingArchiveCancellations", x => x.EnvironmentId);
                    table.CheckConstraint("CK_PendingArchiveCancellations_CancelledAt", "DATEPART(TZOFFSET, [CancelledAt]) = 0 AND [CancelledAt] > '0001-01-01T00:00:00+00:00'");
                    table.CheckConstraint("CK_PendingArchiveCancellations_Ids", "[EnvironmentId] <> '00000000-0000-0000-0000-000000000000'\nAND [RequestId] <> '00000000-0000-0000-0000-000000000000'\nAND [JobId] <> '00000000-0000-0000-0000-000000000000'\nAND [ReservationId] <> '00000000-0000-0000-0000-000000000000'\nAND [RootId] <> '00000000-0000-0000-0000-000000000000'\nAND [PoolId] <> '00000000-0000-0000-0000-000000000000'\nAND [ActorId] <> '00000000-0000-0000-0000-000000000000'");
                    table.ForeignKey(
                        name: "FK_PendingArchiveCancellations_ArchiveRoots_RootId",
                        column: x => x.RootId,
                        principalTable: "ArchiveRoots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PendingArchiveCancellations_EnvironmentJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "EnvironmentJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PendingArchiveCancellations_EnvironmentPools_PoolId",
                        column: x => x.PoolId,
                        principalTable: "EnvironmentPools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PendingArchiveCancellations_EnvironmentRequests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "EnvironmentRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PendingArchiveCancellations_EnvironmentReservations_ReservationId",
                        column: x => x.ReservationId,
                        principalTable: "EnvironmentReservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PendingArchiveCancellations_TestEnvironments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "TestEnvironments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PendingArchiveCancellations_JobId",
                table: "PendingArchiveCancellations",
                column: "JobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PendingArchiveCancellations_PoolId",
                table: "PendingArchiveCancellations",
                column: "PoolId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingArchiveCancellations_RequestId",
                table: "PendingArchiveCancellations",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PendingArchiveCancellations_ReservationId",
                table: "PendingArchiveCancellations",
                column: "ReservationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PendingArchiveCancellations_RootId",
                table: "PendingArchiveCancellations",
                column: "RootId");

            // CREATE TRIGGER must start its batch, including inside idempotent migration scripts.
            migrationBuilder.Sql("""
                EXEC(N'CREATE TRIGGER [TR_PendingArchiveCancellations_AppendOnly]
                ON [PendingArchiveCancellations]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM deleted)
                        THROW 51002, ''Pending archive cancellation receipts are append-only.'', 1;
                END');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PendingArchiveCancellations");
        }
    }
}
