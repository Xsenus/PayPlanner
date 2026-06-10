using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class ApiErrorContractTests
{
    [Theory]
    [InlineData("/api/missing", HttpStatusCode.NotFound, "NotFound")]
    [InlineData("/api/unauthorized", HttpStatusCode.Unauthorized, "Unauthorized")]
    [InlineData("/api/forbidden", HttpStatusCode.Forbidden, "Forbidden")]
    public async Task BareApiErrorStatusCodes_ReturnProblemDetails(string path, HttpStatusCode statusCode, string title)
    {
        using var server = CreateServer();
        using var client = server.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(statusCode, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal((int)statusCode, problem!.Status);
        Assert.Equal(title, problem.Title);
        Assert.False(string.IsNullOrWhiteSpace(problem.Detail));
        Assert.True(problem.Extensions.ContainsKey("traceId"));
    }

    [Fact]
    public async Task UnhandledApiException_ReturnsProblemDetailsWithoutLeakingExceptionMessage()
    {
        using var server = CreateServer();
        using var client = server.CreateClient();

        using var response = await client.GetAsync("/api/boom");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal(500, problem!.Status);
        Assert.Equal("UnhandledServerError", problem.Title);
        Assert.DoesNotContain("sensitive-db-secret", problem.Detail, StringComparison.Ordinal);
    }

    private static TestServer CreateServer()
    {
        var builder = new WebHostBuilder()
            .UseEnvironment("Production")
            .ConfigureServices(services => services.AddRouting())
            .Configure(app =>
            {
                app.UseApiErrorContract(app.ApplicationServices.GetRequiredService<IWebHostEnvironment>());
                app.UseRouting();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapGet("/api/missing", context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                        return Task.CompletedTask;
                    });
                    endpoints.MapGet("/api/unauthorized", context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    });
                    endpoints.MapGet("/api/forbidden", context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return Task.CompletedTask;
                    });
                    endpoints.MapGet("/api/boom", _ => throw new InvalidOperationException("sensitive-db-secret"));
                });
            });

        return new TestServer(builder);
    }
}
