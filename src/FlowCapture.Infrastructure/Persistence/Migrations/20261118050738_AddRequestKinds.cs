using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowCapture.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRequestKinds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "Flows",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "NR");

            migrationBuilder.AddColumn<int>(
                name: "RequestNo",
                table: "Flows",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // every workflow that exists today is a New Requirement (NR, the default above); they are numbered 1, 2, 3... inside their module in the
            // order they were made, so the unique index below can be built and nothing is lost or renamed
            migrationBuilder.Sql(@"
UPDATE f SET f.RequestNo = x.rn
FROM Flows f
JOIN (SELECT Id, ROW_NUMBER() OVER (PARTITION BY ProjectDepartmentId ORDER BY CreatedAt, Id) AS rn FROM Flows) x ON x.Id = f.Id;");

            migrationBuilder.CreateIndex(
                name: "IX_Flows_ProjectDepartmentId_Kind_RequestNo",
                table: "Flows",
                columns: new[] { "ProjectDepartmentId", "Kind", "RequestNo" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Flows_ProjectDepartmentId_Kind_RequestNo",
                table: "Flows");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Flows");

            migrationBuilder.DropColumn(
                name: "RequestNo",
                table: "Flows");
        }
    }
}
