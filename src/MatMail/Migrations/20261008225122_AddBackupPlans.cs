using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MatMail.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BackupTarget",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Share = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Domain = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Username = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    PasswordProtected = table.Column<string>(type: "text", nullable: true),
                    LastCheckDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastCheckOk = table.Column<bool>(type: "boolean", nullable: true),
                    LastCheckMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupTarget", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BackupPlan",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    TargetId = table.Column<long>(type: "bigint", nullable: false),
                    Frequency = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    EveryHours = table.Column<int>(type: "integer", nullable: false),
                    MinuteOfDay = table.Column<int>(type: "integer", nullable: false),
                    DayOfWeek = table.Column<int>(type: "integer", nullable: false),
                    DayOfMonth = table.Column<int>(type: "integer", nullable: false),
                    KeepLast = table.Column<int>(type: "integer", nullable: false),
                    KeepDaily = table.Column<int>(type: "integer", nullable: false),
                    KeepWeekly = table.Column<int>(type: "integer", nullable: false),
                    KeepMonthly = table.Column<int>(type: "integer", nullable: false),
                    Encrypt = table.Column<bool>(type: "boolean", nullable: false),
                    PassphraseProtected = table.Column<string>(type: "text", nullable: true),
                    Verify = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    NotifyOnFailure = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    NextRunDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastRunDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastStatus = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupPlan", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BackupPlan_BackupTarget_TargetId",
                        column: x => x.TargetId,
                        principalTable: "BackupTarget",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BackupRun",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PlanId = table.Column<long>(type: "bigint", nullable: true),
                    PlanName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TargetId = table.Column<long>(type: "bigint", nullable: true),
                    TargetName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    StartedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FileName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Bytes = table.Column<long>(type: "bigint", nullable: true),
                    Encrypted = table.Column<bool>(type: "boolean", nullable: false),
                    Tables = table.Column<int>(type: "integer", nullable: true),
                    Rows = table.Column<long>(type: "bigint", nullable: true),
                    Files = table.Column<int>(type: "integer", nullable: true),
                    Pruned = table.Column<int>(type: "integer", nullable: false),
                    Message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    AppVersion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupRun", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BackupRun_BackupPlan_PlanId",
                        column: x => x.PlanId,
                        principalTable: "BackupPlan",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BackupPlan_Name",
                table: "BackupPlan",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackupPlan_NextRunDate",
                table: "BackupPlan",
                column: "NextRunDate");

            migrationBuilder.CreateIndex(
                name: "IX_BackupPlan_TargetId",
                table: "BackupPlan",
                column: "TargetId");

            migrationBuilder.CreateIndex(
                name: "IX_BackupRun_PlanId",
                table: "BackupRun",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_BackupRun_StartedDate",
                table: "BackupRun",
                column: "StartedDate");

            migrationBuilder.CreateIndex(
                name: "IX_BackupTarget_Name",
                table: "BackupTarget",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BackupRun");

            migrationBuilder.DropTable(
                name: "BackupPlan");

            migrationBuilder.DropTable(
                name: "BackupTarget");
        }
    }
}
