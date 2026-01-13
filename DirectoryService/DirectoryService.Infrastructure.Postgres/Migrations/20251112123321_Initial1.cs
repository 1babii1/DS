using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DirectoryService.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Initial1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DepartmentsId",
                table: "departments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_departments_DepartmentsId",
                table: "departments",
                column: "DepartmentsId");

            migrationBuilder.AddForeignKey(
                name: "FK_departments_departments_DepartmentsId",
                table: "departments",
                column: "DepartmentsId",
                principalTable: "departments",
                principalColumn: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_departments_departments_DepartmentsId",
                table: "departments");

            migrationBuilder.DropIndex(
                name: "IX_departments_DepartmentsId",
                table: "departments");

            migrationBuilder.DropColumn(
                name: "DepartmentsId",
                table: "departments");
        }
    }
}
