using System.Net;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class S3EndpointSecurityPolicyTests
{
    [Theory]
    [InlineData("http://storage.example.com")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://10.0.0.1")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://[::1]")]
    [InlineData("https://user:password@storage.example.com")]
    public void ValidateConfiguration_RejectsUnsafeEndpoints(string endpoint)
    {
        var policy = CreatePolicy(new S3EndpointSecurityOptions());
        Assert.Throws<S3StorageOperationException>(() => policy.ValidateConfiguration(endpoint));
    }

    [Fact]
    public void ValidateConfiguration_AllowsExplicitPrivateHostException()
    {
        var policy = CreatePolicy(new S3EndpointSecurityOptions
        {
            AllowedHosts = ["minio.internal"],
        });
        policy.ValidateConfiguration("https://minio.internal");
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("172.16.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("::1")]
    [InlineData("fd00::1")]
    public void IsPrivateOrSpecial_DetectsProtectedRanges(string value)
        => Assert.True(S3EndpointSecurityPolicy.IsPrivateOrSpecial(IPAddress.Parse(value)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("2606:4700:4700::1111")]
    public void IsPrivateOrSpecial_AllowsPublicRanges(string value)
        => Assert.False(S3EndpointSecurityPolicy.IsPrivateOrSpecial(IPAddress.Parse(value)));

    private static S3EndpointSecurityPolicy CreatePolicy(S3EndpointSecurityOptions options)
        => new(new StaticOptionsMonitor<S3EndpointSecurityOptions>(options));

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
