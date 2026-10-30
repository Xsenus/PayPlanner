using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Text.Json.Nodes;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Companies;

namespace PayPlanner.Api.Services;

public sealed partial class DatabaseImportService
{
    private const string SellerMergeUnsupported = "Импорт остановлен до записи: реквизиты, изображения, подразделения или существующие документы нельзя сопоставить однозначно. Проверьте собственные компании, счета и пользователей целевой базы либо используйте полное восстановление резервной копии. Данные не изменены.";

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
            string Column(string name) => columns.Contains(name) ? $"\"{name}\"" : "NULL";
            await using var command = connection.CreateCommand();
            command.Transaction = source.Database.CurrentTransaction!.GetDbTransaction();
            command.CommandText = $"SELECT \"Id\", {Column("SellerSnapshotPolicy")}, {Column("SellerCompanyId")}, {Column("SellerBranchId")}, {Column("SellerSnapshotJson")} FROM \"{table}\"";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetInt32(0);
                var value = reader.IsDBNull(1) ? null : reader.GetString(1);
                int? company = reader.IsDBNull(2) ? null : reader.GetInt32(2);
                int? branch = reader.IsDBNull(3) ? null : reader.GetInt32(3);
                var snapshot = reader.IsDBNull(4) ? null : reader.GetString(4);
                if (value is not null && !DocumentSellerSnapshotPolicy.IsValid(value)) throw new InvalidOperationException(SellerMergeUnsupported);
                if (value == DocumentSellerSnapshotPolicy.OnCreate && string.IsNullOrWhiteSpace(snapshot)) throw new InvalidOperationException(SellerMergeUnsupported);
                maps.SellerPolicies[(table, id)] = value;
                if (company.HasValue || branch.HasValue || !string.IsNullOrWhiteSpace(snapshot))
                    maps.SellerHistory.Add((table, id), InspectSellerHistory(company, branch, snapshot, value));
            }
        }
    }

    private static async Task InspectTargetSellerStateAsync(PaymentContext source, PaymentContext target,
        InvoiceImportKeys invoiceKeys, ImportMaps maps, CancellationToken ct)
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

        if (maps.SellerHistory.Count > 0 && invoiceKeys.Source.Values.Intersect(invoiceKeys.Target.Values, StringComparer.Ordinal).Any())
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
        if (maps.SellerHistory.Count > 0 && await target.Acts.IgnoreQueryFilters().AnyAsync(act => actNumbers.Contains(act.Number), ct))
        {
            var candidates = await target.Acts.IgnoreQueryFilters().AsNoTracking().Where(act => actNumbers.Contains(act.Number))
                .Select(act => new { act.Number, act.Date }).ToArrayAsync(ct);
            if (candidates.Any(act => actDates.Contains(Key(act.Number, act.Date)))) throw new InvalidOperationException(SellerMergeUnsupported);
        }

        var sourceContracts = await source.Contracts.AsNoTracking().Select(contract => new { contract.Number, contract.Date }).ToArrayAsync(ct);
        var contractNumbers = sourceContracts.Select(contract => contract.Number).Distinct().ToArray();
        var contractDates = sourceContracts.Select(contract => Key(contract.Number, contract.Date)).ToHashSet(StringComparer.Ordinal);
        var targetContracts = await target.Contracts.IgnoreQueryFilters().AsNoTracking().Where(contract => contractNumbers.Contains(contract.Number)
                && (contract.SellerBranchId != null || contract.SellerSnapshotJson != null && contract.SellerSnapshotJson.Trim() != ""))
            .Select(contract => new { contract.Number, contract.Date }).ToArrayAsync(ct);
        if (targetContracts.Any(contract => contractDates.Contains(Key(contract.Number, contract.Date)))) throw new InvalidOperationException(SellerMergeUnsupported);
        if (maps.SellerHistory.Count > 0)
        {
            var candidates = await target.Contracts.IgnoreQueryFilters().AsNoTracking().Where(contract => contractNumbers.Contains(contract.Number))
                .Select(contract => new { contract.Id, contract.Number, contract.Date }).ToArrayAsync(ct);
            foreach (var candidate in candidates) maps.ExistingSellerContractIds.Add(candidate.Id);
            if (candidates.Any(contract => contractDates.Contains(Key(contract.Number, contract.Date)))) throw new InvalidOperationException(SellerMergeUnsupported);
        }
    }

    private static void CopySellerState(IDocumentSeller source, IDocumentSeller target, string table, int sourceId, int targetId, ImportMaps maps)
    {
        // A second guard protects against a newly observed unsupported target.
        // The outer importer rolls back every table; never clear an issued winner.
        if (!string.IsNullOrWhiteSpace(target.SellerSnapshotJson) || target.SellerBranchId.HasValue)
            throw new InvalidOperationException(SellerMergeUnsupported);
        if (maps.SellerHistory.TryGetValue((table, sourceId), out var history))
        {
            if (targetId != 0 || history.SnapshotHash != SnapshotHash(source.SellerSnapshotJson)) throw new InvalidOperationException(SellerMergeUnsupported);
            target.SellerCompanyId = maps.SellerCompanies[history.CompanyId];
            target.SellerBranchId = history.BranchId.HasValue ? maps.SellerBranches[history.BranchId.Value] : null;
            if (!string.IsNullOrWhiteSpace(source.SellerSnapshotJson))
            {
                var json = JsonNode.Parse(source.SellerSnapshotJson)!.AsObject();
                json["Id"] = target.SellerCompanyId.Value;
                foreach (var (property, _) in SellerImageProperties)
                    if (json[property] is JsonValue value && value.TryGetValue<int>(out var imageId)) json[property] = maps.SellerImageIds[imageId];
                var remapped = json.ToJsonString();
                if (remapped.Length > DocumentSellerService.MaxSnapshotLength) throw new InvalidOperationException(SellerMergeUnsupported);
                target.SellerSnapshotJson = remapped;
            }
            target.SellerSnapshotPolicy = maps.SellerPolicies.GetValueOrDefault((table, sourceId));
            return;
        }
        if (!string.IsNullOrWhiteSpace(source.SellerSnapshotJson) || source.SellerBranchId.HasValue) throw new InvalidOperationException(SellerMergeUnsupported);
        target.SellerCompanyId = MapId(maps.LegalEntities, source.SellerCompanyId);
        if (targetId == 0) target.SellerSnapshotPolicy = maps.SellerPolicies.GetValueOrDefault((table, sourceId));
    }
}
