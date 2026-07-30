using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;
using PostgreSqlMigration =
    PayPlanner.Api.Migrations.PostgreSql.AddTaskInactivityControlCycleStatePostgres;
using SqliteMigration =
    PayPlanner.Api.Migrations.AddTaskInactivityControlCycleState;

namespace PayPlanner.Api.Tests;

public sealed class TaskInactivityControlMigrationTests
{
    [Fact]
    public void ProviderMigrations_CreateEquivalentCycleStateAndLookupIndex()
    {
        AssertOperations(new TestableSqliteMigration().GetUpOperations("Microsoft.EntityFrameworkCore.Sqlite"));
        AssertOperations(new TestablePostgreSqlMigration().GetUpOperations("Npgsql.EntityFrameworkCore.PostgreSQL"));
    }

    private static void AssertOperations(IReadOnlyList<MigrationOperation> operations)
    {
        Assert.Collection(
            operations,
            operation => AssertNullableColumn<int>(operation, "InactivityControlCycleNumber"),
            operation => AssertNullableColumn<DateTime>(operation, "InactivityControlNextCheckAt"),
            operation =>
            {
                var index = Assert.IsType<CreateIndexOperation>(operation);
                Assert.Equal("IX_TaskJournalItems_InactivityControlNextCheckAt", index.Name);
                Assert.Equal("TaskJournalItems", index.Table);
                Assert.Equal(["InactivityControlNextCheckAt"], index.Columns);
                Assert.False(index.IsUnique);
            });
    }

    private static void AssertNullableColumn<T>(MigrationOperation operation, string columnName)
    {
        var column = Assert.IsType<AddColumnOperation>(operation);
        Assert.Equal("TaskJournalItems", column.Table);
        Assert.Equal(columnName, column.Name);
        Assert.Equal(typeof(T), column.ClrType);
        Assert.True(column.IsNullable);
    }

    private sealed class TestableSqliteMigration : SqliteMigration
    {
        public IReadOnlyList<MigrationOperation> GetUpOperations(string activeProvider)
        {
            var builder = new MigrationBuilder(activeProvider);
            Up(builder);
            return builder.Operations;
        }
    }

    private sealed class TestablePostgreSqlMigration : PostgreSqlMigration
    {
        public IReadOnlyList<MigrationOperation> GetUpOperations(string activeProvider)
        {
            var builder = new MigrationBuilder(activeProvider);
            Up(builder);
            return builder.Operations;
        }
    }
}
