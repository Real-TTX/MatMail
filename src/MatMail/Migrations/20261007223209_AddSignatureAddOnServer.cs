using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatMail.Migrations
{
    /// <inheritdoc />
    public partial class AddSignatureAddOnServer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AddOnServer",
                table: "Signature",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AddOnServer",
                table: "Signature");
        }
    }
}
