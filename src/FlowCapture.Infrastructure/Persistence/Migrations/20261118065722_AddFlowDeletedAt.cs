using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowCapture.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFlowDeletedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "Flows",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "Flows");
        }
    }
}
