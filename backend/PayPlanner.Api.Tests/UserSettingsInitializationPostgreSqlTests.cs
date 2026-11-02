using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class UserSettingsInitializationPostgreSqlTests
{
    [Theory]
    [InlineData(false, true, "Europe/Samara")]
    [InlineData(true, false, "Europe/Samara")]
    [InlineData(false, false, "Europe/Samara")]
    [InlineData(true, true, "UTC")]
    public async Task PostgreSql_ConcurrentFirstWritesRespectExplicitChoiceAndExistingRow(bool firstIsDefault, bool secondIsDefault, string expected)
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES")
            ?? throw new InvalidOperationException("Required isolated PostgreSQL test role is not configured."));
        Assert.Contains(connection.Host, new[] { "127.0.0.1", "localhost", "::1" });
        Assert.Equal(5433, connection.Port);
        Assert.Equal("payplanner_ci", connection.Username);
        connection.Database = "postgres"; connection.Pooling = false;
        await using var admin = new NpgsqlConnection(connection.ConnectionString); await admin.OpenAsync();
        await using (var check = new NpgsqlCommand("SELECT rolcreatedb AND NOT rolsuper AND NOT rolcreaterole AND NOT rolreplication AND NOT rolbypassrls FROM pg_roles WHERE rolname = current_user", admin))
            Assert.Equal(true, await check.ExecuteScalarAsync());
        Assert.StartsWith("17.", admin.PostgreSqlVersion.ToString());
        var database = "pp_user_setting_init_" + Guid.NewGuid().ToString("N");
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            connection.Database = database; connection.CommandTimeout = 20;
            await using var first = Context(connection.ConnectionString);
            await first.Database.EnsureCreatedAsync();
            var user = new User
            {
                FullName = "Synthetic preference owner",
                Email = "setting-owner@example.test",
                PasswordHash = "hash",
                IsActive = true,
                IsApproved = true,
                Role = new Role { Name = "synthetic-setting-role", Description = "Isolated test role" }
            };
            var other = new User { FullName = "Synthetic other", Email = "setting-other@example.test", PasswordHash = "hash", IsActive = true, IsApproved = true, Role = user.Role };
            first.Users.AddRange(user, other); await first.SaveChangesAsync();
            first.UserSettings.Add(new UserSetting { UserId = other.Id, Key = "profile.timeZoneId", Value = "Asia/Novosibirsk" }); await first.SaveChangesAsync();
            connection.ApplicationName = database + "-second"; connection.Timezone = "Europe/Moscow";
            await using var second = Context(connection.ConnectionString);
            Task<ActionResult<UserSettingDto>>? waiting = null;
            var firstValue = firstIsDefault ? "UTC" : secondIsDefault ? "Europe/Samara" : "Europe/Moscow";
            var secondValue = secondIsDefault ? "Asia/Novosibirsk" : "Europe/Samara";
            await using (var transaction = await first.Database.BeginTransactionAsync())
            {
                try
                {
                    var initialRequest = new UpsertUserSettingRequest { Value = firstValue };
                    var initial = firstIsDefault ? await Controller(first, user.Id).Initialize("profile.timeZoneId", initialRequest)
                        : await Controller(first, user.Id).Upsert("profile.timeZoneId", initialRequest);
                    Assert.Equal(firstValue, Assert.IsType<UserSettingDto>(Assert.IsType<OkObjectResult>(initial.Result).Value).Value);
                    var secondRequest = new UpsertUserSettingRequest { Value = secondValue };
                    waiting = secondIsDefault ? Controller(second, user.Id).Initialize("profile.timeZoneId", secondRequest)
                        : Controller(second, user.Id).Upsert("profile.timeZoneId", secondRequest);
                    var timer = Stopwatch.StartNew(); var blocked = false;
                    while (timer.Elapsed < TimeSpan.FromSeconds(10))
                    {
                        await using var observe = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE application_name = @name AND wait_event_type = 'Lock')", admin);
                        observe.Parameters.AddWithValue("name", connection.ApplicationName);
                        if (Equals(true, await observe.ExecuteScalarAsync())) { blocked = true; break; }
                        if (waiting.IsCompleted) break;
                        await Task.Delay(20);
                    }
                    Assert.True(blocked, "The second upsert must actually wait at the existing unique-key boundary.");
                    Assert.False(waiting.IsCompleted);
                    await transaction.CommitAsync();
                    var result = await waiting.WaitAsync(TimeSpan.FromSeconds(20));
                    Assert.Equal(expected, Assert.IsType<UserSettingDto>(Assert.IsType<OkObjectResult>(result.Result).Value).Value);
                }
                finally
                {
                    if (waiting is { IsCompleted: false })
                    {
                        await transaction.RollbackAsync();
                        try { await waiting.WaitAsync(TimeSpan.FromSeconds(25)); } catch { /* Preserve the original assertion and release this owned connection. */ }
                    }
                }
            }
            var saved = await first.UserSettings.AsNoTracking().SingleAsync(row => row.UserId == user.Id);
            Assert.Equal(expected, saved.Value);
            Assert.InRange(saved.UpdatedAt, DateTime.UtcNow.AddMinutes(-2), DateTime.UtcNow.AddMinutes(1));
            Assert.Equal(2, await first.UserSettings.CountAsync());
            Assert.Equal("Asia/Novosibirsk", await first.UserSettings.AsNoTracking().Where(row => row.UserId == other.Id).Select(row => row.Value).SingleAsync());
            var originalTimestamp = saved.UpdatedAt;
            var initialized = await Controller(first, user.Id).Initialize("profile.timeZoneId", new UpsertUserSettingRequest { Value = "Europe/Moscow" });
            Assert.Equal(expected, Assert.IsType<UserSettingDto>(Assert.IsType<OkObjectResult>(initialized.Result).Value).Value);
            Assert.Equal(originalTimestamp, (await first.UserSettings.AsNoTracking().SingleAsync(row => row.UserId == user.Id)).UpdatedAt);
            Assert.Empty(await first.Payments.ToListAsync()); Assert.Empty(await first.ClientCases.ToListAsync()); Assert.Empty(await first.TaskJournalItems.ToListAsync());
        }
        finally
        {
            Assert.Matches("^pp_user_setting_init_[a-f0-9]{32}$", database);
            await using var owner = new NpgsqlCommand("SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = @name", admin); owner.Parameters.AddWithValue("name", database);
            Assert.Equal("payplanner_ci", await owner.ExecuteScalarAsync());
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\"", admin); await drop.ExecuteNonQueryAsync();
        }
    }

    private static PostgreSqlPaymentContext Context(string connection) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
        .UseNpgsql(connection).Options, useObjectAuditModel: false);

    private static UserSettingsController Controller(PaymentContext db, int userId) => new(db)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "SyntheticAuth")) }
        },
    };
}
