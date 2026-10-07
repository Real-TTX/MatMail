using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MatMail.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantBranding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Slug",
                table: "Tenant",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TenantBranding",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    BrandName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Website = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    AccentColor = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    Logo = table.Column<byte[]>(type: "bytea", nullable: true),
                    LogoContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LogoToken = table.Column<Guid>(type: "uuid", nullable: true),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantBranding", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantBranding_Tenant_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenant",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tenant_Slug",
                table: "Tenant",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantBranding_LogoToken",
                table: "TenantBranding",
                column: "LogoToken");

            migrationBuilder.CreateIndex(
                name: "IX_TenantBranding_TenantId",
                table: "TenantBranding",
                column: "TenantId",
                unique: true);

            // Existing tenants: the built-in Administrator role holds every permission, so it gets the new one as well.
            migrationBuilder.Sql(
                "UPDATE \"Role\" SET \"Permissions\" = array_append(\"Permissions\", 'branding.manage') " +
                "WHERE \"Name\" = 'Administrator' AND NOT ('branding.manage' = ANY(\"Permissions\"))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantBranding");

            migrationBuilder.DropIndex(
                name: "IX_Tenant_Slug",
                table: "Tenant");

            migrationBuilder.DropColumn(
                name: "Slug",
                table: "Tenant");
        }
    }
}
