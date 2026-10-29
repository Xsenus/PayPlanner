using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Companies;

public sealed class SellerSnapshotConflictException : Exception
{
    public SellerSnapshotConflictException() : base("Документ или реквизиты компании изменились либо сейчас заняты. Обновите карточку и повторите действие.") { }
    public SellerSnapshotConflictException(Exception inner) : base("Документ или реквизиты компании изменились либо сейчас заняты. Обновите карточку и повторите действие.", inner) { }
}

public sealed class SellerSelectionException(string message) : Exception(message);

/// <summary>Короткая транзакция снимка. Не меняет финансовые lease и не повторяет занятую операцию.</summary>
internal sealed class DocumentSellerLease : IAsyncDisposable
{
    private const int MaximumPayments = 5000;
    private readonly PaymentContext db;
    private readonly IDbContextTransaction? ownedTransaction;
    private readonly SqliteTransaction? nativeTransaction;
    private readonly bool closeConnection;
    private bool committed;
    private bool cleanTrackerAdmitted;

    private DocumentSellerLease(PaymentContext db, IDbContextTransaction? transaction,
        SqliteTransaction? native = null, bool close = false)
        => (this.db, ownedTransaction, nativeTransaction, closeConnection) = (db, transaction, native, close);

    public static async Task<T> RunAsync<T>(PaymentContext db, Func<DocumentSellerLease, Task<T>> action, CancellationToken ct)
    {
        DocumentSellerLease? lease = null;
        try
        {
            lease = await BeginForCreationAsync(db, ct);
            var result = await action(lease);
            await lease.CommitAsync(ct);
            return result;
        }
        catch (Exception error) when (error is not SellerSnapshotConflictException && IsConflict(error))
        {
            throw new SellerSnapshotConflictException(error);
        }
        finally { if (lease is not null) await lease.DisposeAsync(); }
    }

    internal static async Task<DocumentSellerLease> BeginForCreationAsync(PaymentContext db, CancellationToken ct)
    {
        try { return await BeginAsync(db, ct); }
        catch (Exception error) when (IsConflict(error)) { throw new SellerSnapshotConflictException(error); }
    }

    internal async Task CommitAsync(CancellationToken ct)
    {
        try
        {
            if (ownedTransaction is not null) await ownedTransaction.CommitAsync(ct);
            committed = true;
        }
        catch (Exception error) when (IsConflict(error)) { throw new SellerSnapshotConflictException(error); }
    }

