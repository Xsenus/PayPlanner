using System.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;

namespace PayPlanner.Api.Services.Invoices;

/// <summary>Short source leases for direct act writes and their contract relation writers.</summary>
internal sealed class FinancialSourceLease : IAsyncDisposable
{
    // Fail closed on an oversized graph; never qualify only a truncated subset.
    private const int MaximumRoots = 5000;
    private readonly PaymentContext db;
    private readonly IDbContextTransaction transaction;
    private readonly SqliteTransaction? sqliteTransaction;
    private readonly bool closeConnection;
    private bool committed;
    private bool commitStarted;
    private bool trackerAdmitted;

    private FinancialSourceLease(PaymentContext db, IDbContextTransaction transaction,
        SqliteTransaction? sqliteTransaction = null, bool closeConnection = false)
    {
        this.db = db;
        this.transaction = transaction;
        this.sqliteTransaction = sqliteTransaction;
        this.closeConnection = closeConnection;
    }

    public static async Task<ActionResult<T>> RunAsync<T>(PaymentContext db,
        Func<FinancialSourceLease, Task<ActionResult<T>>> action, CancellationToken ct)
    {
        FinancialSourceLease? lease = null;
        try
        {
            // These five request paths have only read/preflight work before admission.
            // Never save, detach, or discard unrelated pending work from a reused context.
            RequireCleanTracker(db);
            lease = await BeginAsync(db, ct);
            return await action(lease);
        }
        catch (Exception exception) when (lease?.commitStarted != true && IsConflict(exception))
        {
            return new ConflictObjectResult(new
            {
                message = "Связанные данные изменились или сейчас заняты. Обновите карточку и повторите сохранение.",
            });
        }
        catch (SourceGraphTooLargeException) when (lease?.commitStarted != true)
        {
            return new BadRequestObjectResult(new
            {
                message = "Слишком много связанных документов для одного сохранения. Уменьшите объём операции.",
            });
        }
        finally
        {
            if (lease is not null) await lease.DisposeAsync();
        }
    }

    private static async Task<FinancialSourceLease> BeginAsync(PaymentContext db, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Financial source mutation requires its own request transaction.");

        if (db.Database.IsNpgsql())
            return new FinancialSourceLease(db, await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct));

        if (!db.Database.IsSqlite())
            throw new InvalidOperationException("Unsupported financial source lease provider.");

