using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class ProviderSpecificSqlTests
{
    private const string SupplementarySqlInventoryPath = "backend/PayPlanner.Api.Tests/Fixtures/provider-specific-sql-locks.json";
    private const string SupplementarySqlInventorySha256 = "08aa0f14610531da8e2b1004e9569759f6652e4bfad838e75fd0ed97ce5edaec";

    // Keep every original owner-specific guard assertion. The additional owners
    // bind the complete source, including caller/helper transaction and provider
    // guards, as well as exact method, line and SQL identities. Any edit requires
    // an explicit review; an automatically discovered query is never approved.
    private static void ApproveSupplementarySqlLocks(string root, Func<string, string> readSource, HashSet<string> approved)
    {
        var inventory = ReadSupplementarySqlInventory(root);
        foreach (var entry in inventory.Entries)
        {
            var source = NormalizeSqlInventorySource(readSource(entry.Path));
            Assert.Equal(entry.SourceSha256, SqlInventoryHash(source));
            Assert.True(entry.HasDirectProviderGuard);
            Assert.Contains("Database.IsNpgsql()", source, StringComparison.Ordinal);
            var lines = source.Split('\n');
            foreach (var statement in entry.Statements)
            {
                Assert.InRange(statement.Line, 1, lines.Length);
                Assert.False(string.IsNullOrWhiteSpace(statement.Method));
                Assert.Equal(statement.Text, lines[statement.Line - 1].Trim());
                Assert.True(statement.Text.Contains("FOR UPDATE", StringComparison.Ordinal)
                    || statement.Text.Contains("FOR SHARE", StringComparison.Ordinal));
                Assert.True(statement.Text.Contains("NOWAIT", StringComparison.Ordinal)
                    || statement.Text.Contains("SKIP LOCKED", StringComparison.Ordinal));
                Assert.True(approved.Add($"{entry.Path}:{statement.Line}:{statement.Text}"), "An SQL owner cannot be registered twice.");
            }
        }
    }

    public static IEnumerable<object[]> SupplementarySqlOwnerMutations()
        => ReadSupplementarySqlInventory(FindRepositoryRoot()).Entries.SelectMany(entry =>
            new[] { "provider", "bounded-wait", "extra-owner" }.Select(mode => new object[] { entry.Path, mode }));

    [Theory]
    [MemberData(nameof(SupplementarySqlOwnerMutations))]
    public void SupplementarySqlOwnersRejectProviderWaitAndOwnerTampering(string relativePath, string mode)
    {
        var root = FindRepositoryRoot();
        var original = File.ReadAllText(Path.Combine(root, relativePath));
        var changed = mode switch
        {
            "provider" => original.Replace("Database.IsNpgsql()", "Database.IsSqlite()", StringComparison.Ordinal),
            "bounded-wait" => original.Replace("NOWAIT", "", StringComparison.Ordinal).Replace("SKIP LOCKED", "", StringComparison.Ordinal),
            "extra-owner" => original + "\ninternal static class UnregisteredSqlLockOwner { private const string Sql = \"SELECT 1 FOR SHARE NOWAIT\"; }\n",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        Assert.NotEqual(original, changed);
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertRepositoryForUpdateSql(root,
            new Dictionary<string, string>(StringComparer.Ordinal) { [relativePath] = changed }));
    }

    [Fact]
    public void SupplementarySqlInventoryHasExactOwnersAndNoDuplicateStatements()
    {
        var root = FindRepositoryRoot();
        var expected = ReadSupplementarySqlInventory(root);
        Assert.Equal(29, expected.Entries.Select(entry => entry.Path).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(48, expected.Entries.SelectMany(entry => entry.Statements.Select(statement => (entry.Path, statement.Method))).Distinct().Count());
        var approved = new HashSet<string>(StringComparer.Ordinal);
        ApproveSupplementarySqlLocks(root, path => File.ReadAllText(Path.Combine(root, path)), approved);
        Assert.Equal(131, approved.Count);
    }

    private static SupplementarySqlInventory ReadSupplementarySqlInventory(string root)
    {
        var source = NormalizeSqlInventorySource(File.ReadAllText(Path.Combine(root, SupplementarySqlInventoryPath)));
        Assert.Equal(SupplementarySqlInventorySha256, SqlInventoryHash(source));
        var inventory = JsonSerializer.Deserialize<SupplementarySqlInventory>(source);
        Assert.NotNull(inventory);
        Assert.Equal(1, inventory.SchemaVersion);
        Assert.Equal(29, inventory.Entries.Length);
        Assert.Equal(131, inventory.Entries.Sum(entry => entry.Statements.Length));
        Assert.All(inventory.Entries, entry =>
        {
            Assert.StartsWith("backend/", entry.Path, StringComparison.Ordinal);
            Assert.DoesNotContain("..", entry.Path, StringComparison.Ordinal);
            Assert.DoesNotContain("\\", entry.Path, StringComparison.Ordinal);
            Assert.EndsWith(".cs", entry.Path, StringComparison.Ordinal);
        });
        return inventory;
    }

    private static string NormalizeSqlInventorySource(string source) => source.Replace("\r\n", "\n", StringComparison.Ordinal);
    private static string SqlInventoryHash(string source) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
    private sealed record SupplementarySqlInventory(int SchemaVersion, SupplementarySqlOwner[] Entries);
    private sealed record SupplementarySqlOwner(string Path, string SourceSha256, bool HasDirectProviderGuard, SupplementarySqlStatement[] Statements);
    private sealed record SupplementarySqlStatement(string Method, int Line, string Text);
}
