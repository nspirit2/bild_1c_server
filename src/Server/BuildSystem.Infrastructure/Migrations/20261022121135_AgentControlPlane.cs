using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildSystem.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AgentControlPlane : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentEnrollments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExpectedAgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExpectedName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false, collation: "Latin1_General_100_BIN2"),
                    PoolId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentEnrollments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentEnrollments_EnvironmentPools_PoolId",
                        column: x => x.PoolId,
                        principalTable: "EnvironmentPools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Agents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    PoolId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true, collation: "Latin1_General_100_BIN2"),
                    SupportsControlPlaneProbe = table.Column<bool>(type: "bit", nullable: false),
                    LastHeartbeat = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Agents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Agents_EnvironmentPools_PoolId",
                        column: x => x.PoolId,
                        principalTable: "EnvironmentPools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AgentEvents",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FencingToken = table.Column<long>(type: "bigint", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    PayloadHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentEvents", x => x.EventId);
                    table.ForeignKey(
                        name: "FK_AgentEvents_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentEvents_EnvironmentJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "EnvironmentJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AgentLeases",
                columns: table => new
                {
                    JobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FencingToken = table.Column<long>(type: "bigint", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentLeases", x => x.JobId);
                    table.ForeignKey(
                        name: "FK_AgentLeases_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentLeases_EnvironmentJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "EnvironmentJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentEnrollments_PoolId",
                table: "AgentEnrollments",
                column: "PoolId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentEvents_AgentId",
                table: "AgentEvents",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentEvents_JobId",
                table: "AgentEvents",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentLeases_AgentId_State",
                table: "AgentLeases",
                columns: new[] { "AgentId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_Agents_PoolId",
                table: "Agents",
                column: "PoolId");

            migrationBuilder.CreateIndex(
                name: "IX_Agents_TokenHash",
                table: "Agents",
                column: "TokenHash",
                unique: true,
                filter: "[TokenHash] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentEnrollments");

            migrationBuilder.DropTable(
                name: "AgentEvents");

            migrationBuilder.DropTable(
                name: "AgentLeases");

            migrationBuilder.DropTable(
                name: "Agents");
        }
    }
}
