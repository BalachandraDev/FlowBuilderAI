using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowCapture.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCombinedGuideDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ProjectDepartmentId",
                table: "GeneratedDocuments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProjectDepartmentId",
                table: "DocumentJobRuns",
                type: "uniqueidentifier",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProjectDepartmentId",
                table: "GeneratedDocuments");

            migrationBuilder.DropColumn(
                name: "ProjectDepartmentId",
                table: "DocumentJobRuns");
        }
    }
}
