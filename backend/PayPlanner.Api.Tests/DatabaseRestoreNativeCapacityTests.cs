using System.Reflection;
using System.Text;
using System.Text.Json;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class DatabaseRestoreNativeCapacityTests
{
    [Theory]
    [InlineData("none")]
    [InlineData("overflow")]
    [InlineData("footer")]
    [InlineData("cancel")]
    public async Task CalculationStreamsOnlyCopiedNativeBindingsAndRejectsInvalidInput(string scenario)
    {
        var inventorySha = new string('a', 64);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var bindings = new[]
        {
            new DatabaseBackupNativeBinding("binding", new("esign-original", 1, "source.pdf", null, null, 1, null),
                "fixture.fileset", "signatures/source.pdf", false, scenario == "overflow" ? long.MaxValue : 123, inventorySha),
            new DatabaseBackupNativeBinding("binding", new("esign-artifact", 2, "signed.pdf", null, null, null, 1),
                "fixture.fileset", "signatures/signed.pdf", false, 456, inventorySha),
            new DatabaseBackupNativeBinding("binding", new("profile-photo", 3, "/user-photos/local.png", null, null, null, null),
                "fixture.fileset", "photos/local.png", false, 789, inventorySha),
            new DatabaseBackupNativeBinding("binding", new("profile-photo", 4, "https://fixture.invalid/photo.png", null, null, null, null),
                null, null, true, null, null),
            new DatabaseBackupNativeBinding("binding", new("case-document", 5, "case/large.pdf", null, null, 1, 1),
                "fixture.fileset", "case/large.pdf", false, long.MaxValue, inventorySha),
        };
        var lines = new List<string>
        {
            JsonSerializer.Serialize(new { kind = "header", formatVersion = 1, inventorySha256 = inventorySha, state = "complete" }, json),
        };
        lines.AddRange(bindings.Select(binding => JsonSerializer.Serialize(binding, json)));
        lines.Add(JsonSerializer.Serialize(new { kind = "footer", bindingCount = scenario == "footer" ? 0 : bindings.Length }, json));
        using var mapping = new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n"));
        using var cancellation = new CancellationTokenSource();
        if (scenario == "cancel") cancellation.Cancel();
        var type = typeof(DatabaseRestoreDrillService).Assembly.GetType("PayPlanner.Api.Services.DatabaseRestoreNativeRuntime")!;
        var calculation = (Task<long>)type.GetMethod("CalculateRequiredBytesAsync", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [mapping, inventorySha, cancellation.Token])!;
        if (scenario == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => calculation);
        else if (scenario != "none") await Assert.ThrowsAsync<InvalidDataException>(() => calculation);
        else Assert.Equal(64L * 1024 * 1024 + 123 + 456 + 789, await calculation);
    }
}
