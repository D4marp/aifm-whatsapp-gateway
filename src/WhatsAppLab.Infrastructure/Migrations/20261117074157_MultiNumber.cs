using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppLab.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MultiNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WhatsAppOutboundMessageDbSet_Status_ScheduledFor",
                table: "WhatsAppOutboundMessageDbSet");

            migrationBuilder.AddColumn<Guid>(
                name: "SessionId",
                table: "WhatsAppSettingDbSet",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "WhatsAppSessionDbSet",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "WhatsAppSessionDbSet",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProfileKey",
                table: "WhatsAppSessionDbSet",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SessionId",
                table: "WhatsAppOutboundMessageDbSet",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SessionId",
                table: "WhatsAppInboundMessageDbSet",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppSessionDbSet_ProfileKey",
                table: "WhatsAppSessionDbSet",
                column: "ProfileKey");

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppOutboundMessageDbSet_SessionId_Status_ScheduledFor",
                table: "WhatsAppOutboundMessageDbSet",
                columns: new[] { "SessionId", "Status", "ScheduledFor" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WhatsAppSessionDbSet_ProfileKey",
                table: "WhatsAppSessionDbSet");

            migrationBuilder.DropIndex(
                name: "IX_WhatsAppOutboundMessageDbSet_SessionId_Status_ScheduledFor",
                table: "WhatsAppOutboundMessageDbSet");

            migrationBuilder.DropColumn(
                name: "SessionId",
                table: "WhatsAppSettingDbSet");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "WhatsAppSessionDbSet");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "WhatsAppSessionDbSet");

            migrationBuilder.DropColumn(
                name: "ProfileKey",
                table: "WhatsAppSessionDbSet");

            migrationBuilder.DropColumn(
                name: "SessionId",
                table: "WhatsAppOutboundMessageDbSet");

            migrationBuilder.DropColumn(
                name: "SessionId",
                table: "WhatsAppInboundMessageDbSet");

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppOutboundMessageDbSet_Status_ScheduledFor",
                table: "WhatsAppOutboundMessageDbSet",
                columns: new[] { "Status", "ScheduledFor" });
        }
    }
}
