using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PayPlanner.Api.Data;

namespace PayPlanner.Api.Services.Storage;

public enum GeneratedFinancialDocumentKind { Invoice, Act, Contract }
public sealed record GeneratedFinancialIdentity(int EntityId, int? InvoiceEntityId = null);

// Reserve only IDs, never visible financial rows. The caller must commit the IDs
// together with its trusted frozen attempt before provider I/O.
public sealed class GeneratedFinancialIdentityAllocator(PaymentContext db)
{
    public async Task<GeneratedFinancialIdentity> AllocateAsync(GeneratedFinancialDocumentKind kind, CancellationToken ct)
    {
        var table = kind switch
        {
            GeneratedFinancialDocumentKind.Invoice => "Payments",
            GeneratedFinancialDocumentKind.Act => "Acts",
            GeneratedFinancialDocumentKind.Contract => "Contracts",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Financial identity allocation requires its attempt transaction.");
        if (!db.Database.IsSqlite() && !db.Database.IsNpgsql())
            throw new NotSupportedException("Financial identity allocation requires SQLite or PostgreSQL.");
        var entityId = await NextAsync(table, ct);
        var invoiceId = kind == GeneratedFinancialDocumentKind.Invoice ? await NextAsync("Invoices", ct) : (int?)null;
        return new(entityId, invoiceId);
    }

    private async Task<int> NextAsync(string table, CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandTimeout = db.Database.GetCommandTimeout() ?? 30;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "table";
        parameter.Value = db.Database.IsNpgsql() ? $"\"{table}\"" : table;
        command.Parameters.Add(parameter);
        if (db.Database.IsNpgsql())
        {
            command.CommandText = "SELECT nextval(pg_get_serial_sequence(@table, 'Id'))";
            return await ReadSingleAsync(command, ct) ?? throw new InvalidOperationException("Missing financial identity sequence.");
        }
        // The table name is exclusively from the fixed server mapping above.
        // UPDATE acquires the SQLite writer lock even if this sequence is empty.
        command.CommandText = $"UPDATE sqlite_sequence SET seq = MAX(COALESCE(seq, 0), (SELECT COALESCE(MAX(\"Id\"), 0) FROM \"{table}\")) + 1 WHERE name = @table RETURNING seq";
        var value = await ReadSingleAsync(command, ct);
        if (value.HasValue) return value.Value;
        command.CommandText = $"INSERT INTO sqlite_sequence(name, seq) VALUES (@table, (SELECT COALESCE(MAX(\"Id\"), 0) + 1 FROM \"{table}\")) RETURNING seq";
        return await ReadSingleAsync(command, ct) ?? throw new InvalidOperationException("Missing reserved financial identity.");
    }

    private static async Task<int?> ReadSingleAsync(DbCommand command, CancellationToken ct)
    {
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        if (reader.IsDBNull(0)) throw new InvalidOperationException("Missing financial identity sequence.");
        var value = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
        if (value is <= 0 or > int.MaxValue || await reader.ReadAsync(ct))
            throw new InvalidOperationException("Financial identity sequence is invalid or exhausted.");
        return (int)value;
    }
}
