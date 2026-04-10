using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using System.IO.Compression;
using System.Net;
using System.Text;
using Xunit;

namespace PayPlanner.Api.Tests;

public class ResponseCompressionTests
{
    [Fact]
    public async Task JsonResponse_UsesBrotli_WhenClientRequestsBr()
    {
        using var server = CreateServer();
        using var client = server.CreateClient();
        client.BaseAddress = new Uri("https://localhost");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/json");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "br");

        using var response = await client.SendAsync(request);

        response.EnsureSuccessStatusCode();

        Assert.Contains("br", response.Content.Headers.ContentEncoding);

        var compressedBytes = await response.Content.ReadAsByteArrayAsync();
        using var memory = new MemoryStream(compressedBytes);
        using var stream = new BrotliStream(memory, CompressionMode.Decompress);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();

        Assert.Contains("\"message\":\"ok\"", body);
    }

    [Fact]
    public async Task JsonResponse_UsesGzip_WhenClientRequestsGzip()
    {
        using var server = CreateServer();
        using var client = server.CreateClient();
        client.BaseAddress = new Uri("https://localhost");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/json");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");

        using var response = await client.SendAsync(request);

        response.EnsureSuccessStatusCode();

        Assert.Contains("gzip", response.Content.Headers.ContentEncoding);

        var compressedBytes = await response.Content.ReadAsByteArrayAsync();
        using var memory = new MemoryStream(compressedBytes);
        using var stream = new GZipStream(memory, CompressionMode.Decompress);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();

        Assert.Contains("\"message\":\"ok\"", body);
    }

    private static TestServer CreateServer()
    {
        var builder = new WebHostBuilder()
            .ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddResponseCompression(options =>
                {
                    options.EnableForHttps = true;
                    options.Providers.Add<BrotliCompressionProvider>();
                    options.Providers.Add<GzipCompressionProvider>();
                    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/json"]);
                });
                services.Configure<BrotliCompressionProviderOptions>(options =>
                {
                    options.Level = CompressionLevel.Fastest;
                });
                services.Configure<GzipCompressionProviderOptions>(options =>
                {
                    options.Level = CompressionLevel.Fastest;
                });
            })
            .Configure(app =>
            {
                app.UseResponseCompression();
                app.Run(async context =>
                {
                    context.Response.ContentType = "application/json; charset=utf-8";
                    await context.Response.WriteAsync("{\"message\":\"ok\"}");
                });
            });

        return new TestServer(builder);
    }
}
