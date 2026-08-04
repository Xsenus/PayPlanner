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

    [Fact]
    public async Task TransformAsync_AcceptsLegacyVersionZeroAndMatchingVersion()
    {
        await SeedUserAsync(isActive: true, isApproved: true, authTokenVersion: 0);

        var legacy = await CreateService().TransformAsync(CreatePrincipal(42));
        var versioned = await CreateService().TransformAsync(CreatePrincipal(42, authTokenVersion: 0));

        Assert.True(legacy.Identity?.IsAuthenticated);
        Assert.True(versioned.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task TransformAsync_RejectsJwtAfterServerVersionChanges()
    {
        await SeedUserAsync(isActive: true, isApproved: true, authTokenVersion: 3);

        var transformed = await CreateService().TransformAsync(CreatePrincipal(42, authTokenVersion: 2));

        Assert.False(transformed.Identity?.IsAuthenticated);
        Assert.Empty(transformed.Claims);
    }

    [Fact]
    public async Task TransformAsync_RejectsMalformedOrDuplicateTokenVersion()
    {
        await SeedUserAsync(isActive: true, isApproved: true);
        var malformed = CreatePrincipal(42);
        ((ClaimsIdentity)malformed.Identity!).AddClaim(
            new Claim(AuthSessionClaims.TokenVersionClaimType, "invalid"));
        var duplicate = CreatePrincipal(42, authTokenVersion: 0);
        ((ClaimsIdentity)duplicate.Identity!).AddClaim(
            AuthSessionClaims.CreateTokenVersionClaim(0));

        Assert.False((await CreateService().TransformAsync(malformed)).Identity?.IsAuthenticated);
        Assert.False((await CreateService().TransformAsync(duplicate)).Identity?.IsAuthenticated);
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

    private async Task SeedUserAsync(bool isActive, bool isApproved, int authTokenVersion = 0)
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
            AuthTokenVersion = authTokenVersion,
        });
        await db.SaveChangesAsync();
    }

    private static ClaimsPrincipal CreatePrincipal(
        int userId,
        string email = "token@example.test",
        string name = "Имя из токена",
        string role = "token-role",
        int? authTokenVersion = null)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Email, email),
            new Claim(ClaimTypes.Name, name),
            new Claim(ClaimTypes.Role, role),
        ],
        authenticationType: "test");
        if (authTokenVersion.HasValue)
        {
            identity.AddClaim(AuthSessionClaims.CreateTokenVersionClaim(authTokenVersion.Value));
        }
        return new ClaimsPrincipal(identity);
    }

    private sealed class TestContextFactory(DbContextOptions<PaymentContext> options)
        : IDbContextFactory<PaymentContext>
    {
        public PaymentContext CreateDbContext()
            => new(options, useObjectAuditModel: false);
    }
}
