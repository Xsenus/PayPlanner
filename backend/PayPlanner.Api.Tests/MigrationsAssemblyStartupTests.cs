using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PayPlanner.Api.Data;
using Xunit;
using Xunit.Abstractions;

namespace PayPlanner.Api.Tests;

public sealed class MigrationsAssemblyStartupTests(ITestOutputHelper output)
{
    [Fact]
    public void ExplicitAssemblyObjectIsPreserved()
    {
        var assembly = typeof(MigrationsAssemblyStartupTests).Assembly;
        using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite("Data Source=:memory:", options => options.MigrationsAssembly(assembly)).Options);
        Assert.Same(assembly, db.GetService<IMigrationsAssembly>().Assembly);
    }

    [Fact]
    public async Task ApiStartsAndMigratesWithItsOwnDependencyManifest()
    {
        // The test manifest references migrations and masks a broken deployment. Start the real API
        // with its own manifest, exactly as the published application is resolved on the server.
        var root = Path.Combine(Path.GetTempPath(), "pp_migrations_startup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var apiPath = typeof(PaymentContext).Assembly.Location;
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--depsfile");
        start.ArgumentList.Add(Path.ChangeExtension(apiPath, ".deps.json"));
        start.ArgumentList.Add("--runtimeconfig");
        start.ArgumentList.Add(Path.ChangeExtension(apiPath, ".runtimeconfig.json"));
        start.ArgumentList.Add(apiPath);
        var inherited = start.Environment.ToArray();
        start.Environment.Clear();
        foreach (var (key, value) in inherited)
            if (new[] { "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "DOTNET_ROOT", "DOTNET_ROOT_X64" }
                .Contains(key, StringComparer.OrdinalIgnoreCase)) start.Environment[key] = value;
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
        start.Environment["ASPNETCORE_CONTENTROOT"] = root;
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        start.Environment["Database__Provider"] = "sqlite";
        start.Environment["ConnectionStrings__Default"] = $"Data Source={Path.Combine(root, "test.db")};Pooling=False";
        start.Environment["BackgroundServices__Enabled"] = "false";
        start.Environment["Email__Enabled"] = "false";
        start.Environment["Seed__ClientsAndPayments"] = "false";
        start.Environment["PAYPLANNER_SKIP_CLIENT_SEED"] = "1";
        start.Environment["Jwt__Secret"] = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        start.Environment["DataProtection__KeysPath"] = Path.Combine(root, "keys");
        start.Environment["DOTNET_PROCESSOR_COUNT"] = "1";
        var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new StringBuilder();
        using var process = new Process { StartInfo = start };
        var started = false;
        void ReadLine(object sender, DataReceivedEventArgs args)
        {
            if (args.Data is not { } line) return;
            lock (log) log.AppendLine(line);
            const string marker = "Now listening on: ";
            var index = line.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0 && Uri.TryCreate(line[(index + marker.Length)..].Trim(), UriKind.Absolute, out var uri))
                listening.TrySetResult(uri);
        }
        process.OutputDataReceived += ReadLine;
        process.ErrorDataReceived += ReadLine;
        try
        {
            started = process.Start();
            Assert.True(started);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var exited = process.WaitForExitAsync();
            var winner = await Task.WhenAny(listening.Task, exited, Task.Delay(TimeSpan.FromMinutes(2)));
            lock (log) Assert.True(winner == listening.Task, log.ToString());
            using var client = new HttpClient { BaseAddress = await listening.Task, Timeout = TimeSpan.FromSeconds(15) };
            using var response = await client.GetAsync("/api/health");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
                .UseSqlite(start.Environment["ConnectionStrings__Default"]!).Options);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Contains("20261011035741_AddSalesRequestNotificationOutbox", await db.Database.GetAppliedMigrationsAsync());
        }
        finally
        {
            if (started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            lock (log) output.WriteLine(log.ToString());
            // Windows can briefly retain SQLite's memory-mapped WAL after the process exits.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                    break;
                }
                catch (Exception error) when (attempt < 10 && error is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(200);
                }
            }
        }
    }
}
