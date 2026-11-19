using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using PayPlanner.Api.Services;
using System.Text.Json;
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
        Assert.Contains("Content-Disposition", policy.ExposedHeaders);
        Assert.Contains("X-Upload-Key", policy.ExposedHeaders);
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
        Assert.Contains("Content-Disposition", policy.ExposedHeaders);
        Assert.Contains("X-Upload-Key", policy.ExposedHeaders);
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

    [Fact]
    public void ProductionConfiguration_AllowsPublicCrmFrontendOrigin()
    {
        var root = FindRepositoryRoot();
        using var appsettings = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "backend", "appsettings.Production.json")));

        var origins = appsettings.RootElement
            .GetProperty("Cors")
            .GetProperty("AllowedOrigins")
            .EnumerateArray()
            .Select(origin => origin.GetString()?.TrimEnd('/'))
            .ToArray();

        Assert.Contains("https://crm.elitpravo.com", origins);
        Assert.Contains("http://185.46.10.202", origins);
        Assert.Contains("https://185.46.10.202", origins);
    }

    private static CorsPolicy GetDefaultPolicy(CorsOptions options)
        => options.GetPolicy(options.DefaultPolicyName)
           ?? throw new InvalidOperationException("Default CORS policy was not configured.");

    private static string FindRepositoryRoot()
    {
        var current = TestRepository.FindDirectory();

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "package.json"))
                && Directory.Exists(Path.Combine(current.FullName, ".github"))
                && Directory.Exists(Path.Combine(current.FullName, "backend")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

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
