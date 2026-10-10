using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatMail.Migrations
{
    /// <inheritdoc />
    public partial class AddMailboxQuota : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "QuotaBytes",
                table: "Mailbox",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QuotaBytes",
                table: "Mailbox");
        }
    }
}
