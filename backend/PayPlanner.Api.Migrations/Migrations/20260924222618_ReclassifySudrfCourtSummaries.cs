using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayPlanner.Api.Migrations
{
    /// <inheritdoc />
    public partial class ReclassifySudrfCourtSummaries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE ClientCaseDocuments
                SET DocumentType = 'Сводка судебного события'
                WHERE SourceType = 'sudrf'
                  AND DocumentKey LIKE 'sudrf-act-%'
                  AND DocumentType = 'Судебный акт';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Do not relabel source summaries as published court acts on rollback.
        }
    }
}
