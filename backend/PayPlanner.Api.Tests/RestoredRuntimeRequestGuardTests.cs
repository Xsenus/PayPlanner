using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class RestoredRuntimeRequestGuardTests
{
    private const string Instance = "cefe9b7e3bb64c51b3c80a9e96bb57ea";

    [Theory]
    [InlineData("GET", "/hubs/notifications", true, 200)]
    [InlineData("GET", "/user-photos/fixture.png", true, 200)]
    [InlineData("POST", "/api/auth/login", true, 409)]
    [InlineData("DELETE", "/api/tasks/1", true, 409)]
    [InlineData("GET", "/hubs/notifications", false, 409)]
    [InlineData("POST", "/api/tasks", false, 409)]
    public async Task QueryIdentityOnlyAllowsReadsInTheMatchingProbe(string method, string path, bool isolated, int status)
    {
        var context = Context(method, path);
        context.Request.QueryString = new QueryString($"?{RestoredRuntimeRequestGuard.InstanceQuery}={Instance}");
        Assert.Equal(status == 200 ? 1 : 0, await Invoke(context, isolated));
        Assert.Equal(status, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("")]
    [InlineData("cefe9b7e3bb64c51b3c80a9e96bb57ea&pp_restored_instance=cefe9b7e3bb64c51b3c80a9e96bb57ea")]
    public async Task InvalidOrDuplicateQueryCannotBeOverriddenByAValidHeader(string query)
    {
        var context = Context("GET", "/hubs/notifications");
        context.Request.QueryString = new QueryString($"?{RestoredRuntimeRequestGuard.InstanceQuery}={query}");
        context.Request.Headers[RestoredRuntimeRequestGuard.InstanceHeader] = Instance;
        Assert.Equal(0, await Invoke(context, true));
        Assert.Equal(409, context.Response.StatusCode);
    }

    [Fact]
    public async Task HttpPipelineAllowsPreflightButRejectsUnmarkedBusinessRequestBeforeHandler()
    {
        var calls = 0;
        using var server = new TestServer(new WebHostBuilder()
            .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [RestoredRuntimeIsolation.EnabledKey] = "true",
                [RestoredRuntimeDatabaseIdentity.ConfigurationKey] = Instance,
            }))
            .ConfigureServices(services => services.AddCors(options => options.AddDefaultPolicy(policy =>
                policy.WithOrigins("http://127.0.0.1:5173").AllowAnyHeader().AllowAnyMethod())))
            .Configure(app =>
            {
                app.UseCors();
                app.UseMiddleware<RestoredRuntimeRequestGuard>();
                app.Run(_ => { calls++; return Task.CompletedTask; });
            }));
        using var client = server.CreateClient();
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/tasks");
        preflight.Headers.Add("Origin", "http://127.0.0.1:5173");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", RestoredRuntimeRequestGuard.InstanceHeader);
        using var preflightResponse = await client.SendAsync(preflight);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, preflightResponse.StatusCode);
        Assert.Equal(0, calls);
        using var handshake = await client.GetAsync(RestoredRuntimeRequestGuard.IdentityPath);
        Assert.True(handshake.IsSuccessStatusCode);
        Assert.Equal(0, calls);
        using var rejected = await client.PostAsync("/api/tasks", null);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal(0, calls);
        using var accepted = new HttpRequestMessage(HttpMethod.Post, "/api/tasks");
        accepted.Headers.Add(RestoredRuntimeRequestGuard.InstanceHeader, Instance);
        using var acceptedResponse = await client.SendAsync(accepted);
        Assert.True(acceptedResponse.IsSuccessStatusCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task IdentityReadDoesNotReachBusinessHandlerAndIsNotCacheable()
    {
        var context = Context("GET", RestoredRuntimeRequestGuard.IdentityPath);
        var calls = await Invoke(context, true);
        Assert.Equal(0, calls);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.True(json.RootElement.GetProperty("isolated").GetBoolean());
        Assert.Equal(Instance, json.RootElement.GetProperty("instanceId").GetString());
        Assert.Equal(2, json.RootElement.EnumerateObject().Count());
    }

    [Theory]
    [InlineData("GET", "/api/tasks")]
    [InlineData("POST", "/api/auth/login")]
    [InlineData("DELETE", "/api/invoices/1")]
    [InlineData("POST", "/hubs/notifications/negotiate")]
    [InlineData("GET", "/uploads/document.pdf")]
    public async Task MissingIdentityRejectsReadsWritesLoginHubAndFiles(string method, string path)
    {
        var context = Context(method, path);
        Assert.Equal(0, await Invoke(context, true));
        Assert.Equal(409, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("CEFE9B7E3BB64C51B3C80A9E96BB57EA")]
    public async Task WrongIdentityNeverReachesHandler(string id)
    {
        var context = Context("POST", "/api/tasks");
        context.Request.Headers[RestoredRuntimeRequestGuard.InstanceHeader] = id;
        Assert.Equal(0, await Invoke(context, true));
        Assert.Equal(409, context.Response.StatusCode);
    }

    [Fact]
    public async Task DuplicateIdentityIsRejected()
    {
        var context = Context("GET", "/api/tasks");
        context.Request.Headers[RestoredRuntimeRequestGuard.InstanceHeader] = new StringValues([Instance, Instance]);
        Assert.Equal(0, await Invoke(context, true));
        Assert.Equal(409, context.Response.StatusCode);
    }

    [Fact]
    public async Task CorrectIdentityReachesHandler()
    {
        var context = Context("POST", "/api/tasks");
        context.Request.Headers[RestoredRuntimeRequestGuard.InstanceHeader] = Instance;
        Assert.Equal(1, await Invoke(context, true));
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("DELETE")]
    [InlineData("HEAD")]
    public async Task IdentityEndpointOnlyAllowsRead(string method)
    {
        var context = Context(method, RestoredRuntimeRequestGuard.IdentityPath);
        Assert.Equal(0, await Invoke(context, true));
        Assert.Equal(405, context.Response.StatusCode);
        Assert.Equal("GET", context.Response.Headers.Allow.ToString());
    }

    [Fact]
    public async Task NormalServerDoesNotAdvertiseProbeOrAcceptProbeBusinessRequests()
    {
        var handshake = Context("GET", RestoredRuntimeRequestGuard.IdentityPath);
        Assert.Equal(0, await Invoke(handshake, false));
        Assert.Equal(404, handshake.Response.StatusCode);
        var marked = Context("POST", "/api/tasks");
        marked.Request.Headers[RestoredRuntimeRequestGuard.InstanceHeader] = Instance;
        Assert.Equal(0, await Invoke(marked, false));
        Assert.Equal(409, marked.Response.StatusCode);
        var ordinary = Context("POST", "/api/tasks");
        Assert.Equal(1, await Invoke(ordinary, false));
        Assert.False(ordinary.Response.Headers.ContainsKey("Cache-Control"));
    }

    private static DefaultHttpContext Context(string method, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<int> Invoke(HttpContext context, bool isolated)
    {
        var calls = 0;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [RestoredRuntimeIsolation.EnabledKey] = isolated.ToString(),
            [RestoredRuntimeDatabaseIdentity.ConfigurationKey] = Instance,
        }).Build();
        var middleware = new RestoredRuntimeRequestGuard(_ => { calls++; return Task.CompletedTask; }, configuration);
        await middleware.InvokeAsync(context);
        return calls;
    }
}
