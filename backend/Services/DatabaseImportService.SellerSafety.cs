using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Companies;

namespace PayPlanner.Api.Services;

public sealed partial class DatabaseImportService
{
    private const string SellerMergeUnsupported = "Импорт остановлен до записи: сохранённые реквизиты документов или филиалы требуют отдельного сопоставления. Используйте полное восстановление резервной копии либо импорт без этих документов. Данные не изменены.";

    // One fixed read-only model, with its own runtime type/cache key. Policy is
    // read through the inspected source schema below, so older SQLite files do
    // not acquire a new missing-column SELECT and are never altered or migrated.
    private sealed class SellerPolicyImportSourceContext : PaymentContext
    {
        public SellerPolicyImportSourceContext(DbContextOptions options) : base(options, useObjectAuditModel: false)
            => ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Payment>().Ignore(document => document.SellerSnapshotPolicy);
            modelBuilder.Entity<Act>().Ignore(document => document.SellerSnapshotPolicy);
            modelBuilder.Entity<Contract>().Ignore(document => document.SellerSnapshotPolicy);
        }
    }

    private static async Task InspectSourceSellerStateAsync(PaymentContext source, ImportMaps maps, CancellationToken ct)
    {
        var timing = await source.SystemSettings.AsNoTracking()
            .Where(row => row.Key == DocumentSellerSnapshotPolicy.SettingKey).Select(row => row.Value).SingleOrDefaultAsync(ct);
        if (timing is not null && !DocumentSellerSnapshotPolicy.IsValid(timing))
            throw new InvalidOperationException(SellerMergeUnsupported);
        var connection = source.Database.GetDbConnection();
        // These names are code constants, never file/request input.
        foreach (var table in new[] { "Payments", "Acts", "Contracts" })
        {
            var columns = await GetSqliteTableColumnsAsync(connection, table, ct);
            if (columns.Count == 0) continue;
            var unsupported = new List<string>();
            if (columns.Contains("SellerSnapshotJson")) unsupported.Add("(\"SellerSnapshotJson\" IS NOT NULL AND TRIM(\"SellerSnapshotJson\") <> '')");
            if (columns.Contains("SellerBranchId")) unsupported.Add("\"SellerBranchId\" IS NOT NULL");
            if (unsupported.Count > 0)
            {
                await using var check = connection.CreateCommand();
                check.Transaction = source.Database.CurrentTransaction!.GetDbTransaction();
                check.CommandText = $"SELECT 1 FROM \"{table}\" WHERE {string.Join(" OR ", unsupported)} LIMIT 1";
                if (await check.ExecuteScalarAsync(ct) is not null) throw new InvalidOperationException(SellerMergeUnsupported);
            }
            if (!columns.Contains("SellerSnapshotPolicy")) continue;
            await using var command = connection.CreateCommand();
            command.Transaction = source.Database.CurrentTransaction!.GetDbTransaction();
            command.CommandText = $"SELECT \"Id\", \"SellerSnapshotPolicy\" FROM \"{table}\" WHERE \"SellerSnapshotPolicy\" IS NOT NULL";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var value = reader.GetString(1);
                if (!DocumentSellerSnapshotPolicy.IsValid(value)) throw new InvalidOperationException(SellerMergeUnsupported);
                // On-create without a snapshot is not a valid historical capture.
                if (value == DocumentSellerSnapshotPolicy.OnCreate) throw new InvalidOperationException(SellerMergeUnsupported);
                maps.SellerPolicies[(table, reader.GetInt32(0))] = value;
            }
        }
    }

    private static async Task InspectTargetSellerStateAsync(PaymentContext source, PaymentContext target,
        InvoiceImportKeys invoiceKeys, CancellationToken ct)
    {
        var sourceInvoiceKeys = invoiceKeys.Source.Values.ToHashSet(StringComparer.Ordinal);
        var sourceInvoices = await source.Invoices.AsNoTracking().Select(invoice => new { invoice.Number, invoice.Date }).ToArrayAsync(ct);
        var invoiceNumbers = sourceInvoices.Select(invoice => invoice.Number).Distinct().ToArray();
        var invoiceDates = sourceInvoices.Select(invoice => Key(invoice.Number, invoice.Date)).ToHashSet(StringComparer.Ordinal);
        var targetInvoices = await target.Invoices.IgnoreQueryFilters().AsNoTracking()
            .Where(invoice => invoiceNumbers.Contains(invoice.Number)
                && invoice.Payments.Any(payment => payment.SellerBranchId != null
                    || payment.SellerSnapshotJson != null && payment.SellerSnapshotJson.Trim() != ""))
            .Select(invoice => new { invoice.Id, invoice.Number, invoice.Date }).ToArrayAsync(ct);
        if (targetInvoices.Any(invoice => invoiceKeys.Target.TryGetValue(invoice.Id, out var identity)
            ? sourceInvoiceKeys.Contains(identity) : invoiceDates.Contains(Key(invoice.Number, invoice.Date))))
            throw new InvalidOperationException(SellerMergeUnsupported);

        // Acts lack a branch/immutable-asset importer. Conservative Number+Date
        // matching also protects hidden/soft-deleted financial traces; it can
        // reject an unrelated equal-number document until full remap is supported.
        var sourceActs = await source.Acts.AsNoTracking().Select(act => new { act.Number, act.Date }).ToArrayAsync(ct);
        var actNumbers = sourceActs.Select(act => act.Number).Distinct().ToArray();
        var actDates = sourceActs.Select(act => Key(act.Number, act.Date)).ToHashSet(StringComparer.Ordinal);
        var targetActs = await target.Acts.IgnoreQueryFilters().AsNoTracking().Where(act => actNumbers.Contains(act.Number)
                && (act.SellerBranchId != null || act.SellerSnapshotJson != null && act.SellerSnapshotJson.Trim() != ""))
            .Select(act => new { act.Number, act.Date }).ToArrayAsync(ct);
        if (targetActs.Any(act => actDates.Contains(Key(act.Number, act.Date)))) throw new InvalidOperationException(SellerMergeUnsupported);

        var sourceContracts = await source.Contracts.AsNoTracking().Select(contract => new { contract.Number, contract.Date }).ToArrayAsync(ct);
        var contractNumbers = sourceContracts.Select(contract => contract.Number).Distinct().ToArray();
        var contractDates = sourceContracts.Select(contract => Key(contract.Number, contract.Date)).ToHashSet(StringComparer.Ordinal);
        var targetContracts = await target.Contracts.IgnoreQueryFilters().AsNoTracking().Where(contract => contractNumbers.Contains(contract.Number)
                && (contract.SellerBranchId != null || contract.SellerSnapshotJson != null && contract.SellerSnapshotJson.Trim() != ""))
            .Select(contract => new { contract.Number, contract.Date }).ToArrayAsync(ct);
        if (targetContracts.Any(contract => contractDates.Contains(Key(contract.Number, contract.Date)))) throw new InvalidOperationException(SellerMergeUnsupported);
    }

    private static void CopySellerState(IDocumentSeller source, IDocumentSeller target, string table, int sourceId, int targetId, ImportMaps maps)
    {
        // A second guard protects against a newly observed unsupported target.
        // The outer importer rolls back every table; never clear an issued winner.
        if (!string.IsNullOrWhiteSpace(source.SellerSnapshotJson) || source.SellerBranchId.HasValue
            || !string.IsNullOrWhiteSpace(target.SellerSnapshotJson) || target.SellerBranchId.HasValue)
            throw new InvalidOperationException(SellerMergeUnsupported);
        target.SellerCompanyId = MapId(maps.LegalEntities, source.SellerCompanyId);
        if (targetId == 0) target.SellerSnapshotPolicy = maps.SellerPolicies.GetValueOrDefault((table, sourceId));
    }
}
