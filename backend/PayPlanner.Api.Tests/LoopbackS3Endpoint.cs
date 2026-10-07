using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

/// <summary>Bounded synthetic S3 wire fixture. Never uses real buckets, secrets, DNS, or external addresses.</summary>
internal sealed class LoopbackS3Endpoint : IAsyncDisposable
{
    public const string ConnectionId = "synthetic-loopback-s3";
    public const string Bucket = "synthetic-bucket";
    private const int MaxBytes = 1024 * 1024;
    private readonly ConcurrentDictionary<string, (byte[] Bytes, string Type)> _objects = new();
    private readonly WebApplication _app;
    private readonly string _root;
    private int _putCount;
    public Uri Address { get; private set; } = null!;
    public int PutCount => Volatile.Read(ref _putCount);
    public bool Available { get; set; } = true;
    public S3StorageConnectionSettings Connection => new()
    {
        Id = ConnectionId,
        Name = "Synthetic loopback S3",
        Provider = "custom",
        Endpoint = Address.AbsoluteUri.TrimEnd('/'),
        Region = "us-east-1",
        Bucket = Bucket,
        RootPrefix = "fixture",
        AccessKeyId = "synthetic-access",
        SecretAccessKey = "synthetic-secret-unused-elsewhere",
        IsEnabled = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private LoopbackS3Endpoint()
    {
        _root = Path.Combine(Path.GetTempPath(), "pp_task_upload_s3_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        { ContentRootPath = _root, EnvironmentName = "Production", Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        { ["BackgroundServices:Enabled"] = "false", ["Email:Enabled"] = "false" });
        builder.Logging.ClearProviders();
        builder.WebHost.PreferHostingUrls(false);
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        _app = builder.Build();
        _app.Run(HandleAsync);
    }

    public static async Task<LoopbackS3Endpoint> StartAsync()
    {
        var result = new LoopbackS3Endpoint();
        try
        {
            await result._app.StartAsync();
            result.Address = new Uri(Assert.Single(result._app.Urls));
            Assert.Equal("127.0.0.1", result.Address.Host);
            Assert.Equal("http", result.Address.Scheme);
            return result;
        }
        catch { await result.DisposeAsync(); throw; }
    }

    private async Task HandleAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (!path.StartsWith($"/{Bucket}/fixture/", StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal)
            || !context.Request.Headers.Authorization.ToString().Contains("Credential=synthetic-access/", StringComparison.Ordinal))
        { context.Response.StatusCode = 403; return; }
        if (!Available) { context.Response.StatusCode = 503; return; }
        if (HttpMethods.IsPut(context.Request.Method))
        {
            if (_objects.Count >= 4 || context.Request.ContentLength > MaxBytes)
            { context.Response.StatusCode = 413; return; }
            using var raw = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
            {
                if (raw.Length + read > MaxBytes) { context.Response.StatusCode = 413; return; }
                await raw.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
            }
            var body = raw.ToArray();
            if (context.Request.Headers.ContentEncoding.ToString().Contains("aws-chunked", StringComparison.OrdinalIgnoreCase))
                body = DecodeAwsChunks(body);
            if (context.Request.Headers.TryGetValue("x-amz-decoded-content-length", out var length))
                Assert.Equal(int.Parse(length.ToString(), CultureInfo.InvariantCulture), body.Length);
            _objects[path] = (body, context.Request.ContentType ?? "application/octet-stream");
            Interlocked.Increment(ref _putCount);
            context.Response.Headers.ETag = '"' + Convert.ToHexString(MD5.HashData(body)).ToLowerInvariant() + '"';
            context.Response.StatusCode = 200;
            return;
        }
        if (HttpMethods.IsHead(context.Request.Method) || HttpMethods.IsGet(context.Request.Method))
        {
            if (!_objects.TryGetValue(path, out var stored)) { context.Response.StatusCode = 404; return; }
            context.Response.ContentLength = stored.Bytes.Length;
            context.Response.ContentType = stored.Type;
            context.Response.Headers.ETag = '"' + Convert.ToHexString(MD5.HashData(stored.Bytes)).ToLowerInvariant() + '"';
            context.Response.Headers.LastModified = DateTimeOffset.UtcNow.ToString("R", CultureInfo.InvariantCulture);
            if (HttpMethods.IsGet(context.Request.Method)) await context.Response.Body.WriteAsync(stored.Bytes, context.RequestAborted);
            return;
        }
        context.Response.StatusCode = 405;
    }

    private static byte[] DecodeAwsChunks(byte[] raw)
    {
        using var output = new MemoryStream();
        var offset = 0;
        while (offset < raw.Length)
        {
            var headerLength = raw.AsSpan(offset).IndexOf("\r\n"u8);
            Assert.True(headerLength >= 1, "Malformed synthetic AWS chunk header.");
            var header = Encoding.ASCII.GetString(raw, offset, headerLength).Split(';')[0];
            var size = int.Parse(header, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            offset += headerLength + 2;
            if (size == 0) return output.ToArray();
            Assert.True(size > 0 && size <= MaxBytes && offset <= raw.Length - size - 2);
            Assert.Equal((byte)'\r', raw[offset + size]);
            Assert.Equal((byte)'\n', raw[offset + size + 1]);
            output.Write(raw, offset, size);
            offset += size + 2;
        }
        throw new InvalidDataException("Synthetic AWS chunk stream is incomplete.");
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await _app.StopAsync(timeout.Token);
        await _app.DisposeAsync();
        _objects.Clear();
        var fullRoot = Path.GetFullPath(_root);
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(fullRoot), ignoreCase: true);
        Assert.StartsWith("pp_task_upload_s3_", Path.GetFileName(fullRoot), StringComparison.Ordinal);
        Assert.True(Guid.TryParseExact(Path.GetFileName(fullRoot)["pp_task_upload_s3_".Length..], "N", out _));
        if (Directory.Exists(fullRoot)) Directory.Delete(fullRoot, recursive: true);
    }
}
