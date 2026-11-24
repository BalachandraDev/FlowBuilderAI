using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowCapture.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Height",
                table: "DiagramAssets",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Width",
                table: "DiagramAssets",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "DocumentJobRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    FlowId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FlowIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AssignedBy = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    IncludeDiagrams = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ErrorCode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    ErrorDetail = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RequestedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResultDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ResultVersionNo = table.Column<int>(type: "int", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentJobRuns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentJobRuns_FlowId_Status",
                table: "DocumentJobRuns",
                columns: new[] { "FlowId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentJobRuns_ProjectId_CreatedAt",
                table: "DocumentJobRuns",
                columns: new[] { "ProjectId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocumentJobRuns");

            migrationBuilder.DropColumn(
                name: "Height",
                table: "DiagramAssets");

            migrationBuilder.DropColumn(
                name: "Width",
                table: "DiagramAssets");
        }
    }
}
