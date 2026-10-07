using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MatMail.Migrations
{
    /// <inheritdoc />
    public partial class AddTwoFactor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TwoFactorMode",
                table: "Tenant",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "Optional");

            migrationBuilder.AddColumn<bool>(
                name: "RequiresTwoFactor",
                table: "Role",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "AppPassword",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Token = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Prefix = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    SecretHash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    LastUsedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastUsedIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppPassword", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppPassword_User_UserId",
                        column: x => x.UserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserRecoveryCode",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    CodeHash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    UsedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRecoveryCode", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserRecoveryCode_User_UserId",
                        column: x => x.UserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserTotp",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    Secret = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    ConfirmedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastUsedStep = table.Column<long>(type: "bigint", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserTotp", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserTotp_User_UserId",
                        column: x => x.UserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppPassword_Token",
                table: "AppPassword",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppPassword_UserId_Prefix",
                table: "AppPassword",
                columns: new[] { "UserId", "Prefix" });

            migrationBuilder.CreateIndex(
                name: "IX_UserRecoveryCode_UserId",
                table: "UserRecoveryCode",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserTotp_UserId",
                table: "UserTotp",
                column: "UserId",
                unique: true);

            // Existing tenants: the built-in Administrator role holds every permission, so it gets the new one as well.
            migrationBuilder.Sql(
                "UPDATE \"Role\" SET \"Permissions\" = array_append(\"Permissions\", 'security.manage') " +
                "WHERE \"Name\" = 'Administrator' AND NOT ('security.manage' = ANY(\"Permissions\"))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE \"Role\" SET \"Permissions\" = array_remove(\"Permissions\", 'security.manage')");

            migrationBuilder.DropTable(
                name: "AppPassword");

            migrationBuilder.DropTable(
                name: "UserRecoveryCode");

            migrationBuilder.DropTable(
                name: "UserTotp");

            migrationBuilder.DropColumn(
                name: "TwoFactorMode",
                table: "Tenant");

            migrationBuilder.DropColumn(
                name: "RequiresTwoFactor",
                table: "Role");
        }
    }
}
