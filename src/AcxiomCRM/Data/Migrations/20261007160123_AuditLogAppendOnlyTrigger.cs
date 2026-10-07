using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AcxiomCRM.Data.Migrations
{
    /// <inheritdoc />
    public partial class AuditLogAppendOnlyTrigger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Database-level protection for the audit trail (spec 4.9): even direct SQL or bulk
            // updates cannot change or remove audit rows. Inserts are unaffected.
            // EXEC(...) keeps CREATE TRIGGER first in its own batch, so this also works
            // inside the idempotent script produced by "dotnet ef migrations script --idempotent".
            migrationBuilder.Sql("""
                EXEC(N'CREATE TRIGGER [TR_AuditLogs_AppendOnly] ON [AuditLogs]
                INSTEAD OF UPDATE, DELETE
                AS
                BEGIN
                    THROW 50001, ''Audit log entries are append-only and cannot be modified or deleted.'', 1;
                END')
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [TR_AuditLogs_AppendOnly];");
        }
    }
}
