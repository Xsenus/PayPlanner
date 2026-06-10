using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class CorsPolicyConfigurationTests
{
    [Fact]
    public void ConfigureDefaultPolicy_AllowsAnyOriginInDevelopment()
    {
        var options = new CorsOptions();
        var configuration = new ConfigurationBuilder().Build();

        CorsPolicyConfiguration.ConfigureDefaultPolicy(
            options,
            configuration,
            new TestWebHostEnvironment("Development"));

        var policy = GetDefaultPolicy(options);
        Assert.True(policy.AllowAnyOrigin);
        Assert.Contains("*", policy.Headers);
        Assert.Contains("*", policy.Methods);
    }

    [Fact]
    public void ConfigureDefaultPolicy_RestrictsProductionToConfiguredOrigins()
    {
        var options = new CorsOptions();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cors:AllowedOrigins:0"] = "https://crm.example.com/",
                ["Cors:AllowedOrigins:1"] = "https://admin.example.com"
            })
            .Build();

        CorsPolicyConfiguration.ConfigureDefaultPolicy(
            options,
            configuration,
            new TestWebHostEnvironment("Production"));

        var policy = GetDefaultPolicy(options);
        Assert.False(policy.AllowAnyOrigin);
        Assert.Equal(
            ["https://crm.example.com", "https://admin.example.com"],
            policy.Origins);
        Assert.Contains("*", policy.Headers);
        Assert.Contains("*", policy.Methods);
    }

    [Fact]
    public void ConfigureDefaultPolicy_DoesNotAllowAnyOriginInProductionWhenOriginsAreMissing()
    {
        var options = new CorsOptions();
        var configuration = new ConfigurationBuilder().Build();

        CorsPolicyConfiguration.ConfigureDefaultPolicy(
            options,
            configuration,
            new TestWebHostEnvironment("Production"));

        var policy = GetDefaultPolicy(options);
        Assert.False(policy.AllowAnyOrigin);
        Assert.Empty(policy.Origins);
    }

    private static CorsPolicy GetDefaultPolicy(CorsOptions options)
        => options.GetPolicy(options.DefaultPolicyName)
           ?? throw new InvalidOperationException("Default CORS policy was not configured.");

    private sealed class TestWebHostEnvironment(string environmentName) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PayPlanner.Api.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public string EnvironmentName { get; set; } = environmentName;
        public string WebRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
