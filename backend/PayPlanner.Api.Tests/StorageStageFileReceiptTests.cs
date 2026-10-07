using System.Text.Json.Nodes;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class StorageStageFileReceiptTests
{
    private const string WindowsIdentity = "windows-stage-file-v1:ffffffffffffffff:0123456789abcdef0123456789abcdef:0123456789abcdef";
    private const string LinuxIdentity = "linux-stage-file-v1:ffffffff:12345678:ffffffffffffffff:0123456789abcdef:12345678";
    private static StorageStageFileReceipt Initial(string identity = WindowsIdentity)
        => StorageStageFileReceipt.Create(Guid.NewGuid(), new string('a', 64), 2L * StorageStageFileReceipt.MaximumBlockBytes, identity);

    [Theory]
    [InlineData(WindowsIdentity)]
    [InlineData(LinuxIdentity)]
    public void ReceiptPreservesExactIdentityAndBothWriteBoundaries(string identity)
    {
        var initial = Initial(identity);
        var first = initial.Plan(StorageStageFileReceipt.MaximumBlockBytes, new string('b', 64));
        var second = first.Plan(first.UpperBound, new string('c', 64));
        Assert.Equal(0, initial.PlannedBytes);
        Assert.Equal(1, first.Sequence);
        Assert.Equal(2, second.Sequence);
        Assert.Equal(first.PlannedBytes, second.PreviousBytes);
        Assert.Equal(first.PlannedSha256, second.PreviousSha256);
        Assert.Equal(initial.ObjectId, second.ObjectId);
        Assert.Equal(initial.AdmissionBindingHash, second.AdmissionBindingHash);
        Assert.Equal(identity, second.FileIdentity);
        foreach (var receipt in new[] { initial, first, second })
        {
            var json = receipt.Serialize();
            Assert.InRange(json.Length, 1, StorageStageFileReceipt.MaximumJsonLength);
            Assert.True(StorageStageFileReceipt.TryParse(json, out var parsed));
            Assert.Equal(receipt, parsed);
        }
    }

    [Theory]
    [InlineData("Version")]
    [InlineData("ObjectId")]
    [InlineData("AdmissionBindingHash")]
    [InlineData("UpperBound")]
    [InlineData("FileIdentity")]
    [InlineData("Sequence")]
    [InlineData("PreviousBytes")]
    [InlineData("PreviousSha256")]
    [InlineData("PlannedBytes")]
    [InlineData("PlannedSha256")]
    public void ReceiptRejectsMissingOrInvalidRequiredFields(string field)
    {
        var node = JsonNode.Parse(Initial().Serialize())!.AsObject();
        node.Remove(field);
        Assert.False(StorageStageFileReceipt.TryParse(node.ToJsonString(), out var missing));
        Assert.Null(missing);
        node[field] = field switch
        {
            "Version" => JsonValue.Create(2),
            "ObjectId" => JsonValue.Create(Guid.Empty),
            "UpperBound" => JsonValue.Create(0),
            "Sequence" or "PreviousBytes" or "PlannedBytes" => JsonValue.Create(-1),
            "FileIdentity" => JsonValue.Create("unknown"),
            _ => JsonValue.Create(new string('A', 64))
        };
        Assert.False(StorageStageFileReceipt.TryParse(node.ToJsonString(), out var invalid));
        Assert.Null(invalid);
    }

    [Fact]
    public void ReceiptRejectsAmbiguousUnboundedAndInconsistentMetadata()
    {
        var receipt = Initial();
        var json = receipt.Serialize();
        foreach (var invalid in new string?[] { null, "null", "[]", "{", new(' ', 1025),
            json.Insert(1, "\"Version\":1,"), json.Insert(1, "\"Unexpected\":1,") })
            Assert.False(StorageStageFileReceipt.TryParse(invalid, out _));
        Assert.Throws<InvalidOperationException>(() => (receipt with { PlannedSha256 = new string('b', 64) }).Serialize());
        Assert.Throws<InvalidOperationException>(() => (receipt with { Sequence = 2, PlannedBytes = 4, PlannedSha256 = new string('b', 64) }).Serialize());
        Assert.Throws<ArgumentException>(() => Initial(WindowsIdentity + ":extra"));
        Assert.Throws<ArgumentException>(() => Initial(WindowsIdentity.ToUpperInvariant()));
    }

    [Fact]
    public void PlanningCannotRepeatShrinkOverrunOrOverflowTheAdmittedWrite()
    {
        var initial = Initial();
        var first = initial.Plan(4, new string('b', 64));
        foreach (var bytes in new long[] { -1, 0, 4, first.UpperBound + 1, 4L + StorageStageFileReceipt.MaximumBlockBytes + 1 })
            Assert.Throws<ArgumentException>(() => first.Plan(bytes, new string('c', 64)));
        Assert.Throws<ArgumentException>(() => first.Plan(5, new string('C', 64)));
        Assert.Throws<ArgumentException>(() => (first with { Sequence = long.MaxValue }).Plan(5, new string('c', 64)));
        Assert.Equal(0, initial.Sequence);
        Assert.Equal(4, first.PlannedBytes);
    }
}
