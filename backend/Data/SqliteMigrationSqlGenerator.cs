using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace PayPlanner.Api.Data;

public sealed class PayPlannerSqliteMigrationsSqlGenerator(
    MigrationsSqlGeneratorDependencies dependencies,
    IRelationalAnnotationProvider migrationsAnnotations)
    : SqliteMigrationsSqlGenerator(dependencies, migrationsAnnotations)
{
    protected override void Generate(
        AddForeignKeyOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true)
    {
    }

    protected override void Generate(
        DropForeignKeyOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true)
    {
    }
}

public static class SqliteMigrationSqlGeneratorOptionsBuilderExtensions
{
    public static DbContextOptionsBuilder UsePayPlannerSqliteMigrations(this DbContextOptionsBuilder options)
        => options.ReplaceService<IMigrationsSqlGenerator, PayPlannerSqliteMigrationsSqlGenerator>();
}
