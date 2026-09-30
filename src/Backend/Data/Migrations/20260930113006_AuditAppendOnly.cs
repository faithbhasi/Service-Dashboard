using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServiceDashboard.Data.Migrations
{
    /// <inheritdoc />
    public partial class AuditAppendOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The audit table is append-only: rows can never be edited. (Old rows are removed by the retention job only.)
            migrationBuilder.Sql(@"CREATE TRIGGER AuditLogs_NoUpdate BEFORE UPDATE ON AuditLogs
BEGIN SELECT RAISE(ABORT, 'AuditLogs is append-only'); END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS AuditLogs_NoUpdate;");
        }
    }
}
