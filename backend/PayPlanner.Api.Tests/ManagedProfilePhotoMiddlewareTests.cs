using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class ManagedProfilePhotoMiddlewareTests
{
    [Theory]
    [InlineData("available", "GET", 200)]
    [InlineData("absolute", "GET", 200)]
    [InlineData("available", "HEAD", 200)]
    [InlineData("range", "GET", 206)]
    [InlineData("unavailable", "GET", 503)]
    [InlineData("deleted", "GET", 404)]
    [InlineData("legacy", "GET", 200)]
    [InlineData("disabled", "GET", 200)]
    [InlineData("class-disabled", "GET", 200)]
    [InlineData("other-photo", "GET", 200)]
    public async Task CurrentPhotoUsesManagedStreamWithoutBypassingKnownFailures(string scenario, string method, int expectedStatus)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options;
        await using var seed = new PaymentContext(dbOptions, useObjectAuditModel: false);
        await seed.Database.EnsureCreatedAsync();
        var user = new User
        {
            Email = "photo@example.test",
            FullName = "Тест фото",
            PasswordHash = "hash",
            Role = new Role { Name = "admin", Description = "Администратор" },
            IsActive = true,
            IsApproved = true
        };
        seed.Users.Add(user);
        await seed.SaveChangesAsync();
        var path = $"/user-photos/{user.Id}/photo.png";
        user.PhotoUrl = scenario == "other-photo" ? $"/user-photos/{user.Id}/new.png"
            : scenario == "absolute" ? "https://crm.example.test" + path : path;
        await seed.SaveChangesAsync();
        var root = Path.Combine(Path.GetTempPath(), "pp-photo-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, user.Id.ToString()));
        byte[] legacyBytes = [7, 8, 9];
        await File.WriteAllBytesAsync(Path.Combine(root, user.Id.ToString(), "photo.png"), legacyBytes);
        var reader = new TestReader(scenario);
        var monitor = new StaticMonitor(new()
        {
            Enabled = scenario != "disabled",
            ProductReadEnabledClasses = scenario == "class-disabled" ? [] : ["profile-photo"]
        });
        try
        {
            using var provider = new PhysicalFileProvider(root);
            using var server = new TestServer(new WebHostBuilder().ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddScoped(_ => new PaymentContext(dbOptions, useObjectAuditModel: false));
                services.AddSingleton<IOptionsMonitor<StorageOrchestrationOptions>>(monitor);
                services.AddSingleton<IProductStorageReader>(reader);
            }).Configure(app =>
            {
                app.UseMiddleware<ManagedProfilePhotoMiddleware>();
                // A readable old physical file must not bypass catalog failure.
                app.UseStaticFiles(new StaticFileOptions { FileProvider = provider, RequestPath = "/user-photos" });
            }));
            using var client = server.CreateClient();
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (scenario == "range") request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1, 2);
            using var response = await client.SendAsync(request);
            Assert.Equal((HttpStatusCode)expectedStatus, response.StatusCode);
            var body = await response.Content.ReadAsByteArrayAsync();
            if (scenario is "available" or "absolute" or "range")
            {
                Assert.Equal(method == "HEAD" ? [] : scenario == "range" ? [2, 3] : reader.Bytes, body);
                Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
                Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
                Assert.True(reader.Stream!.Disposed);
                Assert.Equal((LegacyStorageSources.ProfilePhotos, (long)user.Id, user.PhotoUrl!, "image/*"), reader.Binding);
            }
            else if (scenario is "deleted" or "unavailable")
            {
                Assert.Empty(body);
                Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
            }
            else Assert.Equal(legacyBytes, body);
            Assert.Equal(scenario is "disabled" or "class-disabled" or "other-photo" ? 0 : 1, reader.Calls);
        }
        finally
        {
            Assert.StartsWith("pp-photo-test-", Path.GetFileName(root), StringComparison.Ordinal);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class TestReader(string scenario) : IProductStorageReader
    {
        public byte[] Bytes { get; } = [1, 2, 3, 4];
        public int Calls { get; private set; }
        public (string, long, string, string) Binding { get; private set; }
        public TrackedStream? Stream { get; private set; }
        public Task<ProductStorageReadResult> OpenReadAsync(string source, long fileId, string logicalName, string contentType, CancellationToken cancellationToken)
        {
            Calls++;
            Binding = (source, fileId, logicalName, contentType);
            var status = scenario switch
            {
                "deleted" => ProductStorageReadStatus.Deleted,
                "unavailable" => ProductStorageReadStatus.Unavailable,
                "legacy" => ProductStorageReadStatus.Legacy,
                _ => ProductStorageReadStatus.Available
            };
            if (status != ProductStorageReadStatus.Available) return Task.FromResult(new ProductStorageReadResult(status));
            Stream = new TrackedStream(Bytes);
            return Task.FromResult(new ProductStorageReadResult(status,
                new StorageObjectReadResult(Guid.NewGuid(), 1, "image/*", Path.GetFileName(logicalName), Bytes.Length, Stream, "mirror")));
        }
    }
    private sealed class TrackedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class StaticMonitor(StorageOrchestrationOptions settings) : IOptionsMonitor<StorageOrchestrationOptions>
    {
        public StorageOrchestrationOptions CurrentValue => settings;
        public StorageOrchestrationOptions Get(string? name) => settings;
        public IDisposable? OnChange(Action<StorageOrchestrationOptions, string?> listener) => null;
    }
}
