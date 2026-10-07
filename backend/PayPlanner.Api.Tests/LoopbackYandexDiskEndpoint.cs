using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace PayPlanner.Api.Tests;

// Exercises the real native REST + signed-link transfer sequence on loopback only.
internal sealed class LoopbackYandexDiskEndpoint : IAsyncDisposable
{
    public const string Token = "owned-loopback-yandex-token";
    public const string RootPath = "disk:/owned-yandex";
    private const int MaximumBytes = 1024 * 1024;
    private readonly ConcurrentDictionary<string, byte[]> objects = new();
    private readonly WebApplication app;
    private readonly string root;
    private int puts;
    private int putAttempts;
    public Uri Address { get; private set; } = null!;
    public int PutCount => Volatile.Read(ref puts);
    public int PutAttempts => Volatile.Read(ref putAttempts);
    public bool OmitContentLength { get; set; }
    public long TotalCapacityBytes { get; set; } = 10 * 1024 * 1024;
    public long UsedCapacityBytes { get; set; }
    public bool FailCapacity { get; set; }
    public bool OmitResourceSize { get; set; }
    public bool FailNextPutAfterCommit { get; set; }
    public bool DropNextPutResponse { get; set; }
    public bool FailDownload { get; set; }
    public bool FailUploadLink { get; set; }
    public bool DropDownloads { get; set; }
    public bool TruncateDownloads { get; set; }
    public long? ResourceSizeOverride { get; set; }
    public bool BlockPutAcknowledgement { get; set; }
    public TaskCompletionSource<bool> PutCommitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> ReleasePutAcknowledgement { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private LoopbackYandexDiskEndpoint()
    {
        root = Path.Combine(Path.GetTempPath(), "pp_yandex_wire_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = root, EnvironmentName = "Testing", Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["BackgroundServices:Enabled"] = "false", ["Email:Enabled"] = "false" });
        builder.Logging.ClearProviders();
        builder.WebHost.PreferHostingUrls(false);
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        app = builder.Build();
        app.Run(HandleAsync);
    }

    public static async Task<LoopbackYandexDiskEndpoint> StartAsync()
    {
        var endpoint = new LoopbackYandexDiskEndpoint();
        try
        {
            await endpoint.app.StartAsync();
            endpoint.Address = new Uri(Assert.Single(endpoint.app.Urls));
            Assert.Equal("127.0.0.1", endpoint.Address.Host);
            return endpoint;
        }
        catch { await endpoint.DisposeAsync(); throw; }
    }

    public HttpClient CreateClient() => new(new RouteOnlyToOwnedEndpoint(Address)) { Timeout = TimeSpan.FromSeconds(15) };
    public void SetBytes(string relativePath, byte[] bytes) => objects[RootPath + "/" + relativePath.TrimStart('/')] = bytes.ToArray();
    public byte[] GetBytes(string relativePath) => objects[RootPath + "/" + relativePath.TrimStart('/')].ToArray();
    public void Remove(string relativePath) => objects.TryRemove(RootPath + "/" + relativePath.TrimStart('/'), out _);

    private async Task HandleAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        var key = context.Request.Query["path"].ToString();
        if (path == "/v1/disk" && HttpMethods.IsGet(context.Request.Method))
        {
            if (context.Request.Headers.Authorization != "OAuth " + Token) { context.Response.StatusCode = 401; return; }
            if (FailCapacity) { context.Response.StatusCode = 503; return; }
            await context.Response.WriteAsJsonAsync(new
            {
                total_space = TotalCapacityBytes,
                used_space = UsedCapacityBytes,
                user = new { uid = "128280859" }
            });
            return;
        }
        if (!key.StartsWith(RootPath + "/", StringComparison.Ordinal) && key != RootPath || key.Contains("..", StringComparison.Ordinal))
        { context.Response.StatusCode = 403; return; }
        if (path.StartsWith("/v1/disk/", StringComparison.Ordinal))
        {
            if (context.Request.Headers.Authorization != "OAuth " + Token) { context.Response.StatusCode = 401; return; }
            if (path == "/v1/disk/resources/upload")
            {
                if (FailUploadLink) { context.Response.StatusCode = 503; return; }
                var overwrite = context.Request.Query["overwrite"] == "true";
                if (!overwrite && objects.ContainsKey(key)) { context.Response.StatusCode = 409; return; }
                await context.Response.WriteAsJsonAsync(new { href = new Uri(Address, "/upload?path=" + Uri.EscapeDataString(key) + "&overwrite=" + overwrite.ToString().ToLowerInvariant()).AbsoluteUri });
                return;
            }
            if (path == "/v1/disk/resources/download")
            {
                if (!objects.ContainsKey(key)) { context.Response.StatusCode = 404; return; }
                await context.Response.WriteAsJsonAsync(new { href = new Uri(Address, "/content?path=" + Uri.EscapeDataString(key)).AbsoluteUri });
                return;
            }
            if (path == "/v1/disk/resources")
            {
                if (HttpMethods.IsPut(context.Request.Method)) { context.Response.StatusCode = 201; return; }
                if (HttpMethods.IsGet(context.Request.Method) && objects.TryGetValue(key, out var bytes))
                { await context.Response.WriteAsJsonAsync(new { type = "file", size = OmitResourceSize ? (long?)null : ResourceSizeOverride ?? bytes.Length }); return; }
                if (HttpMethods.IsDelete(context.Request.Method)) { objects.TryRemove(key, out _); context.Response.StatusCode = 204; return; }
            }
        }
        else if (path == "/content" && objects.TryGetValue(key, out var content))
        {
            if (DropDownloads) { context.Abort(); return; }
            if (FailDownload) { context.Response.StatusCode = 503; return; }
            context.Response.ContentType = "application/octet-stream";
            if (!OmitContentLength) context.Response.ContentLength = content.Length;
            if (TruncateDownloads)
            {
                await context.Response.Body.WriteAsync(content.AsMemory(0, content.Length / 2), context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                context.Abort(); return;
            }
            await context.Response.Body.WriteAsync(content, context.RequestAborted);
            return;
        }
        else if (path == "/upload" && HttpMethods.IsPut(context.Request.Method))
        {
            Interlocked.Increment(ref putAttempts);
            if (context.Request.Headers.Authorization.Count != 0) { context.Response.StatusCode = 403; return; }
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
            {
                if (bytes.Length + count > MaximumBytes) { context.Response.StatusCode = 413; return; }
                await bytes.WriteAsync(buffer.AsMemory(0, count), context.RequestAborted);
            }
            var body = bytes.ToArray();
            if (context.Request.Query["overwrite"] == "true") objects[key] = body;
            else if (!objects.TryAdd(key, body)) { context.Response.StatusCode = 409; return; }
            Interlocked.Increment(ref puts);
            PutCommitted.TrySetResult(true);
            if (BlockPutAcknowledgement) await ReleasePutAcknowledgement.Task.WaitAsync(context.RequestAborted);
            if (DropNextPutResponse) { DropNextPutResponse = false; context.Abort(); return; }
            if (FailNextPutAfterCommit) { FailNextPutAfterCommit = false; context.Response.StatusCode = 503; return; }
            context.Response.StatusCode = 201;
            return;
        }
        context.Response.StatusCode = 404;
    }

    private sealed class RouteOnlyToOwnedEndpoint(Uri endpoint) : DelegatingHandler(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Missing request URI");
            if (uri.Scheme == "https" && uri.Host == "cloud-api.yandex.net"
                && (uri.AbsolutePath == "/v1/disk" || uri.AbsolutePath.StartsWith("/v1/disk/", StringComparison.Ordinal)))
                request.RequestUri = new Uri(endpoint, uri.PathAndQuery);
            else Assert.Equal(endpoint.GetLeftPart(UriPartial.Authority), uri.GetLeftPart(UriPartial.Authority));
            Assert.Equal("http", request.RequestUri.Scheme);
            Assert.Equal("127.0.0.1", request.RequestUri.Host);
            return base.SendAsync(request, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await app.DisposeAsync();
        var resolved = Path.GetFullPath(root);
        Assert.Matches("^pp_yandex_wire_[a-f0-9]{32}$", Path.GetFileName(resolved));
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(resolved));
        Directory.Delete(resolved, recursive: true);
    }
}