    private static async Task<DocumentSellerLease> BeginAsync(PaymentContext db, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql() && !db.Database.IsSqlite())
            throw new NotSupportedException("Снимки продавца поддерживаются только SQLite и PostgreSQL.");
        if (db.Database.CurrentTransaction is not null) return new(db, null);
        if (db.Database.IsNpgsql()) return new(db, await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct));

        var connection = (SqliteConnection)db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        var timeout = connection.DefaultTimeout;
        SqliteTransaction? native = null;
        try
        {
            if (opened) await db.Database.OpenConnectionAsync(ct);
            ct.ThrowIfCancellationRequested();
            // Zero is unlimited in this provider. Admission has a bounded provider retry;
            // there is no application retry and no deferred read-to-write promotion.
            connection.DefaultTimeout = 1;
            native = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
            ct.ThrowIfCancellationRequested();
            var transaction = await db.Database.UseTransactionAsync(native, ct)
                ?? throw new InvalidOperationException("Транзакция снимка не подключена.");
            return new(db, transaction, native, opened);
        }
        catch
        {
            if (native is not null) await native.DisposeAsync();
            if (opened) await db.Database.CloseConnectionAsync();
            throw;
        }
        finally { connection.DefaultTimeout = timeout; }
    }

    public static void RequireCleanTracker(PaymentContext db)
    {
        db.ChangeTracker.DetectChanges();
        if (db.ChangeTracker.Entries().Any(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
            throw new SellerSnapshotConflictException();
    }

    public void AdmitCleanTracker()
    {
        RequireCleanTracker(db);
        cleanTrackerAdmitted = true;
    }

    public async Task<IDocumentSeller[]> LockDocumentAsync(IDocumentSeller document, CancellationToken ct)
    {
        if (document is Payment payment)
        {
            var invoiceId = await db.Payments.AsNoTracking().Where(p => p.Id == payment.Id)
                .Select(p => new { p.InvoiceId }).SingleOrDefaultAsync(ct)
                ?? throw new SellerSnapshotConflictException();
            if (db.Database.IsNpgsql() && invoiceId.InvoiceId.HasValue)
                await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"Invoices\" WHERE \"Id\" = {invoiceId.InvoiceId.Value} FOR UPDATE NOWAIT").ToArrayAsync(ct);
            var ids = await PaymentIdsAsync(payment.Id, invoiceId.InvoiceId, ct);
            var visibleBefore = await VisiblePaymentRootsAsync(ids, ct);
            if (db.Database.IsNpgsql())
                await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"Payments\" WHERE \"Id\" = ANY({ids}) ORDER BY \"Id\" FOR UPDATE NOWAIT").ToArrayAsync(ct);
            if (!ids.SequenceEqual(await PaymentIdsAsync(payment.Id, invoiceId.InvoiceId, ct))) throw new SellerSnapshotConflictException();
            if (!visibleBefore.SequenceEqual(await VisiblePaymentRootsAsync(ids, ct))) throw new SellerSnapshotConflictException();
            var rows = await db.Payments.AsNoTracking().Where(p => ids.Contains(p.Id)).OrderBy(p => p.Id)
                .Select(p => new Payment
                {
                    Id = p.Id,
                    InvoiceId = p.InvoiceId,
                    Account = p.Account,
                    Type = p.Type,
                    DocumentKind = p.DocumentKind,
                    ClientId = p.ClientId,
                    LegalEntityId = p.LegalEntityId,
                    SellerCompanyId = p.SellerCompanyId,
                    SellerBranchId = p.SellerBranchId,
                    SellerSnapshotJson = p.SellerSnapshotJson,
                    SellerSnapshotPolicy = p.SellerSnapshotPolicy
                }).ToArrayAsync(ct);
            var current = rows.SingleOrDefault(p => p.Id == payment.Id) ?? throw new SellerSnapshotConflictException();
            if (current.InvoiceId != invoiceId.InvoiceId || current.Type != payment.Type
                || current.DocumentKind != payment.DocumentKind || current.ClientId != payment.ClientId
                || current.LegalEntityId != payment.LegalEntityId) throw new SellerSnapshotConflictException();
            return rows;
        }
        if (document is Act act)
        {
            if (db.Database.IsNpgsql())
                await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"Acts\" WHERE \"Id\" = {act.Id} FOR UPDATE NOWAIT").ToArrayAsync(ct);
            var current = await db.Acts.AsNoTracking().Where(a => a.Id == act.Id)
                .Select(a => new Act
                {
                    Id = a.Id,
                    Number = a.Number,
                    InvoiceId = a.InvoiceId,
                    ClientId = a.ClientId,
                    LegalEntityId = a.LegalEntityId,
                    SellerCompanyId = a.SellerCompanyId,
                    SellerBranchId = a.SellerBranchId,
                    SellerSnapshotJson = a.SellerSnapshotJson,
                    SellerSnapshotPolicy = a.SellerSnapshotPolicy
                }).SingleOrDefaultAsync(ct)
                ?? throw new SellerSnapshotConflictException();
            if (current.ClientId != act.ClientId || current.LegalEntityId != act.LegalEntityId || current.InvoiceId != act.InvoiceId)
                throw new SellerSnapshotConflictException();
            return [current];
        }
        if (document is Contract contract)
        {
            if (db.Database.IsNpgsql())
                await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"Contracts\" WHERE \"Id\" = {contract.Id} FOR UPDATE NOWAIT").ToArrayAsync(ct);
            return [await db.Contracts.AsNoTracking().Where(c => c.Id == contract.Id)
                .Select(c => new Contract { Id = c.Id, Number = c.Number, SellerCompanyId = c.SellerCompanyId,
                    SellerBranchId = c.SellerBranchId, SellerSnapshotJson = c.SellerSnapshotJson,
                    SellerSnapshotPolicy = c.SellerSnapshotPolicy }).SingleOrDefaultAsync(ct)
                ?? throw new SellerSnapshotConflictException()];
        }
        throw new NotSupportedException("Неизвестный документ продавца.");
    }

    private async Task<int[]> PaymentIdsAsync(int id, int? invoiceId, CancellationToken ct)
    {
        // Standalone raw lockset only. Never compose IgnoreQueryFilters into an
        // authority query: hidden siblings are locked, never exposed or written.
        var ids = await db.Payments.IgnoreQueryFilters().AsNoTracking().Where(p => p.Id == id
                || invoiceId.HasValue && p.InvoiceId == invoiceId && p.DocumentKind == DocumentKind.Invoice)
            .OrderBy(p => p.Id).Select(p => p.Id).Take(MaximumPayments + 1).ToArrayAsync(ct);
        if (ids.Length > MaximumPayments) throw new SellerSelectionException("Слишком много платежей одного счёта для фиксации реквизитов.");
        return ids;
    }

    private sealed record VisiblePaymentRoot(int Id, int? InvoiceId, PaymentType Type, DocumentKind DocumentKind,
        int? ClientId, int? LegalEntityId);

    private Task<VisiblePaymentRoot[]> VisiblePaymentRootsAsync(int[] ids, CancellationToken ct)
        => db.Payments.AsNoTracking().Where(payment => ids.Contains(payment.Id)).OrderBy(payment => payment.Id)
            .Select(payment => new VisiblePaymentRoot(payment.Id, payment.InvoiceId, payment.Type, payment.DocumentKind,
                payment.ClientId, payment.LegalEntityId)).ToArrayAsync(ct);

    public async Task<PaymentType?> ReadLockedActInvoiceTypeAsync(Act act, CancellationToken ct)
    {
        if (!act.InvoiceId.HasValue) return act.Invoice?.Type;
        var id = act.InvoiceId.Value;
        if (db.Database.IsNpgsql())
            await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"Payments\" WHERE \"Id\" = {id} FOR SHARE NOWAIT").ToArrayAsync(ct);
        var type = await db.Payments.AsNoTracking().Where(payment => payment.Id == id)
            .Select(payment => (PaymentType?)payment.Type).SingleOrDefaultAsync(ct);
        // The renderer still holds the observed side. Never freeze one side and stamp the other.
        if (act.Invoice is not null && type != act.Invoice.Type) throw new SellerSnapshotConflictException();
        return type;
    }

    public async Task LockCompanyAsync(int companyId, int? branchId, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql())
        {
            if (await db.AddressRecords.AsNoTracking().Where(address => address.LegalEntityId == companyId)
                .Select(address => address.Id).Take(MaximumPayments + 1).CountAsync(ct) > MaximumPayments)
                throw new SellerSelectionException("Слишком много адресов компании для одного снимка реквизитов.");
            return;
        }
        // Registry/default membership, profile, addresses and branch are reread only
        // after these short locks. UPDATE (not SHARE) also serializes image inserts.
        await LockRegistryAsync(ct);
        await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"LegalEntities\" WHERE \"Id\" = {companyId} FOR UPDATE NOWAIT").ToArrayAsync(ct);
        var addresses = await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"AddressRecords\" WHERE \"LegalEntityId\" = {companyId} ORDER BY \"Id\" LIMIT 5001 FOR SHARE NOWAIT").ToArrayAsync(ct);
        if (addresses.Length > MaximumPayments) throw new SellerSelectionException("Слишком много адресов компании для одного снимка реквизитов.");
        if (branchId.HasValue)
            await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"CompanyBranches\" WHERE \"Id\" = {branchId.Value} FOR SHARE NOWAIT").ToArrayAsync(ct);
    }

    public async Task LockRegistryAsync(CancellationToken ct)
    {
        if (!db.Database.IsNpgsql()) return;
        var keys = new[] { "legalEntities.mainCompanyId", "legalEntities.mainCompanyIds", "legalEntities.defaultMainCompanyId" };
        await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"SystemSettings\" WHERE \"Key\" = ANY({keys}) ORDER BY \"Id\" FOR SHARE NOWAIT").ToArrayAsync(ct);
    }

    private static bool IsConflict(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected }
                || current is SqliteException { SqliteErrorCode: 5 or 6 } || current is DbUpdateConcurrencyException) return true;
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (ownedTransaction is not null && !committed)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await ownedTransaction.RollbackAsync(timeout.Token);
                // Admission was clean; do not leave rolled-back seller/audit writes in
                // a reused context. A rejected dirty admission never sets this flag.
                if (cleanTrackerAdmitted) db.ChangeTracker.Clear();
            }
        }
        finally
        {
            if (ownedTransaction is not null) await ownedTransaction.DisposeAsync();
            if (nativeTransaction is not null) await nativeTransaction.DisposeAsync();
            if (closeConnection) await db.Database.CloseConnectionAsync();
        }
    }
}
