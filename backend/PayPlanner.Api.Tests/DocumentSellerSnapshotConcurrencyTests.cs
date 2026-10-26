using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Companies;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class DocumentSellerSnapshotPolicyTests
{
    [Fact]
    public async Task FirstFileAndSetSeller_CompeteOnTheSameRootAndRetainTheWinner()
    {
        await using var lease = await template.CreateDatabaseAsync();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var barrier = new SellerReadHook(afterCompany: async () =>
        {
            entered.TrySetResult(true);
            await release.Task;
        });
        await using var first = SellerPgContext(lease.ConnectionString, barrier);
        var company = await SeedAsync(first);
        var document = new Act { Number = "CONCURRENT-FIRST-FILE" };
        first.Acts.Add(document);
        await first.SaveChangesAsync();
        await using var second = SellerPgContext(lease.ConnectionString);
        var stale = await second.Acts.AsNoTracking().SingleAsync(act => act.Id == document.Id);
        barrier.Enabled = true;
        var freeze = new DocumentSellerService(first).ResolveAsync(document, company, true, default, _ => Task.FromResult(true));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await Assert.ThrowsAsync<SellerSnapshotConflictException>(() => new DocumentSellerService(second)
                .UpdateSelectionAsync(stale, company.Id, null, default, _ => Task.FromResult(true)));
        }
        finally { release.TrySetResult(true); }
        await freeze;
        Assert.NotNull(document.SellerSnapshotJson);
        await Assert.ThrowsAsync<SellerSnapshotConflictException>(() => new DocumentSellerService(second)
            .UpdateSelectionAsync(stale, null, null, default, _ => Task.FromResult(true)));
        var reissued = await new DocumentSellerService(second).ResolveAsync(stale, company, true, default, _ => Task.FromResult(true));
        Assert.Equal("Своя", reissued!.ShortName);
        Assert.Equal(document.SellerSnapshotJson, stale.SellerSnapshotJson);
        Assert.Equal(document.SellerSnapshotJson, await second.Acts.AsNoTracking().Where(act => act.Id == document.Id).Select(act => act.SellerSnapshotJson).SingleAsync());
        Assert.Equal(3, await second.CompanyBrandImageVersions.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidSelection_DoesNotLeaseAnUnavailableForeignCompanyOrBranch(bool branch)
    {
        await using var lease = await template.CreateDatabaseAsync();
        var commands = new SellerReadHook();
        await using var db = SellerPgContext(lease.ConnectionString, commands);
        var own = await SeedAsync(db);
        var foreign = new LegalEntity { ShortName = "Foreign", Inn = "7700000000" };
        db.LegalEntities.Add(foreign);
        await db.SaveChangesAsync();
        var foreignBranch = new CompanyBranch { LegalEntityId = foreign.Id, Name = "Foreign branch" };
        var act = new Act { Number = "INVALID-SELLER-ORACLE" };
        db.CompanyBranches.Add(foreignBranch);
        db.Acts.Add(act);
        await db.SaveChangesAsync();
        await using var holder = SellerPgContext(lease.ConnectionString);
        await using var transaction = await holder.Database.BeginTransactionAsync();
        await holder.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"LegalEntities\" WHERE \"Id\" = {foreign.Id} FOR UPDATE NOWAIT").ToArrayAsync();
        await holder.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"CompanyBranches\" WHERE \"Id\" = {foreignBranch.Id} FOR UPDATE NOWAIT").ToArrayAsync();
        commands.Enabled = true;

        await Assert.ThrowsAsync<SellerSelectionException>(() => new DocumentSellerService(db)
            .UpdateSelectionAsync(act, branch ? own.Id : foreign.Id, branch ? foreignBranch.Id : null,
                default, _ => Task.FromResult(true)));

        Assert.Equal(0, commands.CompanyLeaseCount);
        Assert.Equal(0, commands.BranchLeaseCount);
        Assert.Null(await db.Acts.AsNoTracking().Where(row => row.Id == act.Id).Select(row => row.SellerSnapshotJson).SingleAsync());
        Assert.Equal(0, await db.CompanyBrandImageVersions.CountAsync());
    }

    [Fact]
    public async Task DefaultSwitchBeforeRegistryAdmission_ConflictsWithoutLeasingTheOldOrNewCompany()
    {
        await using var lease = await template.CreateDatabaseAsync();
        await using var writer = SellerPgContext(lease.ConnectionString);
        var first = await SeedAsync(writer);
        var second = new LegalEntity { ShortName = "Later default", Inn = "7800000000" };
        writer.LegalEntities.Add(second);
        await writer.SaveChangesAsync();
        var ids = await writer.SystemSettings.SingleAsync(item => item.Key == "legalEntities.mainCompanyIds");
        ids.Value = $"[{first.Id},{second.Id}]";
        writer.SystemSettings.Add(new SystemSetting { Key = "legalEntities.defaultMainCompanyId", Value = first.Id.ToString() });
        var act = new Act { Number = "DEFAULT-CHANGED" };
        writer.Acts.Add(act);
        await writer.SaveChangesAsync();
        var hook = new SellerReadHook(beforeRegistry: async () =>
        {
            var setting = await writer.SystemSettings.SingleAsync(item => item.Key == "legalEntities.defaultMainCompanyId");
            setting.Value = second.Id.ToString();
            await writer.SaveChangesAsync();
        });
        await using var db = SellerPgContext(lease.ConnectionString, hook);
        var visible = await db.Acts.AsNoTracking().SingleAsync(row => row.Id == act.Id);
        hook.Enabled = true;

        await Assert.ThrowsAsync<SellerSnapshotConflictException>(() => new DocumentSellerService(db)
            .ResolveAsync(visible, first, true, default, _ => Task.FromResult(true)));

        Assert.True(hook.RegistryHookTriggered);
        Assert.Equal(0, hook.CompanyLeaseCount);
        Assert.Null(await db.Acts.AsNoTracking().Where(row => row.Id == act.Id).Select(row => row.SellerSnapshotJson).SingleAsync());
        Assert.Equal(0, await db.CompanyBrandImageVersions.CountAsync());
    }

    [Fact]
    public async Task BusyOwnProfile_RejectsWithoutPartialSnapshotOrAutomaticRetry()
    {
        await using var lease = await template.CreateDatabaseAsync();
        var commands = new SellerReadHook();
        await using var db = SellerPgContext(lease.ConnectionString, commands);
        var company = await SeedAsync(db);
        var act = new Act { Number = "BUSY-OWN-PROFILE" };
        db.Acts.Add(act);
        await db.SaveChangesAsync();
        var beforeAudit = await db.ObjectAuditEvents.CountAsync();
        await using (var holder = SellerPgContext(lease.ConnectionString))
        await using (var transaction = await holder.Database.BeginTransactionAsync())
        {
            await holder.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"LegalEntities\" WHERE \"Id\" = {company.Id} FOR UPDATE NOWAIT").ToArrayAsync();
            commands.Enabled = true;
            await Assert.ThrowsAsync<SellerSnapshotConflictException>(() => new DocumentSellerService(db)
                .ResolveAsync(act, company, true, default, _ => Task.FromResult(true)));
            Assert.Equal(1, commands.CompanyLeaseCount);
        }
        Assert.Equal(0, await db.CompanyBrandImageVersions.CountAsync());
        Assert.Equal(beforeAudit, await db.ObjectAuditEvents.CountAsync());
        Assert.Null(await db.Acts.AsNoTracking().Where(row => row.Id == act.Id).Select(row => row.SellerSnapshotJson).SingleAsync());
    }

    [Fact]
    public async Task InvoiceGroup_LocksHiddenSiblingWithoutWritingItsIssuedHistoryOrBlockingVisiblePrint()
    {
        await using var lease = await template.CreateDatabaseAsync();
        await using var probe = SellerPgContext(lease.ConnectionString);
        var checkedHidden = false;
        var hiddenId = 0;
        var hook = new SellerReadHook(afterCompany: async () =>
        {
            await using var transaction = await probe.Database.BeginTransactionAsync();
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => probe.Database.SqlQuery<int>(
                $"SELECT \"Id\" AS \"Value\" FROM \"Payments\" WHERE \"Id\" = {hiddenId} FOR UPDATE NOWAIT").ToArrayAsync());
            var error = Assert.IsType<PostgresException>(failure.InnerException);
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState);
            checkedHidden = true;
        });
        await using var db = SellerPgContext(lease.ConnectionString, hook);
        var company = await SeedAsync(db);
        var invoice = new Invoice { Number = "HIDDEN-SELLER-SIBLING", Date = new DateTime(2026, 10, 24) };
        var visible = new Payment { Invoice = invoice, Account = invoice.Number, Type = PaymentType.Income, DocumentKind = DocumentKind.Invoice };
        var history = SellerLegalEntityComposer.ToSnapshotJson(SellerLegalEntityComposer.Compose(company, null));
        var hidden = new Payment
        {
            Invoice = invoice,
            Account = invoice.Number,
            Type = PaymentType.Income,
            DocumentKind = DocumentKind.Invoice,
            Amount = 271.55m,
            AccountDate = invoice.Date,
            Date = invoice.Date.AddDays(5),
            SellerCompanyId = company.Id,
            SellerSnapshotJson = history
        };
        db.Payments.AddRange(visible, hidden);
        await db.SaveChangesAsync();
        hiddenId = hidden.Id;
        db.Entry(hidden).Property("IsDeleted").CurrentValue = true;
        await db.SaveChangesAsync();
        hook.Enabled = true;

        var seller = await new DocumentSellerService(db).ResolveAsync(visible, company, true, default, _ => Task.FromResult(true));

        Assert.True(checkedHidden);
        Assert.Equal("Своя", seller!.ShortName);
        Assert.NotNull(visible.SellerSnapshotJson);
        var retained = await db.Payments.IgnoreQueryFilters().AsNoTracking().Where(row => row.Id == hidden.Id)
            .Select(row => new
            {
                row.SellerSnapshotJson,
                row.SellerSnapshotPolicy,
                row.SellerCompanyId,
                row.Amount,
                row.AccountDate,
                row.Date,
                row.InvoiceId,
                Deleted = EF.Property<bool>(row, "IsDeleted")
            }).SingleAsync();
        Assert.True(retained.Deleted); Assert.Equal(history, retained.SellerSnapshotJson);
        Assert.Null(retained.SellerSnapshotPolicy); Assert.Equal(company.Id, retained.SellerCompanyId);
        Assert.Equal(271.55m, retained.Amount); Assert.Equal(invoice.Date, retained.AccountDate);
        Assert.Equal(invoice.Date.AddDays(5), retained.Date); Assert.Equal(invoice.Id, retained.InvoiceId);
        // The lease ends with the print; the hidden row is not permanently locked.
        await using var after = await probe.Database.BeginTransactionAsync();
        Assert.Single(await probe.Database.SqlQuery<int>(
            $"SELECT \"Id\" AS \"Value\" FROM \"Payments\" WHERE \"Id\" = {hidden.Id} FOR UPDATE NOWAIT").ToArrayAsync());
    }

    [Fact]
    public async Task InvoiceGroup_RestoredSiblingBeforeRowAdmissionConflictsWithoutSnapshotWrites()
    {
        await using var lease = await template.CreateDatabaseAsync();
        await using var writer = SellerPgContext(lease.ConnectionString);
        writer.Database.SetCommandTimeout(2);
        var hiddenId = 0;
        var writerAudit = 0;
        var hook = new SellerReadHook(beforePayments: async () =>
        {
            var hidden = await writer.Payments.IgnoreQueryFilters().SingleAsync(row => row.Id == hiddenId);
            writer.Entry(hidden).Property("IsDeleted").CurrentValue = false;
            await writer.SaveChangesAsync();
            writerAudit = await writer.ObjectAuditEvents.CountAsync();
        });
        await using var db = SellerPgContext(lease.ConnectionString, hook);
        var company = await SeedAsync(db);
        var invoice = new Invoice { Number = "RESTORED-SELLER-SIBLING", Date = new DateTime(2026, 10, 24) };
        var visible = new Payment { Invoice = invoice, Account = invoice.Number, Type = PaymentType.Income, DocumentKind = DocumentKind.Invoice };
        var hidden = new Payment { Invoice = invoice, Account = invoice.Number, Type = PaymentType.Income, DocumentKind = DocumentKind.Invoice };
        db.Payments.AddRange(visible, hidden);
        await db.SaveChangesAsync();
        hiddenId = hidden.Id;
        db.Entry(hidden).Property("IsDeleted").CurrentValue = true;
        await db.SaveChangesAsync();
        hook.Enabled = true;

        await Assert.ThrowsAsync<SellerSnapshotConflictException>(() => new DocumentSellerService(db)
            .ResolveAsync(visible, company, true, default, _ => Task.FromResult(true)));

        Assert.True(hook.PaymentHookTriggered);
        Assert.Equal(0, hook.CompanyLeaseCount);
        Assert.Equal(writerAudit, await db.ObjectAuditEvents.CountAsync());
        Assert.Equal(2, await db.Payments.AsNoTracking().CountAsync());
        Assert.All(await db.Payments.AsNoTracking().ToArrayAsync(), row => Assert.Null(row.SellerSnapshotJson));
        Assert.Equal(0, await db.CompanyBrandImageVersions.CountAsync());
    }

    private static PostgreSqlPaymentContext SellerPgContext(string connection, params IInterceptor[] interceptors)
        => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connection).AddInterceptors(interceptors).Options);

    private sealed class SellerReadHook(Func<Task>? beforeRegistry = null, Func<Task>? afterCompany = null,
        Func<Task>? beforePayments = null) : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public bool RegistryHookTriggered { get; private set; }
        public bool PaymentHookTriggered { get; private set; }
        public int CompanyLeaseCount { get; private set; }
        public int BranchLeaseCount { get; private set; }
        private bool companyHookTriggered;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!Enabled) return result;
            if (command.CommandText.Contains("FROM \"LegalEntities\"", StringComparison.Ordinal) && command.CommandText.Contains("FOR UPDATE NOWAIT", StringComparison.Ordinal)) CompanyLeaseCount++;
            if (command.CommandText.Contains("FROM \"CompanyBranches\"", StringComparison.Ordinal) && command.CommandText.Contains("FOR SHARE NOWAIT", StringComparison.Ordinal)) BranchLeaseCount++;
            if (!PaymentHookTriggered && beforePayments is not null && command.CommandText.Contains("FROM \"Payments\"", StringComparison.Ordinal)
                && command.CommandText.Contains("FOR UPDATE NOWAIT", StringComparison.Ordinal))
            {
                PaymentHookTriggered = true;
                await beforePayments();
            }
            if (!RegistryHookTriggered && beforeRegistry is not null && command.CommandText.Contains("FROM \"SystemSettings\"", StringComparison.Ordinal)
                && command.CommandText.Contains("FOR SHARE NOWAIT", StringComparison.Ordinal))
            {
                RegistryHookTriggered = true;
                await beforeRegistry();
            }
            return result;
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Enabled && !companyHookTriggered && afterCompany is not null
                && command.CommandText.Contains("FROM \"LegalEntities\"", StringComparison.Ordinal)
                && command.CommandText.Contains("FOR UPDATE NOWAIT", StringComparison.Ordinal))
            {
                companyHookTriggered = true;
                try { await afterCompany(); }
                catch { await result.DisposeAsync(); throw; }
            }
            return result;
        }
    }
}
