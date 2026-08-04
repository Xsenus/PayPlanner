using System.Security.Claims;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CurrentUserClaimsTransformationTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private DbContextOptions<PaymentContext> _options = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(_connection)
            .Options;

        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync() => _connection.DisposeAsync().AsTask();

    [Fact]
    public async Task TransformAsync_RefreshesClaimsForActiveApprovedUser()
    {
        await SeedUserAsync(isActive: true, isApproved: true);
        var principal = CreatePrincipal(userId: 42, email: "old@example.test", name: "Старое имя", role: "old-role");

        var transformed = await CreateService().TransformAsync(principal);

        Assert.True(transformed.Identity?.IsAuthenticated);
        Assert.Equal("actual@example.test", transformed.FindFirstValue(ClaimTypes.Email));
        Assert.Equal("Актуальное имя", transformed.FindFirstValue(ClaimTypes.Name));
        Assert.Equal("lawyer", transformed.FindFirstValue(ClaimTypes.Role));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task TransformAsync_RejectsDisabledOrUnapprovedUser(bool isActive, bool isApproved)
    {
        await SeedUserAsync(isActive, isApproved);

        var transformed = await CreateService().TransformAsync(CreatePrincipal(42));

        Assert.False(transformed.Identity?.IsAuthenticated);
        Assert.Empty(transformed.Claims);
    }

    [Fact]
    public async Task TransformAsync_RejectsUserMissingFromDatabase()
    {
        var transformed = await CreateService().TransformAsync(CreatePrincipal(404));

        Assert.False(transformed.Identity?.IsAuthenticated);
        Assert.Empty(transformed.Claims);
    }

    [Fact]
    public async Task TransformAsync_LeavesAnonymousPrincipalUntouched()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity());

        var transformed = await CreateService().TransformAsync(principal);

        Assert.Same(principal, transformed);
    }

    private CurrentUserClaimsTransformation CreateService()
        => new(new TestContextFactory(_options));

    private PaymentContext CreateContext() => new(_options, useObjectAuditModel: false);

    private async Task SeedUserAsync(bool isActive, bool isApproved)
    {
        await using var db = CreateContext();
        var role = new Role
        {
            Id = 7,
            Name = "lawyer",
            Description = "Юрист",
        };
        db.Roles.Add(role);
        db.Users.Add(new User
        {
            Id = 42,
            Email = "actual@example.test",
            FullName = "Актуальное имя",
            PasswordHash = "test",
            RoleId = role.Id,
            Role = role,
            IsActive = isActive,
            IsApproved = isApproved,
        });
        await db.SaveChangesAsync();
    }

    private static ClaimsPrincipal CreatePrincipal(
        int userId,
        string email = "token@example.test",
        string name = "Имя из токена",
        string role = "token-role")
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Email, email),
            new Claim(ClaimTypes.Name, name),
            new Claim(ClaimTypes.Role, role),
        ],
        authenticationType: "test");
        return new ClaimsPrincipal(identity);
    }

    private sealed class TestContextFactory(DbContextOptions<PaymentContext> options)
        : IDbContextFactory<PaymentContext>
    {
        public PaymentContext CreateDbContext()
            => new(options, useObjectAuditModel: false);
    }
}
