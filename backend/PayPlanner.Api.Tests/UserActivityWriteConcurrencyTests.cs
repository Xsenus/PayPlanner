using System.Data.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.UserActivity;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class UserActivityWriteConcurrencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlowContextOrConnectionPreparationDoesNotBlockAnotherAuditWrite(bool slowFactory)
    {
        var connectionString = $"Data Source=pp_activity_concurrency_{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
        await using var keeper = new SqliteConnection(connectionString);
        await keeper.OpenAsync();
        await using (var template = await SqliteTestContextFactory.CreateAsync())
            ((SqliteConnection)template.Database.GetDbConnection()).BackupDatabase(keeper);
        var gate = new PreparationGate();
        var logger = new ErrorCounter();
        var fastFactory = new Factory(connectionString);
        var fast = new UserActivityService(fastFactory, new HttpContextAccessor(), logger);
        await fast.UpdateSettingsAsync(true, 30);
        var slow = new UserActivityService(new Factory(connectionString, gate, slowFactory), new HttpContextAccessor(), logger);
        var slowWrite = slow.TryWriteAsync(new ActivityLogEntry { Category = "owned", Action = "slow" });
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var fastLog = await fast.TryWriteAsync(new ActivityLogEntry { Category = "owned", Action = "fast" }, deadline.Token);
            Assert.NotNull(fastLog);
            Assert.False(slowWrite.IsCompleted);
            Assert.Equal(0, logger.Errors);
        }
        finally
        {
            gate.Released.TrySetResult();
            await slowWrite.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.NotNull(await slowWrite);
        await using var proof = fastFactory.CreateDbContext();
        var logs = await proof.UserActivityLogs.OrderBy(log => log.Id).ToArrayAsync();
        Assert.Equal(new[] { "fast", "slow" }, logs.Select(log => log.Action));
        Assert.Equal(2, logs.Select(log => log.Id).Distinct().Count());
        Assert.Equal(0, logger.Errors);
    }

    private sealed class PreparationGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Released.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class Factory(string connectionString, PreparationGate? gate = null, bool slowFactory = false)
        : IDbContextFactory<PaymentContext>
    {
        public PaymentContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connectionString);
            if (gate is not null && !slowFactory) options.AddInterceptors(new OpeningGate(gate));
            return new PaymentContext(options.Options);
        }
        public async Task<PaymentContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            if (gate is not null && slowFactory) await gate.WaitAsync(cancellationToken);
            return CreateDbContext();
        }
    }

    private sealed class OpeningGate(PreparationGate gate) : DbConnectionInterceptor
    {
        public override async ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            await gate.WaitAsync(cancellationToken);
            return result;
        }
    }

    private sealed class ErrorCounter : ILogger<UserActivityService>
    {
        private int _errors;
        public int Errors => Volatile.Read(ref _errors);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        { if (logLevel >= LogLevel.Error) Interlocked.Increment(ref _errors); }
    }
}
