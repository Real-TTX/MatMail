using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MatMail.Migrations
{
    /// <inheritdoc />
    public partial class AddMailTemplate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MailTemplate",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Scope = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    MailboxId = table.Column<long>(type: "bigint", nullable: true),
                    UserId = table.Column<long>(type: "bigint", nullable: true),
                    ForWebClient = table.Column<bool>(type: "boolean", nullable: false),
                    ForMailPrograms = table.Column<bool>(type: "boolean", nullable: false),
                    ForSmartHost = table.Column<bool>(type: "boolean", nullable: false),
                    RelayRuleId = table.Column<long>(type: "bigint", nullable: true),
                    Mode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Html = table.Column<string>(type: "text", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailTemplate", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MailTemplate_Mailbox_MailboxId",
                        column: x => x.MailboxId,
                        principalTable: "Mailbox",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MailTemplate_RelayRule_RelayRuleId",
                        column: x => x.RelayRuleId,
                        principalTable: "RelayRule",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MailTemplate_Tenant_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenant",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MailTemplate_User_UserId",
                        column: x => x.UserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MailTemplate_MailboxId",
                table: "MailTemplate",
                column: "MailboxId");

            migrationBuilder.CreateIndex(
                name: "IX_MailTemplate_RelayRuleId",
                table: "MailTemplate",
                column: "RelayRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_MailTemplate_TenantId",
                table: "MailTemplate",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_MailTemplate_UserId",
                table: "MailTemplate",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MailTemplate");
        }
    }
}
