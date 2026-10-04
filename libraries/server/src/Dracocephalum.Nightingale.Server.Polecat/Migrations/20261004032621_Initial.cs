using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dracocephalum.Nightingale.Server.Polecat.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "nightingale");

            migrationBuilder.CreateTable(
                name: "Lease",
                schema: "nightingale",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    Owner = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    OwnerAddress = table.Column<string>(type: "varchar(500)", unicode: false, maxLength: 500, nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Lease", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutboxEntry",
                schema: "nightingale",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Position = table.Column<long>(type: "bigint", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Ordinal = table.Column<long>(type: "bigint", nullable: true),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    QueuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxEntry", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ParkedEvent",
                schema: "nightingale",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Position = table.Column<long>(type: "bigint", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Ordinal = table.Column<long>(type: "bigint", nullable: true),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    ParkedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParkedEvent", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProgressMark",
                schema: "nightingale",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    Position = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProgressMark", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Setting",
                schema: "nightingale",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Setting", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SubscriptionGroup",
                schema: "nightingale",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<string>(type: "varchar(250)", unicode: false, maxLength: 250, nullable: false),
                    Stream = table.Column<string>(type: "varchar(250)", unicode: false, maxLength: 250, nullable: false),
                    Name = table.Column<string>(type: "varchar(250)", unicode: false, maxLength: 250, nullable: false),
                    Settings = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CheckpointPosition = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriptionGroup", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Lease_Name",
                schema: "nightingale",
                table: "Lease",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxEntry_GroupId_Position",
                schema: "nightingale",
                table: "OutboxEntry",
                columns: new[] { "GroupId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ParkedEvent_GroupId_Position",
                schema: "nightingale",
                table: "ParkedEvent",
                columns: new[] { "GroupId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProgressMark_Name",
                schema: "nightingale",
                table: "ProgressMark",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Setting_Name",
                schema: "nightingale",
                table: "Setting",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionGroup_TenantId_Stream_Name",
                schema: "nightingale",
                table: "SubscriptionGroup",
                columns: new[] { "TenantId", "Stream", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Lease",
                schema: "nightingale");

            migrationBuilder.DropTable(
                name: "OutboxEntry",
                schema: "nightingale");

            migrationBuilder.DropTable(
                name: "ParkedEvent",
                schema: "nightingale");

            migrationBuilder.DropTable(
                name: "ProgressMark",
                schema: "nightingale");

            migrationBuilder.DropTable(
                name: "Setting",
                schema: "nightingale");

            migrationBuilder.DropTable(
                name: "SubscriptionGroup",
                schema: "nightingale");
        }
    }
}
