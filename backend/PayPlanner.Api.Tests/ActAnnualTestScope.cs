using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;
using SourceFixture = PayPlanner.Api.Tests.ActsSourceAvailabilityTests.Fixture;

namespace PayPlanner.Api.Tests;

internal sealed class ActAnnualTestScope(SourceFixture source, PostgreSqlMigratedTemplate.DatabaseLease? lease = null) : IAsyncDisposable
{
    public SourceFixture Source { get; } = source;
    public PaymentContext Db => Source.Db;
    public string? ConnectionString => lease?.ConnectionString;

    public static async Task<ActAnnualTestScope> CreateAsync(PostgreSqlMigratedTemplate template, bool postgres)
    {
        if (!postgres) return new(await SourceFixture.CreateAsync());
        Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES")),
            "Native annual-act checks require the explicitly configured synthetic PostgreSQL connection.");
        var database = await template.CreateDatabaseAsync("pp_act_annual_");
        try { return new(await SourceFixture.CreatePostgreSqlAsync(database.ConnectionString), database); }
        catch { await database.DisposeAsync(); throw; }
    }

    public ActsController Controller(int? actorId = null, INotificationService? notifications = null, IFormRequirementsService? requirements = null)
        => new(Db, requirements ?? new NoopRequirements(), notificationService: notifications)
        {
            ControllerContext = Context(actorId ?? Source.Actor.Id)
        };

    public static ControllerContext Context(int actorId)
        => new()
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actorId.ToString())], "AnnualActTest"))
            }
        };

    public UpsertActRequest Request(int year = 2026)
    {
        var request = Source.Request();
        request.Number = string.Empty;
        request.Date = new DateTime(year, 6, 15);
        return request;
    }

    public async ValueTask DisposeAsync()
    {
        try { await Source.DisposeAsync(); }
        finally { if (lease is not null) await lease.DisposeAsync(); }
    }

    internal sealed class NoopRequirements : IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default) => Task.FromResult(new List<string>());
        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default) => Task.FromResult(new List<string>());
    }
}
