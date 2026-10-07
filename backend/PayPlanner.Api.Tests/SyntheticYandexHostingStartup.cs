using System.Net;
using System.Text.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PayPlanner.Api.Data;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Storage;
using PayPlanner.Api.Services.LegalIntegrations;
using PayPlanner.Api.Services.LegalIntegrations.Models;

[assembly: HostingStartup(typeof(PayPlanner.Api.Tests.SyntheticYandexHostingStartup))]

namespace PayPlanner.Api.Tests;

// Providers and the worker are activated only by the owned child test process.
// No endpoint override or worker route is published with the application.
public sealed class SyntheticYandexHostingStartup : IHostingStartup
{
    internal const string EnabledKey = "PAYPLANNER_SYNTHETIC_YANDEX_ENABLED";
    internal const string EndpointKey = "PAYPLANNER_SYNTHETIC_YANDEX_ENDPOINT";
    internal const string WorkerKey = "PAYPLANNER_SYNTHETIC_WORKER_SECRET";
    internal const string WorkerPath = "/__owned-yandex/process-storage";
    internal const string WorkerHeader = "X-Owned-Worker";
    internal const string CourtKey = "PAYPLANNER_SYNTHETIC_COURT_ENABLED";
    internal const string CourtUrl = "https://owned--test.sudrf.ru/modules.php?name=sud_delo&name_op=case&delo_id=42";
    internal const string CourtFile = "owned-court-source.json";
    internal const string LocalFaultKey = "PAYPLANNER_SYNTHETIC_LOCAL_ACK_FAULT";
    internal const string LocalFaultFile = "owned-local-drop-next-put";
    internal const string LocalPutFile = "owned-local-put-count";

