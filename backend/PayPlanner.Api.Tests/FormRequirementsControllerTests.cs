using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using Xunit;

namespace PayPlanner.Api.Tests;

public class FormRequirementsControllerTests
{
    [Fact]
    public async Task Upsert_ReturnsForbid_ForNonAdminUser()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "form-user", isAdmin: false);
        var controller = CreateController(db, user.Id);

        var result = await controller.Upsert(
            new UpsertFormFieldRequirementRequest
            {
                Section = "clients",
                FieldKey = "Phone",
                IsRequired = true,
            },
            CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        Assert.False(await db.FormFieldRequirements.AnyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Upsert_SavesRequirement_ForAdminUser()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateUserAsync(db, "form-admin", isAdmin: true);
        var controller = CreateController(db, admin.Id);

        var result = await controller.Upsert(
            new UpsertFormFieldRequirementRequest
            {
                Section = " clients ",
                FieldKey = " Phone ",
                IsRequired = true,
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<FormFieldRequirementDto>(ok.Value);
        Assert.Equal("clients", dto.Section);
        Assert.Equal("Phone", dto.FieldKey);
        Assert.True(dto.IsRequired);

        var stored = await db.FormFieldRequirements.AsNoTracking().SingleAsync(CancellationToken.None);
        Assert.Equal("clients", stored.Section);
        Assert.Equal("Phone", stored.FieldKey);
        Assert.True(stored.IsRequired);
    }

    private static async Task<User> CreateUserAsync(PaymentContext db, string name, bool isAdmin)
    {
        var role = new Role
        {
            Name = isAdmin ? "admin" : $"{name}-{Guid.NewGuid():N}",
            Description = name,
        };
        var user = new User
        {
            Email = $"{name}-{Guid.NewGuid():N}@example.test",
            FullName = name,
            PasswordHash = "test",
            Role = role,
            IsActive = true,
            IsApproved = true,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static FormRequirementsController CreateController(PaymentContext db, int userId)
        => new(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                        new Claim(ClaimTypes.Name, "Test user"),
                    ], "Test")),
                },
            },
        };

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
