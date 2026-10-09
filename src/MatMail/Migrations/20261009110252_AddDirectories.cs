using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MatMail.Migrations
{
    /// <inheritdoc />
    public partial class AddDirectories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DirectoryDisabledDate",
                table: "User",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DirectoryDn",
                table: "User",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DirectoryId",
                table: "User",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DirectoryUid",
                table: "User",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DirectoryConnection",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    Security = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    AllowInvalidCertificate = table.Column<bool>(type: "boolean", nullable: false),
                    BindDn = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    BindPasswordProtected = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    BaseDn = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    UserFilter = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    LoginAttribute = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DisplayNameAttribute = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EmailAttribute = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FirstNameAttribute = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LastNameAttribute = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    JobTitleAttribute = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PhoneAttribute = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    MobileAttribute = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    DepartmentAttribute = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AllowedGroupDn = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    GroupLookup = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    NestedGroups = table.Column<bool>(type: "boolean", nullable: false),
                    CreateUsersOnSignIn = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    DefaultRoleIds = table.Column<long[]>(type: "bigint[]", nullable: false),
                    CreateMailbox = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    LastCheckDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastCheckOk = table.Column<bool>(type: "boolean", nullable: true),
                    LastCheckMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    LastSyncDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSyncOk = table.Column<bool>(type: "boolean", nullable: true),
                    LastSyncMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectoryConnection", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DirectoryConnection_Tenant_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenant",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_User_DirectoryId_DirectoryUid",
                table: "User",
                columns: new[] { "DirectoryId", "DirectoryUid" });

            migrationBuilder.CreateIndex(
                name: "IX_DirectoryConnection_TenantId_Name",
                table: "DirectoryConnection",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_User_DirectoryConnection_DirectoryId",
                table: "User",
                column: "DirectoryId",
                principalTable: "DirectoryConnection",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // The administrators of the tenants that exist get the new permission (a new tenant is given all of them).
            migrationBuilder.Sql(
                "UPDATE \"Role\" SET \"Permissions\" = array_append(\"Permissions\", 'directories.manage') " +
                "WHERE \"Name\" = 'Administrator' AND NOT ('directories.manage' = ANY(\"Permissions\"))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE \"Role\" SET \"Permissions\" = array_remove(\"Permissions\", 'directories.manage')");

            migrationBuilder.DropForeignKey(
                name: "FK_User_DirectoryConnection_DirectoryId",
                table: "User");

            migrationBuilder.DropTable(
                name: "DirectoryConnection");

            migrationBuilder.DropIndex(
                name: "IX_User_DirectoryId_DirectoryUid",
                table: "User");

            migrationBuilder.DropColumn(
                name: "DirectoryDisabledDate",
                table: "User");

            migrationBuilder.DropColumn(
                name: "DirectoryDn",
                table: "User");

            migrationBuilder.DropColumn(
                name: "DirectoryId",
                table: "User");

            migrationBuilder.DropColumn(
                name: "DirectoryUid",
                table: "User");
        }
    }
}
