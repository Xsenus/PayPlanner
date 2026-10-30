using System.Data.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class LegalEntitiesControllerTests
{
    [Fact]
    public async Task ProfileEdit_TwoSqliteConnectionsPreserveTheWinningForm()
    {
        await using var source = await CreateContextAsync();
        var fixture = await ConcurrentProfileFixture(source);
        var opened = await ReadConcurrentProfile(fixture.Controller, fixture.Id);
        var connectionString = $"Data Source=profile_edit_{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=1;Pooling=False";
        await using var anchor = new SqliteConnection(connectionString); await anchor.OpenAsync();
        ((SqliteConnection)source.Database.GetDbConnection()).BackupDatabase(anchor);
        var barrier = new ProfileEditReadBarrier();
        await using var first = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connectionString).AddInterceptors(barrier).Options);
        await using var second = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connectionString).Options);
        await AssertProfileWritersConflict(first, second, opened, barrier);
    }

    [Fact]
    public async Task ProfileEdit_PostgreSqlTwoConnectionsAndTimestampPrecisionPreserveTheWinner()
    {
        await using var template = new PostgreSqlModelTemplate();
        await template.InitializeAsync();
        await using var database = await template.CreateDatabaseAsync();
        await using var seed = ProfileEditPgContext(database.ConnectionString);
        var fixture = await ConcurrentProfileFixture(seed);
        var opened = await ReadConcurrentProfile(fixture.Controller, fixture.Id);
        var barrier = new ProfileEditReadBarrier();
        await using var first = ProfileEditPgContext(database.ConnectionString, barrier);
        await using var second = ProfileEditPgContext(database.ConnectionString);
        await AssertProfileWritersConflict(first, second, opened, barrier);
        var userId = await second.Users.AsNoTracking().Select(row => row.Id).SingleAsync();
        var current = await ReadConcurrentProfile(CreateController(second, userId), fixture.Id);
        // The saved in-memory DateTime may have a seventh decimal; a fresh provider
        // materialization must still accept exactly the response's version.
        var reviewed = ConcurrentProfileRequest(current); reviewed.Phone = "222";
        var saved = Assert.IsType<PayPlanner.Api.Models.Responses.LegalEntityDetailDto>(Assert.IsType<OkObjectResult>((await CreateController(second, userId).Update(fixture.Id, reviewed, default)).Result).Value);
        await using var fresh = ProfileEditPgContext(database.ConnectionString);
        var repeat = ConcurrentProfileRequest(saved); repeat.Phone = "333";
        Assert.IsType<OkObjectResult>((await CreateController(fresh, userId).Update(fixture.Id, repeat, default)).Result);
        Assert.Equal("333", (await fresh.LegalEntities.AsNoTracking().SingleAsync()).Phone);
    }

    private static PostgreSqlPaymentContext ProfileEditPgContext(string connection, ProfileEditReadBarrier? barrier = null)
    {
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connection);
        if (barrier is not null) options.AddInterceptors(barrier);
        return new PostgreSqlPaymentContext(options.Options);
    }

    [Fact]
    public async Task ProfileEdit_PostgreSqlConcurrentAddressChangeRollsBackStaleReplacement()
    {
        await using var template = new PostgreSqlModelTemplate();
        await template.InitializeAsync();
        await using var database = await template.CreateDatabaseAsync();
        await using var writer = ProfileEditPgContext(database.ConnectionString);
        var fixture = await ConcurrentProfileFixture(writer);
        writer.AddressRecords.Add(new AddressRecord { LegalEntityId = fixture.Id, Value = "Opened address" });
        await writer.SaveChangesAsync();
        var opened = await ReadConcurrentProfile(fixture.Controller, fixture.Id);
        var userId = await writer.Users.Select(row => row.Id).SingleAsync();
        var barrier = new ProfileEditReadBarrier();
        await using var stale = ProfileEditPgContext(database.ConnectionString, barrier);
        var request = ConcurrentProfileRequest(opened); request.Phone = "Must rollback";
        request.Addresses = [new() { Value = "Old form address" }];
        barrier.Enabled = true;
        var pending = CreateController(stale, userId).Update(fixture.Id, request, default);
        try
        {
            await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await writer.AddressRecords.Where(row => row.LegalEntityId == fixture.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Value, "Concurrent address"));
        }
        finally { barrier.Release.TrySetResult(true); }
        Assert.IsType<ConflictObjectResult>((await pending.WaitAsync(TimeSpan.FromSeconds(20))).Result);
        Assert.Equal("000", (await writer.LegalEntities.AsNoTracking().SingleAsync()).Phone);
        Assert.Equal("Concurrent address", (await writer.AddressRecords.AsNoTracking().SingleAsync()).Value);
        Assert.Empty(stale.ChangeTracker.Entries()); Assert.Null(stale.Database.CurrentTransaction);
    }

    private static async Task AssertProfileWritersConflict(PaymentContext first, PaymentContext second,
        PayPlanner.Api.Models.Responses.LegalEntityDetailDto opened, ProfileEditReadBarrier barrier)
    {
        var userId = await first.Users.AsNoTracking().Select(row => row.Id).SingleAsync();
        var winner = ConcurrentProfileRequest(opened); winner.Phone = "111";
        var loser = ConcurrentProfileRequest(opened); loser.FullName = "Stale writer";
        barrier.Enabled = true;
        var pending = CreateController(first, userId).Update(opened.Id, winner, default);
        try
        {
            await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.IsType<ConflictObjectResult>((await CreateController(second, userId).Update(opened.Id, loser, default).WaitAsync(TimeSpan.FromSeconds(10))).Result);
            Assert.Null(second.Database.CurrentTransaction);
        }
        finally { barrier.Release.TrySetResult(true); }
        Assert.IsType<OkObjectResult>((await pending.WaitAsync(TimeSpan.FromSeconds(20))).Result);
        Assert.Equal(1, barrier.Pauses);
        var stored = await second.LegalEntities.AsNoTracking().SingleAsync();
        Assert.Equal("111", stored.Phone); Assert.Equal(opened.FullName, stored.FullName);
        Assert.Null(first.Database.CurrentTransaction);
        Assert.IsType<ConflictObjectResult>((await CreateController(second, userId).Update(opened.Id, loser, default)).Result);
    }

    private sealed class ProfileEditReadBarrier : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public int Pauses { get; private set; }
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context?.Database.CurrentTransaction is not null
                && command.CommandText.Contains("LegalEntities", StringComparison.Ordinal)
                && command.CommandText.Contains("AddressRecords", StringComparison.Ordinal))
            {
                Enabled = false; Pauses++; Entered.TrySetResult(true);
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(25), cancellationToken);
            }
            return result;
        }
    }
}
