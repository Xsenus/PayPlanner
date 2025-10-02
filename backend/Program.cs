using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models.Auth;
using PayPlanner.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<PaymentContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<UserProfileService>();
builder.Services.AddScoped<ActivityLogService>();

var jwtKey = builder.Configuration["Jwt:Key"] ?? "your-super-secret-key-min-32-chars-long-12345";
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "PayPlanner";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "PayPlanner";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapPost("/api/auth/login", async (LoginRequest request, AuthService authService) =>
{
    var result = await authService.LoginAsync(request);
    return result != null ? Results.Ok(result) : Results.Unauthorized();
});

app.MapPost("/api/auth/register", async (RegisterRequest request, AuthService authService) =>
{
    var result = await authService.RegisterAsync(request);
    return result != null ? Results.Ok(result) : Results.BadRequest(new { message = "User already exists" });
});

app.MapGet("/api/auth/me", async (HttpContext context, AuthService authService) =>
{
    var userId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    if (userId == null)
    {
        return Results.Unauthorized();
    }

    var user = await authService.GetUserByIdAsync(userId);
    return user != null ? Results.Ok(user) : Results.NotFound();
})
.RequireAuthorization();

app.MapGet("/api/users", async (AuthService authService) =>
{
    var users = await authService.GetAllUsersAsync();
    return Results.Ok(users);
})
.RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapGet("/api/users/{id}", async (string id, AuthService authService) =>
{
    var user = await authService.GetUserByIdAsync(id);
    return user != null ? Results.Ok(user) : Results.NotFound();
})
.RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPut("/api/users/{id}", async (string id, UpdateUserRequest request, AuthService authService) =>
{
    var success = await authService.UpdateUserAsync(id, request.FullName, request.IsActive);
    return success ? Results.Ok() : Results.NotFound();
})
.RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPost("/api/users/{id}/password", async (string id, ChangePasswordRequest request, AuthService authService) =>
{
    var success = await authService.ChangePasswordAsync(id, request.NewPassword);
    return success ? Results.Ok() : Results.NotFound();
})
.RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPost("/api/users/{userId}/roles/{roleId}", async (string userId, int roleId, AuthService authService) =>
{
    var success = await authService.AssignRoleAsync(userId, roleId);
    return success ? Results.Ok() : Results.BadRequest();
})
.RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapDelete("/api/users/{userId}/roles/{roleId}", async (string userId, int roleId, AuthService authService) =>
{
    var success = await authService.RemoveRoleAsync(userId, roleId);
    return success ? Results.Ok() : Results.NotFound();
})
.RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapGet("/api/roles", async (AuthService authService) =>
{
    var roles = await authService.GetAllRolesAsync();
    return Results.Ok(roles);
})
.RequireAuthorization();

// User activation/deactivation endpoints
app.MapPost("/api/users/{id}/activate", async (string id, AuthService authService) =>
{
    var success = await authService.ActivateUserAsync(id);
    return success ? Results.Ok() : Results.NotFound();
})
.RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPost("/api/users/{id}/deactivate", async (string id, AuthService authService) =>
{
    var success = await authService.DeactivateUserAsync(id);
    return success ? Results.Ok() : Results.NotFound();
})
.RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapDelete("/api/users/{id}", async (string id, AuthService authService) =>
{
    var success = await authService.DeleteUserAsync(id);
    return success ? Results.Ok() : Results.NotFound();
})
.RequireAuthorization(policy => policy.RequireRole("admin"));

// User profile endpoints
app.MapGet("/api/profiles/{userId}", async (string userId, UserProfileService profileService) =>
{
    var profile = await profileService.GetProfileByUserIdAsync(userId);
    return profile != null ? Results.Ok(profile) : Results.NotFound();
})
.RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPost("/api/profiles/{userId}", async (string userId, CreateUserProfileRequest request, UserProfileService profileService) =>
{
    var profile = await profileService.CreateOrUpdateProfileAsync(userId, request);
    return Results.Ok(profile);
})
.RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapDelete("/api/profiles/{userId}", async (string userId, UserProfileService profileService) =>
{
    var success = await profileService.DeleteProfileAsync(userId);
    return success ? Results.Ok() : Results.NotFound();
})
.RequireAuthorization(policy => policy.RequireRole("admin"));

// Activity log endpoints
app.MapGet("/api/activity-logs", async (
    [AsParameters] ActivityLogFilterRequest filter,
    ActivityLogService logService) =>
{
    var (logs, total) = await logService.GetLogsAsync(filter);
    return Results.Ok(new { logs, total, page = filter.Page, pageSize = filter.PageSize });
})
.RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapGet("/api/activity-logs/user/{userId}", async (string userId, ActivityLogService logService) =>
{
    var logs = await logService.GetUserLogsAsync(userId);
    return Results.Ok(logs);
})
.RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPost("/api/activity-logs/clear", async (
    string? userId,
    DateTime? beforeDate,
    ActivityLogService logService) =>
{
    await logService.ClearLogsAsync(userId, beforeDate);
    return Results.Ok();
})
.RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapGet("/api/activity-logs/status", (ActivityLogService logService) =>
{
    return Results.Ok(new { enabled = logService.IsLoggingEnabled() });
})
.RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPost("/api/activity-logs/enable", (ActivityLogService logService) =>
{
    logService.EnableLogging();
    return Results.Ok();
})
.RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPost("/api/activity-logs/disable", (ActivityLogService logService) =>
{
    logService.DisableLogging();
    return Results.Ok();
})
.RequireAuthorization(policy => policy.RequireRole("admin"));

app.Run();

public record UpdateUserRequest(string? FullName, bool? IsActive);
public record ChangePasswordRequest(string NewPassword);
