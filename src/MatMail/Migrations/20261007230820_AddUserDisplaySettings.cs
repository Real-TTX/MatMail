using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatMail.Migrations
{
    /// <inheritdoc />
    public partial class AddUserDisplaySettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Density",
                table: "User",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShowPreviews",
                table: "User",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "TextSize",
                table: "User",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TimeZone",
                table: "User",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Density",
                table: "User");

            migrationBuilder.DropColumn(
                name: "ShowPreviews",
                table: "User");

            migrationBuilder.DropColumn(
                name: "TextSize",
                table: "User");

            migrationBuilder.DropColumn(
                name: "TimeZone",
                table: "User");
        }
    }
}
