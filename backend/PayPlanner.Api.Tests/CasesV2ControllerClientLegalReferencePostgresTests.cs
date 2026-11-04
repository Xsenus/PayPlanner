using System.Data.Common;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    public static IEnumerable<object[]> LegalReferenceSnapshotCases() => from route in new[] { 1, 7 } from target in new[] { "assignment", "owner", "registry" } select new object[] { route, target };

    [Theory]
    [MemberData(nameof(LegalReferenceSnapshotCases))]
    public async Task ClientLegalReference_PgParentAccessAndFieldsShareTheAdmittedSnapshot(int route, string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options);
        await using var setup = Open(); var seed = await SeedLegalReferenceAsync(setup, target == "assignment" ? "own-assigned" : "visible");
        if (target == "owner")
        {
            await setup.RolePermissions.Where(x => x.RoleId == seed.Actor.RoleId && x.Section == RolePermissionCatalog.LegalEntities).ExecuteUpdateAsync(s => s.SetProperty(x => x.ViewScope, DataVisibilityScope.Own));
            await setup.LegalEntities.Where(x => x.Id == seed.Legal.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResponsibleUserId, seed.Actor.Id)); seed.Legal.ResponsibleUserId = seed.Actor.Id;
        }
        var committed = false;
        var hook = new ClientReadBeforeRows(async ct =>
        {
            await using var writer = Open(); await using var tx = await writer.Database.BeginTransactionAsync(ct); await writer.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '250ms'", ct);
            if (target == "assignment") Assert.Equal(1, await writer.UserCompanyAccesses.Where(x => x.UserId == seed.Actor.Id && x.LegalEntityId == seed.Legal.Id).ExecuteDeleteAsync(ct));
            if (target == "owner") Assert.Equal(1, await writer.LegalEntities.Where(x => x.Id == seed.Legal.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResponsibleUserId, seed.Other.Id), ct));
            if (target == "registry") { writer.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{seed.Legal.Id}]" }); await writer.SaveChangesAsync(ct); }
            await tx.CommitAsync(ct); committed = true;
        });
        await using var request = Open(hook); var result = Assert.IsType<OkObjectResult>(await ReadClientBoundaryAsync(request, seed.Actor, seed.Client, route));
        Assert.True(hook.Called && committed); var json = JsonSerializer.SerializeToElement(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)); AssertLegalReferenceJson(LegalReferenceItem(json, route, seed.Client), seed, "visible", route); Assert.Null(request.Database.CurrentTransaction);
        await using var next = Open(); result = Assert.IsType<OkObjectResult>(await ReadClientBoundaryAsync(next, seed.Actor, seed.Client, route)); json = JsonSerializer.SerializeToElement(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)); AssertLegalReferenceJson(LegalReferenceItem(json, route, seed.Client), seed, "hidden", route);
    }

    public static IEnumerable<object[]> LegalReferencePermissionPinCases() => from route in new[] { 1, 7 } from profile in new[] { false, true } select new object[] { route, profile };

    [Theory]
    [MemberData(nameof(LegalReferencePermissionPinCases))]
    public async Task ClientLegalReference_PgPinsNewlyConsumedLegalAndProfilePermissions(int route, bool profile)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options);
        await using var setup = Open(); var seed = await SeedLegalReferenceAsync(setup, profile ? "own-assigned" : "visible"); var section = profile ? RolePermissionCatalog.CompanyProfile : RolePermissionCatalog.LegalEntities; var blocked = false;
        var hook = new ClientReadBeforeRows(async ct =>
        {
            await using var writer = Open(); await using var tx = await writer.Database.BeginTransactionAsync(ct); await writer.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '250ms'", ct);
            try { await writer.RolePermissions.Where(x => x.RoleId == seed.Actor.RoleId && x.Section == section).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanView, false), ct); await tx.CommitAsync(ct); }
            catch (Exception error) { Exception? cause = error; while (cause is not null && cause is not PostgresException) cause = cause.InnerException; if (cause is not PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable }) throw; blocked = true; }
        });
        await using var request = Open(hook); var result = Assert.IsType<OkObjectResult>(await ReadClientBoundaryAsync(request, seed.Actor, seed.Client, route)); Assert.True(hook.Called && blocked);
        var json = JsonSerializer.SerializeToElement(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)); AssertLegalReferenceJson(LegalReferenceItem(json, route, seed.Client), seed, "visible", route);
        await using var next = Open(); Assert.Equal(1, await next.RolePermissions.Where(x => x.RoleId == seed.Actor.RoleId && x.Section == section).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanView, false)));
        result = Assert.IsType<OkObjectResult>(await ReadClientBoundaryAsync(next, seed.Actor, seed.Client, route)); json = JsonSerializer.SerializeToElement(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)); AssertLegalReferenceJson(LegalReferenceItem(json, route, seed.Client), seed, "hidden", route);
    }

    private sealed class LegalReferenceBeforeProjection(Func<CancellationToken, Task> action) : DbCommandInterceptor
    {
        public bool Called { get; private set; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (!Called && command.CommandText.Contains("FROM \"LegalEntities\"", StringComparison.Ordinal) && command.CommandText.Contains("\"BankAccountsJson\"", StringComparison.Ordinal)) { Called = true; await action(ct); }
            return result;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientLegalReference_PgRootSavePinsAssignmentUntilSafeResponseIsBuilt(bool create)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options);
        await using var setup = Open(); var seed = await SeedLegalReferenceAsync(setup, "own-assigned"); var blocked = false;
        var hook = new LegalReferenceBeforeProjection(async ct =>
        {
            await using var writer = Open(); await using var tx = await writer.Database.BeginTransactionAsync(ct); await writer.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '250ms'", ct);
            try { await writer.UserCompanyAccesses.Where(x => x.UserId == seed.Actor.Id && x.LegalEntityId == seed.Legal.Id).ExecuteDeleteAsync(ct); await tx.CommitAsync(ct); }
            catch (Exception error) { Exception? cause = error; while (cause is not null && cause is not PostgresException) cause = cause.InnerException; if (cause is not PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable }) throw; blocked = true; }
        });
        await using var request = Open(hook); var input = new Client { Name = "Безопасный ответ сохранения", LegalEntityId = seed.Legal.Id, ResponsibleUserId = seed.Actor.Id };
        var result = await WriteRequiredClientAsync(request, seed.Actor, seed.Client, input, 0, create); var value = create ? Assert.IsType<CreatedResult>(result).Value : Assert.IsType<OkObjectResult>(result).Value;
        Assert.True(hook.Called && blocked); var client = Assert.IsType<Client>(value); var json = JsonSerializer.SerializeToElement(client, new JsonSerializerOptions(JsonSerializerDefaults.Web)); AssertLegalReferenceJson(json, seed, "own-assigned", 9); Assert.Null(request.Database.CurrentTransaction);
        await using var next = Open(); Assert.Equal(1, await next.UserCompanyAccesses.Where(x => x.UserId == seed.Actor.Id && x.LegalEntityId == seed.Legal.Id).ExecuteDeleteAsync());
        var read = Assert.IsType<Client>(Assert.IsType<OkObjectResult>(await ReadClientBoundaryAsync(next, seed.Actor, client.Id, 1)).Value); Assert.Equal(seed.Legal.Id, read.LegalEntityId); Assert.Null(read.LegalEntity);
    }
}
