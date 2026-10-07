using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using PayPlanner.Api.Controllers;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class OwnedTaskQueryTimingObserverTests
{
    [Fact]
    public async Task ActualEfCompilationAddsOnlyNumericalCostToTheOwnedRequest()
    {
        var accessor = new HttpContextAccessor();
        var lines = new List<string>();
        using var diagnostics = new OwnedTaskReadDiagnostics(accessor, lines.Add);
        var observer = new OwnedTaskQueryTimingObserver(accessor, diagnostics);
        await observer.StartAsync(default);
        try
        {
            await using var connection = new SqliteConnection($"Data Source=pp_task_query_observer_{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False");
            await connection.OpenAsync();
            await using var db = new ProbeContext(new DbContextOptionsBuilder<ProbeContext>().UseSqlite(connection.ConnectionString).Options);
            await db.Database.EnsureCreatedAsync();
            var request = new DefaultHttpContext();
            request.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new ControllerActionDescriptor
            {
                ControllerTypeInfo = typeof(TasksController).GetTypeInfo(),
                MethodInfo = typeof(TasksController).GetMethod(nameof(TasksController.GetFileUploads))!
            }), "private-endpoint"));
            accessor.HttpContext = request;
            var privateParameter = "private-query-parameter";
            await db.Set<Probe>().Where(item => item.Name == privateParameter).ToArrayAsync();
            diagnostics.CreateLogger(OwnedTaskReadDiagnostics.ActionCategory).Log(LogLevel.Information, new EventId(105),
                new[] { new KeyValuePair<string, object?>("ElapsedMilliseconds", 1000.0) }, null,
                (_, _) => throw new InvalidOperationException("Formatter must never run."));
            var measurement = OwnedTaskReadDiagnostics.ReadMeasurement(Assert.Single(lines))!;
            Assert.True(measurement.Compilations > 0);
            Assert.InRange(measurement.CompilationMilliseconds, 0, 3_600_000);
            Assert.True(measurement.ObservedCommandMilliseconds > 0);
            Assert.True(measurement.ConnectionMilliseconds > 0);
            Assert.DoesNotContain("private", lines[0], StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SELECT", lines[0]);
        }
        finally
        {
            accessor.HttpContext = null;
            await observer.StopAsync(default);
        }
    }

    private sealed class ProbeContext(DbContextOptions<ProbeContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Probe>().HasKey(item => item.Id);
    }

    private sealed class Probe
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }
}
