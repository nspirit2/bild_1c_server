using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildSystem.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TargetCreationPermits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TargetCreationPermits",
                columns: table => new
                {
                    PermitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExpectedOperationRevision = table.Column<long>(type: "bigint", nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StartBefore = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    StartExpectedRevision = table.Column<long>(type: "bigint", nullable: true),
                    Executable = table.Column<bool>(type: "bit", nullable: false),
                    ScheduledJobsMustRemainBlocked = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TargetCreationPermits", x => x.PermitId);
                    table.CheckConstraint("CK_TargetCreationPermits_ExpectedOperationRevision", "[ExpectedOperationRevision] = 1");
                    table.CheckConstraint("CK_TargetCreationPermits_Ids", "[PermitId] <> '00000000-0000-0000-0000-000000000000' AND [OperationId] <> '00000000-0000-0000-0000-000000000000' AND [AttemptId] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_TargetCreationPermits_JobsBlocked", "[ScheduledJobsMustRemainBlocked] = 1");
                    table.CheckConstraint("CK_TargetCreationPermits_NonExecutable", "[Executable] = 0");
                    table.CheckConstraint("CK_TargetCreationPermits_RevisionState", "([State] = 'Prepared' AND [Revision] = 1 AND [StartedAt] IS NULL AND [StartExpectedRevision] IS NULL) OR ([State] = 'StartRecorded' AND [Revision] = 2 AND [StartedAt] IS NOT NULL AND [StartExpectedRevision] IS NOT NULL AND [StartExpectedRevision] = 1) OR ([State] = 'Revoked' AND (([Revision] = 2 AND [StartedAt] IS NULL AND [StartExpectedRevision] IS NULL) OR ([Revision] = 3 AND [StartedAt] IS NOT NULL AND [StartExpectedRevision] IS NOT NULL AND [StartExpectedRevision] = 1)))");
                    table.CheckConstraint("CK_TargetCreationPermits_StartedAt", "[StartedAt] IS NULL OR (DATEPART(TZOFFSET, [StartedAt]) = 0 AND [StartedAt] >= [IssuedAt] AND [StartedAt] < [StartBefore])");
                    table.CheckConstraint("CK_TargetCreationPermits_Window", "DATEPART(TZOFFSET, [IssuedAt]) = 0 AND DATEPART(TZOFFSET, [StartBefore]) = 0 AND DATEADD(minute, 5, [IssuedAt]) = [StartBefore]");
                    table.ForeignKey(
                        name: "FK_TargetCreationPermits_TargetCreationOperations_OperationId",
                        column: x => x.OperationId,
                        principalTable: "TargetCreationOperations",
                        principalColumn: "OperationId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TargetCreationPermits_AttemptId",
                table: "TargetCreationPermits",
                column: "AttemptId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TargetCreationPermits_OperationId",
                table: "TargetCreationPermits",
                column: "OperationId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TargetCreationPermits");
        }
    }
}
