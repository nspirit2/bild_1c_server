using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildSystem.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TargetCreationOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TargetCreationOperations",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RestoreIntentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AttemptExpectedRevision = table.Column<long>(type: "bigint", nullable: true),
                    QuarantineReason = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Executable = table.Column<bool>(type: "bit", nullable: false),
                    ScheduledJobsMustRemainBlocked = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TargetCreationOperations", x => x.OperationId);
                    table.CheckConstraint("CK_TargetCreationOperations_Ids", "[OperationId] <> '00000000-0000-0000-0000-000000000000' AND [RestoreIntentId] <> '00000000-0000-0000-0000-000000000000' AND ([AttemptId] IS NULL OR [AttemptId] <> '00000000-0000-0000-0000-000000000000')");
                    table.CheckConstraint("CK_TargetCreationOperations_JobsBlocked", "[ScheduledJobsMustRemainBlocked] = 1");
                    table.CheckConstraint("CK_TargetCreationOperations_NonExecutable", "[Executable] = 0");
                    table.CheckConstraint("CK_TargetCreationOperations_RevisionState", "([State] = 'Registered' AND [Revision] = 1 AND [AttemptId] IS NULL AND [AttemptExpectedRevision] IS NULL AND [QuarantineReason] IS NULL) OR ([State] = 'AttemptRecorded' AND [Revision] = 2 AND [AttemptId] IS NOT NULL AND [AttemptExpectedRevision] IS NOT NULL AND [AttemptExpectedRevision] = 1 AND [QuarantineReason] IS NULL) OR ([State] = 'ReconciliationRequired' AND [Revision] IN (2, 3) AND [QuarantineReason] IS NOT NULL AND [QuarantineReason] IN ('OutcomeUnknown', 'Cancelled', 'AuthorityRejected', 'TargetConflict') AND (([Revision] = 2 AND [AttemptId] IS NULL AND [AttemptExpectedRevision] IS NULL) OR ([Revision] = 3 AND [AttemptId] IS NOT NULL AND [AttemptExpectedRevision] IS NOT NULL AND [AttemptExpectedRevision] = 1)))");
                    table.ForeignKey(
                        name: "FK_TargetCreationOperations_RestoreIntents_RestoreIntentId",
                        column: x => x.RestoreIntentId,
                        principalTable: "RestoreIntents",
                        principalColumn: "RestoreIntentId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TargetCreationOperations_AttemptId",
                table: "TargetCreationOperations",
                column: "AttemptId",
                unique: true,
                filter: "[AttemptId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TargetCreationOperations_RestoreIntentId",
                table: "TargetCreationOperations",
                column: "RestoreIntentId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TargetCreationOperations");
        }
    }
}
