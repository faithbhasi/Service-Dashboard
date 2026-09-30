using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServiceDashboard.Data.Migrations
{
    /// <inheritdoc />
    public partial class RoleAdScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AdScopeJson",
                table: "Roles",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdScopeJson",
                table: "Roles");
        }
    }
}