    public void Configure(IWebHostBuilder builder)
    {
        builder.ConfigureServices((context, services) =>
        {
            var configuration = context.Configuration;
            var enabled = configuration[EnabledKey];
            if (enabled is null or "false")
            {
                if (new[] { EndpointKey, WorkerKey, CourtKey, LocalFaultKey }.Any(key => configuration[key] is not null))
                    throw new InvalidOperationException("Synthetic Yandex configuration requires explicit activation.");
                return;
            }
            if (enabled != "true")
                throw new InvalidOperationException("Synthetic Yandex activation must be true or false.");
            var root = Path.GetFullPath(context.HostingEnvironment.ContentRootPath);
            var name = Path.GetFileName(root);
            const string prefix = "pp_task_upload_browser_";
            var database = new SqliteConnectionStringBuilder(configuration["ConnectionStrings:Default"]
                ?? throw new InvalidOperationException("Missing synthetic database configuration.")).DataSource;
            if (!name.StartsWith(prefix, StringComparison.Ordinal)
                || !Guid.TryParseExact(name[prefix.Length..], "N", out _)
                || !string.Equals(Path.GetDirectoryName(root), Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFullPath(database), Path.Combine(root, "synthetic.db"), StringComparison.OrdinalIgnoreCase)
                || configuration["Email:Enabled"] != "false" || configuration["BackgroundServices:Enabled"] != "false"
                || !Uri.TryCreate(configuration[EndpointKey], UriKind.Absolute, out var endpoint)
                || endpoint.Scheme != "http" || endpoint.Host != "127.0.0.1" || endpoint.Port <= 0
                || endpoint.AbsolutePath != "/" || endpoint.Query.Length != 0 || endpoint.UserInfo.Length != 0
                || configuration[WorkerKey] is not { Length: 64 } secret)
                throw new InvalidOperationException("Synthetic Yandex startup requires an owned isolated database and endpoint.");

            services.AddHttpClient<IYandexDiskService, YandexDiskService>()
                .ConfigurePrimaryHttpMessageHandler(() => new OwnedYandexTransport(endpoint));
            services.AddSingleton<IStartupFilter>(new OwnedWorkerFilter(secret));
            if (configuration[LocalFaultKey] == "true")
                // Hosting startup runs before Program registers its registry.
                // Resolve the final provider descriptors lazily; an explicit
                // enumerable survives those later ordinary registrations.
                services.AddScoped<IEnumerable<IStorageProvider>>(provider => services
                    .Where(descriptor => descriptor.ServiceType == typeof(IStorageProvider))
                    .Select(descriptor =>
                    {
                        var storage = (IStorageProvider)(descriptor.ImplementationInstance
                            ?? descriptor.ImplementationFactory?.Invoke(provider)
                            ?? ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!));
                        return storage is LocalStorageProvider local
                            ? (IStorageProvider)new OwnedLocalResponseLoss(local, root) : storage;
                    }).ToArray());
            if (configuration[CourtKey] == "true")
                services.AddHttpClient<ISudrfCourtSyncService, SudrfCourtSyncService>()
                    .ConfigurePrimaryHttpMessageHandler(() => new OwnedCourtTransport(Path.Combine(root, CourtFile)));
        });
    }

    // Only registered after the existing owned-root, loopback and mail-off guard.
    private sealed class OwnedLocalResponseLoss(LocalStorageProvider inner, string root)
        : IStorageProvider, IStorageCapacityProvider, IStorageRepairDestinationProvider
    {
        public string Key => inner.Key;
        public StorageProviderCapabilities Capabilities => inner.Capabilities;
        public async Task<StorageWriteResult> PutAsync(StorageWriteRequest request, CancellationToken ct)
        {
            var result = await inner.PutAsync(request, ct);
            if (result.Status != StorageOperationStatus.Succeeded) return result;
            await File.AppendAllTextAsync(Path.Combine(root, LocalPutFile), "put\n", ct);
            var fault = Path.Combine(root, LocalFaultFile);
            if (!File.Exists(fault)) return result;
            File.Delete(fault);
            return result with { Status = StorageOperationStatus.Unknown, Error = "Owned local response lost after successful file persistence." };
        }
        public Task<StorageObjectMetadata> HeadAsync(string locator, CancellationToken ct) => inner.HeadAsync(locator, ct);
        public Task<StorageReadResult> OpenReadAsync(string locator, CancellationToken ct) => inner.OpenReadAsync(locator, ct);
        public Task<StorageDeleteResult> DeleteAsync(string locator, CancellationToken ct) => inner.DeleteAsync(locator, ct);
        public Task<StorageCapacityResource> ObserveCapacityAsync(string key, string? connection, long minimum, CancellationToken ct)
            => inner.ObserveCapacityAsync(key, connection, minimum, ct);
        public string CreateRepairLocator(string original, Guid generation, string key) => inner.CreateRepairLocator(original, generation, key);
    }

    private sealed class OwnedCourtTransport(string path) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method != HttpMethod.Get || request.RequestUri?.AbsoluteUri != new Uri(CourtUrl).AbsoluteUri)
                throw new InvalidOperationException("Synthetic court transport forbids external requests.");
            await using var file = File.OpenRead(path);
            if (file.Length > 256 * 1024) throw new InvalidOperationException("Synthetic court source exceeds its limit.");
            var source = await JsonSerializer.DeserializeAsync<SudrfCourtCaseData>(file, cancellationToken: ct)
                ?? throw new InvalidOperationException("Missing synthetic court source.");
            if (source.SourceUrl != CourtUrl || source.CourtActs.Any(a => a.SourceUrl != CourtUrl))
                throw new InvalidOperationException("Synthetic court source requires its exact owned identity.");
            static string Encode(string value) => WebUtility.HtmlEncode(value);
            var rows = string.Join("", source.CourtActs.Select(a => $"<tr><td>Вынесено {Encode(a.Title.ToLowerInvariant())}</td><td>{a.ActDate:dd.MM.yyyy}</td><td></td><td></td><td>{Encode(a.Result)}</td><td>{Encode(a.Summary)}</td><td>{a.ActDate:dd.MM.yyyy}</td></tr>"));
            var html = $"<html><head><meta charset=\"utf-8\"><title>{Encode(source.CourtName)} - Судебное делопроизводство</title></head><body><h1>ДЕЛО № 33-42/2026</h1><table class=\"cont3\"><tr><th>Наименование события</th><th>Дата</th><th>Время</th><th>Место</th><th>Результат события</th><th>Основания</th><th>Дата размещения</th></tr>{rows}</table></body></html>";
            await File.WriteAllTextAsync(path + ".read", "owned-court-transport", ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html"), RequestMessage = request };
        }
    }

    private sealed class OwnedYandexTransport(Uri endpoint)
        : DelegatingHandler(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Missing synthetic request URI.");
            if (uri.Scheme == "https" && uri.Host == "cloud-api.yandex.net"
                && (uri.AbsolutePath == "/v1/disk" || uri.AbsolutePath.StartsWith("/v1/disk/", StringComparison.Ordinal)))
                request.RequestUri = new Uri(endpoint, uri.PathAndQuery);
            else if (uri.Scheme != "http" || uri.GetLeftPart(UriPartial.Authority) != endpoint.GetLeftPart(UriPartial.Authority))
                throw new InvalidOperationException("Synthetic Yandex transport forbids external requests.");
            return base.SendAsync(request, ct);
        }
    }

    private sealed class OwnedWorkerFilter(string secret) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, remaining) =>
            {
                if (context.Request.Path != WorkerPath) { await remaining(context); return; }
                if (!HttpMethods.IsPost(context.Request.Method)
                    || context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address)
                    || context.Request.Headers[WorkerHeader] != secret)
                { context.Response.StatusCode = 403; return; }
                await using var scope = app.ApplicationServices.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
                if (await db.SystemSettings.Where(s => s.Key == "notifications.email.enabled")
                    .Select(s => s.Value).SingleAsync(context.RequestAborted) != "false")
                    throw new InvalidOperationException("Synthetic worker requires database email isolation.");
                await scope.ServiceProvider.GetRequiredService<GeneratedFinancialAttemptRecovery>()
                    .ProcessBatchAsync(context.RequestAborted);
                var result = await scope.ServiceProvider.GetRequiredService<IStorageOperationProcessor>()
                    .ProcessBatchAsync("owned-yandex-wire", context.RequestAborted);
                await context.Response.WriteAsJsonAsync(result, context.RequestAborted);
            });
            next(app);
        };
    }
}
