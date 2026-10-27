using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PayPlanner.Api.Data;

namespace PayPlanner.Api.Services.Companies;

/// <summary>
/// Записывает только immutable-версию, не вызывая SaveChanges и не меняя pending state вызывающего контекста.
/// Транзакцией документа и всеми тремя изображениями владеет DocumentSellerService/его вызывающий код.
/// </summary>
internal static class CompanyBrandImageVersionStore
{
    public static async Task<int> GetOrCreateAsync(PaymentContext db, int companyId, string kind, string hash, string dataUrl, CancellationToken ct)
    {
        if (!db.Database.IsSqlite() && !db.Database.IsNpgsql())
            throw new NotSupportedException("Версии изображений поддерживаются только SQLite и PostgreSQL.");
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Версия изображения требует транзакцию снимка документа.");

        var existing = await FindAsync(db, companyId, kind, hash, ct);
        if (existing.HasValue) return existing.Value;

        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction.GetDbTransaction();
        command.CommandTimeout = db.Database.GetCommandTimeout() ?? 30;
        command.CommandText = "INSERT INTO \"CompanyBrandImageVersions\" (\"LegalEntityId\", \"Kind\", \"Sha256\", \"DataUrl\", \"CreatedAt\") VALUES (@company, @kind, @hash, @data, @created) ON CONFLICT (\"LegalEntityId\", \"Kind\", \"Sha256\") DO NOTHING RETURNING \"Id\"";
        Add(command, "company", companyId);
        Add(command, "kind", kind);
        Add(command, "hash", hash);
        Add(command, "data", dataUrl);
        // Обе существующие схемы хранят CreatedAt без часового пояса; значение остаётся UTC по соглашению модели.
        Add(command, "created", DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified));
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                var value = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                if (value is <= 0 or > int.MaxValue || await reader.ReadAsync(ct))
                    throw new InvalidOperationException("Не удалось получить номер версии изображения.");
                return (int)value;
            }
        }

        // Только точный unique conflict имеет reconciliation; ошибки записи/отмены не перехватываются.
        return await FindAsync(db, companyId, kind, hash, ct)
            ?? throw new InvalidOperationException("Созданная версия изображения не найдена.");
    }

    private static Task<int?> FindAsync(PaymentContext db, int companyId, string kind, string hash, CancellationToken ct)
        => db.CompanyBrandImageVersions.AsNoTracking()
            .Where(item => item.LegalEntityId == companyId && item.Kind == kind && item.Sha256 == hash)
            .Select(item => (int?)item.Id).FirstOrDefaultAsync(ct);

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
