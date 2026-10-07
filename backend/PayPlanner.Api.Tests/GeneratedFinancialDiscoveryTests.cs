using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class ManagedGeneratedCaseDocumentTests
{
    [Theory]
    [InlineData(false, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(true, GeneratedFinancialDocumentKind.Invoice)]
    [InlineData(false, GeneratedFinancialDocumentKind.Act)]
    [InlineData(true, GeneratedFinancialDocumentKind.Act)]
    [InlineData(false, GeneratedFinancialDocumentKind.Contract)]
    [InlineData(true, GeneratedFinancialDocumentKind.Contract)]
    public async Task FinancialDiscoveryFindsOwnedCaptureBeforeFileIntentAndProvesExplicitRetirement(bool postgres, GeneratedFinancialDocumentKind kind)
    {
        await using var f = await Fixture.CreateAsync(postgres, 2);
        var orderId = await f.Db.ClientCaseWorkOrders.Select(o => o.Id).SingleAsync();
        var accepted = await CaptureWithClaimsAsync(f.Db, f.Case.Id, orderId, f.User.Id, f.User.FullName, kind);
        var receipt = Assert.IsType<GeneratedFinancialDocumentReceipt>(accepted.Receipt);
        var reader = new CaseDocumentUploadStatusReader(f.Db, new StaticOptions(f.FinancialStorageOptions));
        var pending = Assert.Single((await reader.ListPageAsync(f.Case.Id, f.User.Id, default))!.Items);
        Assert.Equal(receipt.Proof.UploadKey, pending.UploadKey); Assert.Equal("manualRetry", pending.Status);
        Assert.Null(pending.DocumentId); Assert.Null(pending.Version);
        Assert.Equal("financial", pending.Generation!.Kind); Assert.True(pending.Generation.SourceVerified);
        Assert.Equal(receipt.Proof.Kind, pending.Generation.FinancialKind);
        Assert.Equal(receipt.Generation.RequestSha256, pending.Generation.RequestSha256);
        f.FinancialStorageOptions.Enabled = false;
        Assert.Equal("paused", Assert.Single((await reader.ReadAsync(f.Case.Id, f.User.Id, [receipt.Proof.UploadKey], default))!).Status);
        Assert.Equal("retired", await new CaseDocumentUploadRetirementService(f.Db).RetireAsync(f.Case.Id, f.User.Id, receipt.Proof.UploadKey, default));
        var retired = Assert.Single((await reader.ListPageAsync(f.Case.Id, f.User.Id, default))!.Items);
        Assert.Equal("retired", retired.Status); Assert.Equal(pending.Generation, retired.Generation);
        Assert.Empty(await f.Db.StorageObjects.ToArrayAsync()); Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync());
        Assert.Equal(0, await FinancialRowsAsync(f.Db)); Assert.Equal(0, f.Local.PutCount); Assert.Equal(0, f.Mirror.PutCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinancialDiscoveryPagesBoundedMetadataAndExcludesOtherAuthorAndHistoricalPublication(bool postgres)
    {
        var trace = new FinancialDiscoveryQueryTrace();
        await using var f = await Fixture.CreateAsync(postgres, 2, true, trace);
        var orderId = await f.Db.ClientCaseWorkOrders.Select(o => o.Id).SingleAsync();
        var keys = new List<Guid>();
        for (var i = 0; i < 61; i++)
        {
            var result = await CaptureWithClaimsAsync(f.Db, f.Case.Id, orderId, f.User.Id, f.User.FullName, GeneratedFinancialDocumentKind.Contract);
            Assert.Equal("recorded", result.Status); keys.Add(result.Receipt!.Proof.UploadKey);
        }
        var other = new User
        {
            Email = "financial-discovery-other@example.local",
            FullName = "Другой автор",
            PasswordHash = "synthetic",
            IsActive = true,
            IsApproved = true,
            Role = new Role
            {
                Name = "financial-discovery-other",
                Permissions = new[] { RolePermissionCatalog.Cases, RolePermissionCatalog.Contracts }.Select(section => new RolePermission
                { Section = section, CanView = true, CanCreate = true, CanEdit = true, ViewScope = DataVisibilityScope.All }).ToList()
            }
        };
        f.Db.Users.Add(other); await f.Db.SaveChangesAsync();
        var foreign = await CaptureWithClaimsAsync(f.Db, f.Case.Id, orderId, other.Id, other.FullName, GeneratedFinancialDocumentKind.Contract);
        Assert.Equal("recorded", foreign.Status);
        var historic = await f.Db.GeneratedFinancialDocumentAttempts.SingleAsync(a => a.UploadKey == keys[0]);
        historic.PublishedDocumentId = 999999; await f.Db.SaveChangesAsync(); keys.RemoveAt(0);
        var reader = new CaseDocumentUploadStatusReader(f.Db, new StaticOptions(f.FinancialStorageOptions));
        var first = (await reader.ListPageAsync(f.Case.Id, f.User.Id, default))!;
        Assert.Equal(50, first.Items.Count); Assert.NotNull(first.NextCursor);
        var second = (await reader.ListPageAsync(f.Case.Id, f.User.Id, default, first.NextCursor))!;
        Assert.Equal(10, second.Items.Count); Assert.Null(second.NextCursor);
        Assert.Equal(keys.OrderBy(k => k.ToString("N"), StringComparer.Ordinal), first.Items.Concat(second.Items).Select(s => s.UploadKey));
        Assert.All(first.Items.Concat(second.Items), s => { Assert.Equal("manualRetry", s.Status); Assert.Null(s.DocumentId); });
        Assert.Equal(2, trace.Windows); Assert.Equal(2, trace.Proofs); Assert.False(trace.LoadedLargeContent);
        Assert.Empty(await f.Db.StorageObjects.ToArrayAsync()); Assert.Equal(0, await FinancialRowsAsync(f.Db));
        Assert.Equal(0, f.Local.ReadCount); Assert.Equal(0, f.Mirror.ReadCount);
    }

    private sealed class FinancialDiscoveryQueryTrace : DbCommandInterceptor
    {
        public int Windows { get; private set; }
        public int Proofs { get; private set; }
        public bool LoadedLargeContent { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("case-upload-discovery-financial-window", StringComparison.Ordinal)) Windows++;
            if (command.CommandText.Contains("case-upload-status-financial-proofs", StringComparison.Ordinal)) Proofs++;
            if (command.CommandText.Contains("case-upload-discovery-financial-window", StringComparison.Ordinal)
                || command.CommandText.Contains("case-upload-status-financial-proofs", StringComparison.Ordinal))
                LoadedLargeContent |= command.CommandText.Contains("FrozenDomainJson", StringComparison.Ordinal)
                    || command.CommandText.Contains("GeneratedFinancialDocumentContents", StringComparison.Ordinal);
            return ValueTask.FromResult(result);
        }
    }
}
