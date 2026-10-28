using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildSystem.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class StagingAllocationRelease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StagingAllocationReleases",
                columns: table => new
                {
                    ReleaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResultId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AuthorizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RootId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PoolId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReleasedBytes = table.Column<long>(type: "bigint", nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StagingAllocationReleases", x => x.ReleaseId);
                    table.ForeignKey(
                        name: "FK_StagingAllocationReleases_ArchiveRoots_RootId",
                        column: x => x.RootId,
                        principalTable: "ArchiveRoots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StagingAllocationReleases_AspNetUsers_ActorId",
                        column: x => x.ActorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StagingAllocationReleases_EnvironmentPools_PoolId",
                        column: x => x.PoolId,
                        principalTable: "EnvironmentPools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StagingAllocationReleases_EnvironmentReservations_ReservationId",
                        column: x => x.ReservationId,
                        principalTable: "EnvironmentReservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StagingAllocationReleases_StageCleanupAuthorizations_AuthorizationId",
                        column: x => x.AuthorizationId,
                        principalTable: "StageCleanupAuthorizations",
                        principalColumn: "AuthorizationId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StagingAllocationReleases_StageCleanupResults_ResultId",
                        column: x => x.ResultId,
                        principalTable: "StageCleanupResults",
                        principalColumn: "ResultId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StagingAllocationReleases_ActorId",
                table: "StagingAllocationReleases",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_StagingAllocationReleases_AuthorizationId",
                table: "StagingAllocationReleases",
                column: "AuthorizationId");

            migrationBuilder.CreateIndex(
                name: "IX_StagingAllocationReleases_PoolId",
                table: "StagingAllocationReleases",
                column: "PoolId");

            migrationBuilder.CreateIndex(
                name: "IX_StagingAllocationReleases_ReservationId",
                table: "StagingAllocationReleases",
                column: "ReservationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StagingAllocationReleases_ResultId",
                table: "StagingAllocationReleases",
                column: "ResultId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StagingAllocationReleases_RootId",
                table: "StagingAllocationReleases",
                column: "RootId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StagingAllocationReleases");
        }
    }
}