        var connection = (SqliteConnection)db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        SqliteTransaction? native = null;
        var timeout = connection.DefaultTimeout;
        try
        {
            if (opened) await db.Database.OpenConnectionAsync(ct);
            // SQLite admission is BEGIN IMMEDIATE, not PostgreSQL row-lock semantics.
            // The provider may retry busy admission for at most this short timeout;
            // zero means unlimited waiting. No application retry or snapshot promotion.
            ct.ThrowIfCancellationRequested();
            connection.DefaultTimeout = 1;
            native = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
            ct.ThrowIfCancellationRequested();
            var transaction = await db.Database.UseTransactionAsync(native, ct)
                ?? throw new InvalidOperationException("SQLite financial transaction was not enlisted.");
            return new FinancialSourceLease(db, transaction, native, opened);
        }
        catch
        {
            if (native is not null) await native.DisposeAsync();
            if (opened) await db.Database.CloseConnectionAsync();
            throw;
        }
        finally { connection.DefaultTimeout = timeout; }
    }

    public async Task CommitAsync(CancellationToken ct)
    {
        commitStarted = true;
        await transaction.CommitAsync(ct);
        committed = true;
    }

    public async Task AcquireActAsync(int? actId, int? previousInvoiceId, UpsertActRequest request, CancellationToken ct)
    {
        var before = await ReadActGraphAsync(actId, previousInvoiceId, request, ct);
        await LockGraphAsync(before, ct);
        if (!before.SameAs(await ReadActGraphAsync(actId, previousInvoiceId, request, ct)))
            throw new SourceChangedException();
        RefreshCleanTracker();
    }

    public async Task AcquireContractAsync(int? contractId, UpsertContractRequest? request, CancellationToken ct)
    {
        // Child DELETE does not lock its parent. Participate before any authoritative
        // relation/canonical-set read, even when a save would change only child rows.
        await LockAsync(Root.Contracts, Ids([contractId ?? 0]), ct);
        var before = await ReadContractGraphAsync(contractId, request, ct);
        await LockGraphAsync(before, ct);
        if (!before.SameAs(await ReadContractGraphAsync(contractId, request, ct)))
            throw new SourceChangedException();
        RefreshCleanTracker();
    }

    private static void RequireCleanTracker(PaymentContext db)
    {
        db.ChangeTracker.DetectChanges();
        if (db.ChangeTracker.Entries().Any(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
            throw new SourceChangedException();
    }

    private void RefreshCleanTracker()
    {
        // No business changes are legitimate before this point. Discard only the
        // clean read cache so authoritative roots and included relations are fresh.
        RequireCleanTracker(db);
        db.ChangeTracker.Clear();
        trackerAdmitted = true;
    }

    private async Task<Graph> ReadActGraphAsync(int? actId, int? previousInvoiceId, UpsertActRequest request, CancellationToken ct)
    {
        var acts = await db.Acts.AsNoTracking().Where(a => actId.HasValue && a.Id == actId.Value)
            .Select(a => new ActRoot(a.Id, a.InvoiceId, a.ClientId, a.LegalEntityId)).ToArrayAsync(ct);
        var seeds = Ids(acts.Select(a => a.InvoiceId ?? 0).Append(previousInvoiceId ?? 0).Append(request.InvoiceId ?? 0));
        var payments = await PaymentRootsAsync(seeds, null, ct);
        // Raw IDs must remain leased when a row is filtered away by soft-delete.
        // A fresh availability check then denies it while the raw lease prevents restore races.
        var paymentIds = Ids(payments.Select(p => p.Id).Concat(seeds));
        var actIds = Ids(acts.Select(a => a.Id).Append(actId ?? 0));
        var contractIds = await BoundedIdsAsync(db.ContractActs.AsNoTracking()
            .Where(l => actIds.Contains(l.ActId)).Select(l => l.ContractId)
            .Union(db.ContractInvoices.AsNoTracking().Where(l => paymentIds.Contains(l.InvoiceId)).Select(l => l.ContractId)), ct);
        return new Graph(paymentIds, actIds,
            Ids(contractIds.Concat(payments.Select(p => p.ContractId ?? 0)).Append(request.ContractId ?? 0)),
            Ids(payments.SelectMany(p => new[] { p.ClientId ?? 0, p.ExpenseClientId ?? 0 })
                .Concat(acts.Select(a => a.ClientId ?? 0)).Append(request.ClientId ?? 0)),
            Ids(payments.SelectMany(p => new[] { p.LegalEntityId ?? 0, p.ExpenseLegalEntityId ?? 0 })
                .Concat(acts.Select(a => a.LegalEntityId ?? 0)).Append(request.LegalEntityId ?? 0)));
    }

    private async Task<Graph> ReadContractGraphAsync(int? contractId, UpsertContractRequest? request, CancellationToken ct)
    {
        var selectedActs = Ids(request?.ActIds ?? []);
        var directActs = db.Acts.AsNoTracking().Where(a => selectedActs.Contains(a.Id)
            || db.ContractActs.Any(l => l.ContractId == contractId && l.ActId == a.Id));
        var directActRows = await BoundedActsAsync(directActs, ct);
        var selectedPayments = Ids((request?.InvoiceIds ?? []).Concat(request?.PaymentIds ?? [])
            .Concat(directActRows.Select(a => a.InvoiceId ?? 0)));
        var payments = await PaymentRootsAsync(selectedPayments, contractId, ct);
        var paymentIds = Ids(payments.Select(p => p.Id));
        var acts = await BoundedActsAsync(db.Acts.AsNoTracking().Where(a => selectedActs.Contains(a.Id)
            || db.ContractActs.Any(l => l.ContractId == contractId && l.ActId == a.Id)
            || a.InvoiceId.HasValue && paymentIds.Contains(a.InvoiceId.Value)), ct);
        var actIds = Ids(acts.Select(a => a.Id).Concat(selectedActs));
        var otherContracts = await BoundedIdsAsync(db.ContractActs.AsNoTracking()
            .Where(l => actIds.Contains(l.ActId)).Select(l => l.ContractId)
            .Union(db.ContractInvoices.AsNoTracking().Where(l => paymentIds.Contains(l.InvoiceId)).Select(l => l.ContractId)), ct);
        var clients = await BoundedIdsAsync(db.ClientContracts.AsNoTracking()
            .Where(l => l.ContractId == contractId).Select(l => l.ClientId), ct);
        var companies = await BoundedIdsAsync(db.LegalEntityContracts.AsNoTracking()
            .Where(l => l.ContractId == contractId).Select(l => l.LegalEntityId), ct);
        return new Graph(Ids(paymentIds.Concat(selectedPayments)), actIds,
            Ids(otherContracts.Concat(payments.Select(p => p.ContractId ?? 0)).Append(contractId ?? 0)),
            Ids(clients.Concat(payments.SelectMany(p => new[] { p.ClientId ?? 0, p.ExpenseClientId ?? 0 }))
                .Concat(acts.Select(a => a.ClientId ?? 0)).Concat(request?.Clients?.Select(c => c.ClientId) ?? [])),
            Ids(companies.Concat(payments.SelectMany(p => new[] { p.LegalEntityId ?? 0, p.ExpenseLegalEntityId ?? 0 }))
                .Concat(acts.Select(a => a.LegalEntityId ?? 0)).Concat(request?.LegalEntities?.Select(c => c.LegalEntityId) ?? [])));
    }

    private async Task<PaymentRoot[]> PaymentRootsAsync(int[] seedIds, int? contractId, CancellationToken ct)
    {
        var seeds = db.Payments.AsNoTracking().Where(p => seedIds.Contains(p.Id)
            || contractId.HasValue && (p.ContractId == contractId
                || db.ContractInvoices.Any(l => l.ContractId == contractId && l.InvoiceId == p.Id)
                || db.ContractPayments.Any(l => l.ContractId == contractId && l.PaymentId == p.Id)));
        var invoiceIds = seeds.Where(p => p.InvoiceId.HasValue).Select(p => p.InvoiceId!.Value);
        var rows = await db.Payments.AsNoTracking()
            .Where(p => seeds.Any(s => s.Id == p.Id) || p.InvoiceId.HasValue && invoiceIds.Contains(p.InvoiceId.Value))
            .OrderBy(p => p.Id).Take(MaximumRoots + 1)
            .Select(p => new PaymentRoot(p.Id, p.ContractId, p.ClientId, p.LegalEntityId, p.ExpenseClientId, p.ExpenseLegalEntityId))
            .ToArrayAsync(ct);
        EnsureBound(rows.Length);
        return rows;
    }

    private static async Task<ActRoot[]> BoundedActsAsync(IQueryable<Act> query, CancellationToken ct)
    {
        var rows = await query.OrderBy(a => a.Id).Take(MaximumRoots + 1)
            .Select(a => new ActRoot(a.Id, a.InvoiceId, a.ClientId, a.LegalEntityId)).ToArrayAsync(ct);
        EnsureBound(rows.Length);
        return rows;
    }

    private static async Task<int[]> BoundedIdsAsync(IQueryable<int> query, CancellationToken ct)
    {
        var ids = await query.Distinct().OrderBy(id => id).Take(MaximumRoots + 1).ToArrayAsync(ct);
        EnsureBound(ids.Length);
        return ids;
    }

    private async Task LockGraphAsync(Graph graph, CancellationToken ct)
    {
        EnsureBound(graph.Payments.Length + graph.Acts.Length + graph.Contracts.Length
            + graph.Clients.Length + graph.Companies.Length);
        await LockAsync(Root.Payments, graph.Payments, ct);
        await LockAsync(Root.Acts, graph.Acts, ct);
        await LockAsync(Root.Contracts, graph.Contracts, ct);
        await LockAsync(Root.Clients, graph.Clients, ct);
        await LockAsync(Root.LegalEntities, graph.Companies, ct);
    }

    private async Task LockAsync(Root root, int[] ids, CancellationToken ct)
    {
        if (ids.Length == 0 || db.Database.IsSqlite()) return;
        // NOWAIT applies to row locks. PostgreSQL may still wait on table/DDL locks.
        // Every new root lease is nonblocking, including cross-contract admission.
        var query = root switch
        {
            Root.Payments => db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"Payments\" WHERE \"Id\" = ANY({ids}) ORDER BY \"Id\" FOR UPDATE NOWAIT"),
            Root.Acts => db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"Acts\" WHERE \"Id\" = ANY({ids}) ORDER BY \"Id\" FOR UPDATE NOWAIT"),
            Root.Contracts => db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"Contracts\" WHERE \"Id\" = ANY({ids}) ORDER BY \"Id\" FOR UPDATE NOWAIT"),
            Root.Clients => db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"Clients\" WHERE \"Id\" = ANY({ids}) ORDER BY \"Id\" FOR UPDATE NOWAIT"),
            Root.LegalEntities => db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"LegalEntities\" WHERE \"Id\" = ANY({ids}) ORDER BY \"Id\" FOR UPDATE NOWAIT"),
            _ => throw new ArgumentOutOfRangeException(nameof(root)),
        };
        await query.ToArrayAsync(ct);
    }

    private static int[] Ids(IEnumerable<int> ids)
    {
        var result = ids.Where(id => id > 0).Distinct().Take(MaximumRoots + 1).OrderBy(id => id).ToArray();
        EnsureBound(result.Length);
        return result;
    }

    private static void EnsureBound(int count)
    {
        if (count > MaximumRoots) throw new SourceGraphTooLargeException();
    }

    private static bool IsConflict(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is SourceChangedException or DbUpdateConcurrencyException
                || current is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.LockNotAvailable }
                || current is SqliteException { SqliteErrorCode: 5 or 6 }) return true;
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!committed)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await transaction.RollbackAsync(timeout.Token);
                if (!commitStarted && trackerAdmitted) db.ChangeTracker.Clear();
            }
        }
        finally
        {
            await transaction.DisposeAsync();
            if (sqliteTransaction is not null) await sqliteTransaction.DisposeAsync();
            if (closeConnection) await db.Database.CloseConnectionAsync();
        }
    }

    private enum Root { Payments, Acts, Contracts, Clients, LegalEntities }
    private sealed class SourceChangedException : Exception { }
    private sealed class SourceGraphTooLargeException : Exception { }
    private sealed record ActRoot(int Id, int? InvoiceId, int? ClientId, int? LegalEntityId);
    private sealed record PaymentRoot(int Id, int? ContractId, int? ClientId, int? LegalEntityId, int? ExpenseClientId, int? ExpenseLegalEntityId);
    private sealed record Graph(int[] Payments, int[] Acts, int[] Contracts, int[] Clients, int[] Companies)
    {
        public bool SameAs(Graph other) => Payments.SequenceEqual(other.Payments) && Acts.SequenceEqual(other.Acts)
            && Contracts.SequenceEqual(other.Contracts) && Clients.SequenceEqual(other.Clients) && Companies.SequenceEqual(other.Companies);
    }
}
