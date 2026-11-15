using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppLab.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPacingSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "JitterSeconds",
                table: "WhatsAppSettingDbSet",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxPerWindow",
                table: "WhatsAppSettingDbSet",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SendFromHour",
                table: "WhatsAppSettingDbSet",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SendToHour",
                table: "WhatsAppSettingDbSet",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WindowMinutes",
                table: "WhatsAppSettingDbSet",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "JitterSeconds",
                table: "WhatsAppSettingDbSet");

            migrationBuilder.DropColumn(
                name: "MaxPerWindow",
                table: "WhatsAppSettingDbSet");

            migrationBuilder.DropColumn(
                name: "SendFromHour",
                table: "WhatsAppSettingDbSet");

            migrationBuilder.DropColumn(
                name: "SendToHour",
                table: "WhatsAppSettingDbSet");

            migrationBuilder.DropColumn(
                name: "WindowMinutes",
                table: "WhatsAppSettingDbSet");
        }
    }
}
