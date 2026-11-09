using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildSystem.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CreationPermitCredentialPins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CredentialId",
                table: "TargetCreationPermits",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EndpointMappingId",
                table: "TargetCreationPermits",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TargetCreationPermits_CredentialId",
                table: "TargetCreationPermits",
                column: "CredentialId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_TargetCreationPermits_CredentialPins",
                table: "TargetCreationPermits",
                sql: "([CredentialId] IS NULL AND [EndpointMappingId] IS NULL) OR ([CredentialId] IS NOT NULL AND [EndpointMappingId] IS NOT NULL AND [CredentialId] <> '00000000-0000-0000-0000-000000000000' AND [EndpointMappingId] <> '00000000-0000-0000-0000-000000000000')");

            migrationBuilder.AddForeignKey(
                name: "FK_TargetCreationPermits_TargetCredentials_CredentialId",
                table: "TargetCreationPermits",
                column: "CredentialId",
                principalTable: "TargetCredentials",
                principalColumn: "CredentialId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TargetCreationPermits_TargetCredentials_CredentialId",
                table: "TargetCreationPermits");

            migrationBuilder.DropIndex(
                name: "IX_TargetCreationPermits_CredentialId",
                table: "TargetCreationPermits");

            migrationBuilder.DropCheckConstraint(
                name: "CK_TargetCreationPermits_CredentialPins",
                table: "TargetCreationPermits");

            migrationBuilder.DropColumn(
                name: "CredentialId",
                table: "TargetCreationPermits");

            migrationBuilder.DropColumn(
                name: "EndpointMappingId",
                table: "TargetCreationPermits");
        }
    }
}
