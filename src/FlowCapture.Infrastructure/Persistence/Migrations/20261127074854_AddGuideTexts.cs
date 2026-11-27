using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowCapture.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGuideTexts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TextsJson",
                table: "DocumentJobRuns",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TextsJson",
                table: "DocumentJobRuns");
        }
    }
}
