using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatMail.Migrations
{
    /// <inheritdoc />
    public partial class AddReadingPaneAndConversations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ConversationView",
                table: "User",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ReadingPane",
                table: "User",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConversationView",
                table: "User");

            migrationBuilder.DropColumn(
                name: "ReadingPane",
                table: "User");
        }
    }
}
