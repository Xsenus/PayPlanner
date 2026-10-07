using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using PayPlanner.Api.Hubs;
using PayPlanner.Api.Data;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

// Test-only gateway: ordinary child API + actual authenticated hubs in the publisher's process.
// It never manufactures notification frames or starts general jobs/external transports.
internal sealed class SyntheticLiveNotificationGateway : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly HttpClient _forward = new();
    private readonly ReadyFilter _ready;
    private readonly Errors _errors;
    private readonly IServiceScope _publisherScope;
    public Uri Address => new(_app.Urls.Single());
    public IRealtimeNotificationPublisher Publisher => _publisherScope.ServiceProvider.GetRequiredService<IRealtimeNotificationPublisher>();

    private SyntheticLiveNotificationGateway(WebApplication app, ReadyFilter ready, Errors errors)
    {
        (_app, _ready, _errors) = (app, ready, errors);
        _publisherScope = app.Services.CreateScope();
    }

    public static async Task<SyntheticLiveNotificationGateway> StartAsync(SyntheticWritableBackendProcess runtime)
    {
        Assert.Matches("^pp_task_upload_browser_[a-f0-9]{32}$", Path.GetFileName(runtime.Root));
        Assert.False(runtime.Configuration.GetValue<bool>("Email:Enabled"));
        Assert.False(runtime.Configuration.GetValue<bool>("BackgroundServices:Enabled"));
        await using (var db = runtime.OpenDatabase())
            Assert.Equal("false", (await db.SystemSettings.SingleAsync(s => s.Key == "notifications.email.enabled")).Value);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = runtime.Root, EnvironmentName = "Production" });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddConfiguration(runtime.Configuration);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var errors = new Errors();
        builder.Logging.ClearProviders(); builder.Logging.AddProvider(errors);
        builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(runtime.Configuration["Cors:AllowedOrigins:0"]!)
            .AllowAnyHeader().AllowAnyMethod()));
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(runtime.Configuration["Jwt:Secret"]!)),
                ValidateIssuer = true,
                ValidIssuer = runtime.Configuration["Jwt:Issuer"],
                ValidateAudience = true,
                ValidAudience = runtime.Configuration["Jwt:Audience"],
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(2)
            };
            o.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    if (context.Request.Path.StartsWithSegments(NotificationsHub.Path) || context.Request.Path.StartsWithSegments(NotesHub.Path))
                        context.Token = context.Request.Query["access_token"];
                    return Task.CompletedTask;
                },
                OnTokenValidated = async context =>
                {
                    if (context.Principal is null) { context.Fail("Missing principal."); return; }
                    var principal = await context.HttpContext.RequestServices.GetRequiredService<CurrentUserClaimsTransformation>()
                        .TransformAsync(context.Principal);
                    if (principal.Identity?.IsAuthenticated != true) context.Fail("Unavailable synthetic account.");
                    else context.Principal = principal;
                }
            };
        });
        builder.Services.AddAuthorization();
        var ready = new ReadyFilter();
        builder.Services.AddSingleton(ready);
        builder.Services.AddSignalR(o => o.AddFilter<ReadyFilter>());
        builder.Services.AddScoped(_ => runtime.OpenDatabase());
        builder.Services.AddSingleton<IDbContextFactory<PaymentContext>>(new OwnedDatabaseFactory(runtime));
        builder.Services.AddScoped<CurrentUserClaimsTransformation>();
        builder.Services.AddScoped<IRealtimeNotificationPublisher, SignalRRealtimeNotificationPublisher>();
        var app = builder.Build();
        var gateway = new SyntheticLiveNotificationGateway(app, ready, errors);
        app.UseCors(); app.UseAuthentication(); app.UseAuthorization();
        app.MapHub<NotificationsHub>(NotificationsHub.Path);
        app.MapHub<NotesHub>(NotesHub.Path);
        app.Map("/api/{**path}", async context =>
        {
            // Forward only to the owned child. Do not reuse its already-authenticated test client.
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method),
                new Uri(runtime.Client.BaseAddress!, context.Request.Path + context.Request.QueryString));
            if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
                request.Content = new StreamContent(context.Request.Body);
            foreach (var header in context.Request.Headers)
            {
                if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase)) continue;
                if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                    request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
            using var response = await gateway._forward.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Headers.Concat(response.Content.Headers))
                if (!header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) && !header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                    context.Response.Headers[header.Key] = header.Value.ToArray();
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        });
        try { await app.StartAsync(); return gateway; }
        catch { await gateway.DisposeAsync(); throw; }
    }

    public async Task WaitForRecipientAsync(int id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await _ready.WaitAsync(id, timeout.Token);
    }

    public async Task AssertAuthenticationAsync(string token, int taskId)
    {
        using var client = new HttpClient { BaseAddress = Address };
        using (var response = await client.PostAsync(NotificationsHub.Path + "/negotiate?negotiateVersion=1", null))
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "invalid-synthetic-token");
        using (var response = await client.PostAsync(NotificationsHub.Path + "/negotiate?negotiateVersion=1", null))
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        using (var response = await client.GetAsync($"/api/tasks/{taskId}"))
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        using (var response = await client.PostAsync(NotificationsHub.Path + "/negotiate?negotiateVersion=1", null))
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        using (var response = await client.GetAsync($"/api/tasks/{taskId}"))
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(); _publisherScope.Dispose(); await _app.DisposeAsync(); _forward.Dispose();
        Assert.Empty(_errors.Messages);
    }

    private sealed class OwnedDatabaseFactory(SyntheticWritableBackendProcess runtime) : IDbContextFactory<PaymentContext>
    {
        public PaymentContext CreateDbContext() => runtime.OpenDatabase();
        public Task<PaymentContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(runtime.OpenDatabase());
    }

    private sealed class ReadyFilter : IHubFilter
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _users = new();
        public Task WaitAsync(int id, CancellationToken ct) => _users.GetOrAdd(id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(ct);
        public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
        {
            await next(context);
            if (context.Hub is NotificationsHub)
                _users.GetOrAdd(int.Parse(context.Context.User!.FindFirstValue(ClaimTypes.NameIdentifier)!),
                    _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        }
    }

    private sealed class Errors : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Collector(Messages);
        public void Dispose() { }
        private sealed class Collector(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => level >= LogLevel.Error;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
            { if (IsEnabled(level)) messages.Enqueue(format(state, error)); }
        }
    }
}
