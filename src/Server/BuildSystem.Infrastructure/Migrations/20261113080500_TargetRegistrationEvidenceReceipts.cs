using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildSystem.Infrastructure.Migrations
{
    /// <summary>Historical evidence only; does not modify execution or ownership ledgers.</summary>
    public partial class TargetRegistrationEvidenceReceipts : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TargetRegistrationEvidenceRecords",
                columns: table => new
                {
                    ClaimId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PermitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CredentialId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreationEndpointMappingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RestoreIntentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetIncarnationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CanonicalClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CanonicalSqlServerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReportedInfobaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RacObservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SqlObservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RacEndpointMappingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SqlEndpointMappingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FencingToken = table.Column<long>(type: "bigint", nullable: false),
                    RegistrationProfileId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    InfobaseName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    SqlServer = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false, collation: "Latin1_General_100_BIN2"),
                    DatabaseName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    SqlCreateDate = table.Column<string>(type: "nvarchar(33)", maxLength: 33, nullable: false, collation: "Latin1_General_100_BIN2"),
                    SqlState = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    SqlDatabaseId = table.Column<int>(type: "int", nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", precision: 7, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TargetRegistrationEvidenceRecords", x => x.ClaimId);
                    table.CheckConstraint("CK_TargetRegistrationEvidenceRecords_Positive", "[SqlDatabaseId] > 0 AND [FencingToken] > 0");
                    table.CheckConstraint("CK_TargetRegistrationEvidenceRecords_DistinctObservations", "[RacObservationId] <> [SqlObservationId]");
                    table.CheckConstraint("CK_TargetRegistrationEvidenceRecords_Profile", "[RegistrationProfileId] = N'rac-registration-8.3.22.2557-v1' AND DATALENGTH([RegistrationProfileId]) = 62");
                    table.CheckConstraint("CK_TargetRegistrationEvidenceRecords_SqlState", "[SqlState] = N'ONLINE' AND DATALENGTH([SqlState]) = 12");
                    table.CheckConstraint("CK_TargetRegistrationEvidenceRecords_CreateDate", "LEN([SqlCreateDate]) BETWEEN 1 AND 33");
                    table.CheckConstraint("CK_TargetRegistrationEvidenceRecords_AcceptedAt", "DATEPART(TZOFFSET, [AcceptedAt]) = 0");
                    table.CheckConstraint("CK_TargetRegistrationEvidenceRecords_Names", "LEN([InfobaseName]) > 0 AND LEN([SqlServer]) > 0 AND LEN([DatabaseName]) > 0");
                    table.CheckConstraint("CK_TargetRegistrationEvidenceRecords_Ids", "[ClaimId] <> '00000000-0000-0000-0000-000000000000' AND [OperationId] <> '00000000-0000-0000-0000-000000000000' AND [AgentId] <> '00000000-0000-0000-0000-000000000000' AND [AttemptId] <> '00000000-0000-0000-0000-000000000000' AND [PermitId] <> '00000000-0000-0000-0000-000000000000' AND [CredentialId] <> '00000000-0000-0000-0000-000000000000' AND [CreationEndpointMappingId] <> '00000000-0000-0000-0000-000000000000' AND [RestoreIntentId] <> '00000000-0000-0000-0000-000000000000' AND [TargetIncarnationId] <> '00000000-0000-0000-0000-000000000000' AND [ReservationId] <> '00000000-0000-0000-0000-000000000000' AND [CanonicalClusterId] <> '00000000-0000-0000-0000-000000000000' AND [CanonicalSqlServerId] <> '00000000-0000-0000-0000-000000000000' AND [ReportedInfobaseId] <> '00000000-0000-0000-0000-000000000000' AND [RacObservationId] <> '00000000-0000-0000-0000-000000000000' AND [SqlObservationId] <> '00000000-0000-0000-0000-000000000000' AND [RacEndpointMappingId] <> '00000000-0000-0000-0000-000000000000' AND [SqlEndpointMappingId] <> '00000000-0000-0000-0000-000000000000'");
                    table.ForeignKey("FK_TargetRegistrationEvidenceRecords_TargetCreationExecutionClaims_ClaimId", x => x.ClaimId, "TargetCreationExecutionClaims", "ClaimId", onDelete: ReferentialAction.Restrict);
                    table.ForeignKey("FK_TargetRegistrationEvidenceRecords_TargetCreationOperations_OperationId", x => x.OperationId, "TargetCreationOperations", "OperationId", onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex("IX_TargetRegistrationEvidenceRecords_OperationId", "TargetRegistrationEvidenceRecords", "OperationId", unique: true);
            migrationBuilder.CreateIndex("IX_TargetRegistrationEvidenceRecords_AttemptId", "TargetRegistrationEvidenceRecords", "AttemptId", unique: true);
            migrationBuilder.CreateIndex("IX_TargetRegistrationEvidenceRecords_RacObservationId", "TargetRegistrationEvidenceRecords", "RacObservationId", unique: true);
            migrationBuilder.CreateIndex("IX_TargetRegistrationEvidenceRecords_SqlObservationId", "TargetRegistrationEvidenceRecords", "SqlObservationId", unique: true);

            migrationBuilder.Sql("""
CREATE TRIGGER [TR_TargetRegistrationEvidenceRecords_AppendOnly]
ON [TargetRegistrationEvidenceRecords]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted)
        THROW 51000, 'Target registration evidence is append-only.', 1;

    -- Independent indexes reject same-role reuse. Range locks protect concurrent
    -- cross-role inserts; the join includes every row of a multi-row batch.
    IF EXISTS (
        SELECT 1 FROM inserted AS i
        INNER JOIN [TargetRegistrationEvidenceRecords] AS t WITH (UPDLOCK, HOLDLOCK)
            ON t.[ClaimId] <> i.[ClaimId]
            AND (t.[RacObservationId] = i.[SqlObservationId]
                OR t.[SqlObservationId] = i.[RacObservationId])
    )
        THROW 51001, 'Registration evidence observation IDs cannot be reused.', 1;
END
""");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "TargetRegistrationEvidenceRecords");
        }
    }
}
