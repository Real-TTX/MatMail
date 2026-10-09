using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MatMail.Migrations
{
    /// <inheritdoc />
    public partial class AddMailRulesAndTransferLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MailRule",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    MailboxId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Match = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    StopProcessing = table.Column<bool>(type: "boolean", nullable: false),
                    MatchCount = table.Column<long>(type: "bigint", nullable: false),
                    LastMatchDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailRule", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MailRule_Mailbox_MailboxId",
                        column: x => x.MailboxId,
                        principalTable: "Mailbox",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MailRule_Tenant_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenant",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MailTransfer",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: true),
                    Direction = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Channel = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    MessageIdHeader = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Subject = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Sender = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Recipients = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    RecipientCount = table.Column<int>(type: "integer", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Peer = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    RemoteIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Detail = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    OutboundMessageId = table.Column<long>(type: "bigint", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailTransfer", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MailRuleAction",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    RuleId = table.Column<long>(type: "bigint", nullable: false),
                    Type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    FolderId = table.Column<long>(type: "bigint", nullable: true),
                    Value = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailRuleAction", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MailRuleAction_MailFolder_FolderId",
                        column: x => x.FolderId,
                        principalTable: "MailFolder",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MailRuleAction_MailRule_RuleId",
                        column: x => x.RuleId,
                        principalTable: "MailRule",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MailRuleAction_Tenant_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenant",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MailRuleCondition",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    RuleId = table.Column<long>(type: "bigint", nullable: false),
                    Field = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Operator = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Value = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    HeaderName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailRuleCondition", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MailRuleCondition_MailRule_RuleId",
                        column: x => x.RuleId,
                        principalTable: "MailRule",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MailRuleCondition_Tenant_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenant",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MailRule_MailboxId_Position",
                table: "MailRule",
                columns: new[] { "MailboxId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_MailRule_TenantId",
                table: "MailRule",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_MailRuleAction_FolderId",
                table: "MailRuleAction",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_MailRuleAction_RuleId",
                table: "MailRuleAction",
                column: "RuleId");

            migrationBuilder.CreateIndex(
                name: "IX_MailRuleAction_TenantId",
                table: "MailRuleAction",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_MailRuleCondition_RuleId",
                table: "MailRuleCondition",
                column: "RuleId");

            migrationBuilder.CreateIndex(
                name: "IX_MailRuleCondition_TenantId",
                table: "MailRuleCondition",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_MailTransfer_CreateDate",
                table: "MailTransfer",
                column: "CreateDate");

            migrationBuilder.CreateIndex(
                name: "IX_MailTransfer_Direction_Status",
                table: "MailTransfer",
                columns: new[] { "Direction", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MailTransfer_OutboundMessageId",
                table: "MailTransfer",
                column: "OutboundMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_MailTransfer_TenantId_CreateDate",
                table: "MailTransfer",
                columns: new[] { "TenantId", "CreateDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MailRuleAction");

            migrationBuilder.DropTable(
                name: "MailRuleCondition");

            migrationBuilder.DropTable(
                name: "MailTransfer");

            migrationBuilder.DropTable(
                name: "MailRule");
        }
    }
}
