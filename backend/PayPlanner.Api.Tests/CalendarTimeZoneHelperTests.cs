using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CalendarTimeZoneHelperTests
{
    [Fact]
    public async Task ResolveUserTimeZoneAsync_UsesProfileSettingAndFallsBackToLocal()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "timezone-role", Description = "Timezone role" };
        var configuredUser = new User
        {
            Email = "configured-timezone@example.test",
            FullName = "Configured Timezone",
            IsActive = true,
            Role = role,
        };
        var fallbackUser = new User
        {
            Email = "fallback-timezone@example.test",
            FullName = "Fallback Timezone",
            IsActive = true,
            Role = role,
        };
        db.Roles.Add(role);
        db.Users.AddRange(configuredUser, fallbackUser);
        await db.SaveChangesAsync();
        db.UserSettings.Add(new UserSetting
        {
            UserId = configuredUser.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = " \"Europe/Samara\" ",
        });
        await db.SaveChangesAsync();

        var configured = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(db, configuredUser.Id);
        var fallback = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(db, fallbackUser.Id);
        var anonymous = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(db, null);

        Assert.Equal(CalendarTimeZoneHelper.ResolveTimeZone("Europe/Samara").Id, configured.Id);
        Assert.Equal(TimeZoneInfo.Local.Id, fallback.Id);
        Assert.Equal(TimeZoneInfo.Local.Id, anonymous.Id);
    }

    [Fact]
    public void FormatUserDateTime_UsesProvidedTimeZone()
    {
        var samara = CalendarTimeZoneHelper.ResolveTimeZone("Europe/Samara");
        var novosibirsk = CalendarTimeZoneHelper.ResolveTimeZone("Asia/Novosibirsk");
        var value = new DateTime(2026, 6, 4, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(
            "04.06.2026 16:00",
            CalendarTimeZoneHelper.FormatUserDateTime(value, samara, culture: CultureInfo.GetCultureInfo("ru-RU")));
        Assert.Equal(
            "04.06.2026 19:00",
            CalendarTimeZoneHelper.FormatUserDateTime(value, novosibirsk, culture: CultureInfo.GetCultureInfo("ru-RU")));
    }

    [Fact]
    public void FromUserLocalToUtc_ConvertsProfileLocalClockToUtc()
    {
        var samara = CalendarTimeZoneHelper.ResolveTimeZone("Europe/Samara");
        var localValue = new DateTime(2026, 6, 5, 16, 0, 0, DateTimeKind.Unspecified);

        var utc = CalendarTimeZoneHelper.FromUserLocalToUtc(localValue, samara);

        Assert.Equal(DateTimeKind.Utc, utc.Kind);
        Assert.Equal(new DateTime(2026, 6, 5, 12, 0, 0, DateTimeKind.Utc), utc);
    }

    [Fact]
    public void FormatOffsetDateTime_UsesProfileOffsetForExternalCalendars()
    {
        var samara = CalendarTimeZoneHelper.ResolveTimeZone("Europe/Samara");
        var value = new DateTime(2026, 6, 4, 12, 0, 0, DateTimeKind.Utc);

        var formatted = CalendarTimeZoneHelper.FormatOffsetDateTime(value, samara);

        Assert.Equal("2026-06-04T16:00:00+04:00", formatted);
    }

    [Fact]
    public void FormatUtcIcsDateTime_AlwaysEmitsUtcInstant()
    {
        var samara = CalendarTimeZoneHelper.ResolveTimeZone("Europe/Samara");
        var value = new DateTime(2026, 6, 4, 12, 0, 0, DateTimeKind.Utc);

        var formatted = CalendarTimeZoneHelper.FormatUtcIcsDateTime(value, samara);

        Assert.Equal("20260604T120000Z", formatted);
    }

    [Fact]
    public void NormalizeUtcInstant_TreatsUnspecifiedAsUtcWithoutClockShift()
    {
        var value = new DateTime(2026, 6, 4, 12, 0, 0, DateTimeKind.Unspecified);

        var normalized = CalendarTimeZoneHelper.NormalizeUtcInstant(value);

        Assert.Equal(DateTimeKind.Utc, normalized.Kind);
        Assert.Equal(new DateTime(2026, 6, 4, 12, 0, 0, DateTimeKind.Utc), normalized);
    }

    [Fact]
    public void PaymentSystemNote_UsesExplicitTimeZoneForTimestamp()
    {
        var payment = new Payment();
        var samara = CalendarTimeZoneHelper.ResolveTimeZone("Europe/Samara");
        var value = new DateTime(2026, 6, 4, 12, 0, 0, DateTimeKind.Utc);

        PaymentDomainService.AppendSystemNote(payment, "Test note", value, "Tester", samara);

        Assert.Contains("04.06.2026 (16:00:00)", payment.SystemNotes, StringComparison.Ordinal);
        Assert.Contains("Tester: Test note", payment.SystemNotes, StringComparison.Ordinal);
    }

    private static async Task<PaymentContext> CreateContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options;

        var db = new PaymentContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }
}
