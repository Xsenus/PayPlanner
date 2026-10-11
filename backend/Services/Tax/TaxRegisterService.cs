using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Tax;

public sealed record TaxRegisterEntryInput(
    int LegalEntityId,
    string? Type,
    DateTime? OperationDate,
    decimal? Amount,
    string? DocumentRef,
    int? DocumentId,
    string? Description,
    string? Category,
    string? SubType,
    bool TaxBaseIncludesVat,
    int? BankStatementOperationId);

public sealed record TaxRegisterResult<T>(T? Value, string? Error = null, int Status = 200)
{
    public bool Ok => Error is null;
    public static TaxRegisterResult<T> Fail(string error, int status = 400) => new(default, error, status);
}

public sealed record TaxRegisterJournalItem(
    int EventId, int EntryId, string Action, string? FromStatus, string? ToStatus, int UserId, string? Comment,
    DateTime CreatedAt, string Direction, decimal SignedAmount, string? DocumentRef);

public sealed record TaxRegisterJournalPage(int Total, int Page, int PageSize, IReadOnlyList<TaxRegisterJournalItem> Items);

public sealed record TaxRegisterBookTotals(decimal Income, decimal Expense, decimal TaxBase, int Entries);

public sealed record TaxRegisterBook(
    int LegalEntityId,
    int Year,
    bool IsClosed,
    IReadOnlyList<TaxRegisterEntry> Entries,
    TaxRegisterBookTotals Totals);

public sealed record TaxRegisterBookPage(
    int LegalEntityId,
    int Year,
    bool IsClosed,
    TaxRegisterBookTotals Totals,
    int Page,
    int PageSize,
    IReadOnlyList<TaxRegisterEntry> Entries);

public interface ITaxRegisterService
{
    Task<TaxRegisterResult<TaxRegisterEntry>> CreateAsync(TaxRegisterEntryInput input, int userId, CancellationToken ct);
    Task<TaxRegisterResult<TaxRegisterEntry>> UpdateDraftAsync(int id, TaxRegisterEntryInput input, int userId, CancellationToken ct);
    Task<TaxRegisterResult<TaxRegisterEntry>> ApproveAsync(int id, int userId, CancellationToken ct);
    Task<TaxRegisterResult<TaxRegisterEntry>> ArchiveAsync(int id, int userId, string? comment, CancellationToken ct);
    Task<TaxRegisterResult<TaxRegisterEntry>> CorrectAsync(int id, string reason, int userId, CancellationToken ct);
    Task<TaxRegisterResult<TaxRegisterYearClose>> CloseYearAsync(int legalEntityId, int year, int userId, CancellationToken ct);
    Task<TaxRegisterBook> BuildBookAsync(int legalEntityId, int year, CancellationToken ct);
    Task<TaxRegisterBookPage> GetBookPageAsync(int legalEntityId, int year, int page, int pageSize, CancellationToken ct);
    IAsyncEnumerable<TaxRegisterEntry> StreamBookEntriesAsync(int legalEntityId, int year, CancellationToken ct);
    Task<bool> IsYearClosedPublicAsync(int legalEntityId, int year, CancellationToken ct);
    Task<TaxRegisterJournalPage> GetJournalAsync(int legalEntityId, DateTime? from, DateTime? to, string? action, int page, int pageSize, CancellationToken ct);
}

/// <summary>
/// Регистр для книги доходов и расходов. Сервис хранит и считает только то, что утвердил человек: правила признания
/// доходов и расходов (кассовый метод, состав расходов, форма книги) определяет бухгалтерия, поэтому здесь нет
/// автоматической классификации — запись попадает в книгу после ручного утверждения со ссылкой на первичный документ.
/// </summary>
public sealed class TaxRegisterService : ITaxRegisterService
{
    private readonly PaymentContext _db;

    public TaxRegisterService(PaymentContext db) => _db = db;


    // The company row is the common database lock for every register mutation, including year closure.
    // SQLite obtains its write lock here; PostgreSQL serializes writers on the same company row.
    private async Task<TaxRegisterResult<T>> WriteAsync<T>(int companyId, Func<Task<TaxRegisterResult<T>>> write, CancellationToken ct)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        if (await _db.LegalEntities.Where(item => item.Id == companyId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ShortName, item => item.ShortName), ct) != 1)
            return TaxRegisterResult<T>.Fail("Компания не найдена.", 404);
        try
        {
            var result = await write();
            if (result.Ok) await transaction.CommitAsync(ct);
            else
            {
                await transaction.RollbackAsync(ct);
                ClearRegisterTracking();
            }
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            ClearRegisterTracking();
            throw;
        }
    }

    private void ClearRegisterTracking()
    {
        foreach (var entry in _db.ChangeTracker.Entries().Where(item => item.Entity is TaxRegisterEntry or TaxRegisterEvent or TaxRegisterYearClose).ToList())
            entry.State = EntityState.Detached;
    }

    private async Task<TaxRegisterResult<TaxRegisterEntry>> WriteEntryAsync(int id, Func<Task<TaxRegisterResult<TaxRegisterEntry>>> write, CancellationToken ct)
    {
        var company = await _db.TaxRegisterEntries.AsNoTracking().Where(item => item.Id == id).Select(item => (int?)item.LegalEntityId).FirstOrDefaultAsync(ct);
        return company.HasValue ? await WriteAsync(company.Value, async () =>
        {
            var tracked = _db.TaxRegisterEntries.Local.FirstOrDefault(item => item.Id == id);
            if (tracked is not null) await _db.Entry(tracked).ReloadAsync(ct);
            return await write();
        }, ct) : TaxRegisterResult<TaxRegisterEntry>.Fail("Запись не найдена.", 404);
    }

    public Task<TaxRegisterResult<TaxRegisterEntry>> CreateAsync(TaxRegisterEntryInput input, int userId, CancellationToken ct)
        => WriteAsync(input.LegalEntityId, () => CreateCoreAsync(input, userId, ct), ct);
    public Task<TaxRegisterResult<TaxRegisterEntry>> UpdateDraftAsync(int id, TaxRegisterEntryInput input, int userId, CancellationToken ct)
        => WriteEntryAsync(id, () => UpdateDraftCoreAsync(id, input, userId, ct), ct);
    public Task<TaxRegisterResult<TaxRegisterEntry>> ApproveAsync(int id, int userId, CancellationToken ct)
        => WriteEntryAsync(id, () => ApproveCoreAsync(id, userId, ct), ct);
    public Task<TaxRegisterResult<TaxRegisterEntry>> ArchiveAsync(int id, int userId, string? comment, CancellationToken ct)
        => WriteEntryAsync(id, () => ArchiveCoreAsync(id, userId, comment, ct), ct);
    public Task<TaxRegisterResult<TaxRegisterEntry>> CorrectAsync(int id, string reason, int userId, CancellationToken ct)
        => WriteEntryAsync(id, () => CorrectCoreAsync(id, reason, userId, ct), ct);
    public Task<TaxRegisterResult<TaxRegisterYearClose>> CloseYearAsync(int legalEntityId, int year, int userId, CancellationToken ct)
        => WriteAsync(legalEntityId, () => CloseYearCoreAsync(legalEntityId, year, userId, ct), ct);

    private async Task<TaxRegisterResult<TaxRegisterEntry>> CreateCoreAsync(TaxRegisterEntryInput input, int userId, CancellationToken ct)
    {
        var entry = new TaxRegisterEntry { CreatedByUserId = userId, PostingAt = DateTime.UtcNow };
        var error = await ApplyInputAsync(entry, input, ct);
        if (error is not null)
        {
            return TaxRegisterResult<TaxRegisterEntry>.Fail(error.Value.Message, error.Value.Status);
        }

        _db.TaxRegisterEntries.Add(entry);
        await _db.SaveChangesAsync(ct);
        AddEvent(entry.Id, TaxRegisterActions.Created, null, entry.Status, userId, null);
        await _db.SaveChangesAsync(ct);
        return new(entry);
    }

    private async Task<TaxRegisterResult<TaxRegisterEntry>> UpdateDraftCoreAsync(int id, TaxRegisterEntryInput input, int userId, CancellationToken ct)
    {
        var entry = await _db.TaxRegisterEntries.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entry is null)
        {
            return TaxRegisterResult<TaxRegisterEntry>.Fail("Запись не найдена.", 404);
        }

        if (entry.Status != TaxRegisterStatuses.Draft)
        {
            return TaxRegisterResult<TaxRegisterEntry>.Fail("Утверждённую запись изменить нельзя — создайте корректировку.", 409);
        }

        if (input.LegalEntityId != entry.LegalEntityId)
        {
            return TaxRegisterResult<TaxRegisterEntry>.Fail("Компанию записи менять нельзя.");
        }

        var error = await ApplyInputAsync(entry, input, ct);
        if (error is not null)
        {
            return TaxRegisterResult<TaxRegisterEntry>.Fail(error.Value.Message, error.Value.Status);
        }

        await _db.SaveChangesAsync(ct);
        return new(entry);
    }

    private async Task<TaxRegisterResult<TaxRegisterEntry>> ApproveCoreAsync(int id, int userId, CancellationToken ct)
    {
        var entry = await _db.TaxRegisterEntries.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entry is null)
        {
            return TaxRegisterResult<TaxRegisterEntry>.Fail("Запись не найдена.", 404);
        }

        if (entry.Status != TaxRegisterStatuses.Draft)
        {
            return TaxRegisterResult<TaxRegisterEntry>.Fail("Утвердить можно только черновик.", 409);
        }

        if (string.IsNullOrWhiteSpace(entry.DocumentRef))
        {
            return TaxRegisterResult<TaxRegisterEntry>.Fail("Для утверждения укажите первичный документ.");
        }

        if (await IsYearClosedAsync(entry.LegalEntityId, entry.OperationDate.Year, ct))
        {
            return TaxRegisterResult<TaxRegisterEntry>.Fail($"Год {entry.OperationDate.Year} закрыт — утверждение запрещено.", 409);
        }

        var from = entry.Status;
        entry.Status = TaxRegisterStatuses.Approved;
        entry.ApprovedByUserId = userId;
        entry.ApprovedAt = DateTime.UtcNow;
        AddEvent(entry.Id, TaxRegisterActions.Approved, from, entry.Status, userId, null);
        await _db.SaveChangesAsync(ct);
        return new(entry);
    }

    private async Task<TaxRegisterResult<TaxRegisterEntry>> ArchiveCoreAsync(int id, int userId, string? comment, CancellationToken ct)
    {
        var entry = await _db.TaxRegisterEntries.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entry is null)
        {
            return TaxRegisterResult<TaxRegisterEntry>.Fail("Запись не найдена.", 404);
        }

        if (entry.Status != TaxRegisterStatuses.Draft)
        {
            return TaxRegisterResult<TaxRegisterEntry>.Fail("В архив уходят только черновики; утверждённую запись исправляют корректировкой.", 409);
        }

        var from = entry.Status;
        entry.Status = TaxRegisterStatuses.Archived;
        AddEvent(entry.Id, TaxRegisterActions.Archived, from, entry.Status, userId, Trim(comment, 1000));
        await _db.SaveChangesAsync(ct);
        return new(entry);
    }

    private async Task<TaxRegisterResult<TaxRegisterEntry>> CorrectCoreAsync(int id, string reason, int userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return TaxRegisterResult<TaxRegisterEntry>.Fail("Укажите причину корректировки.");
        }

        var original = await _db.TaxRegisterEntries.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (original is null)
        {
            return TaxRegisterResult<TaxRegisterEntry>.Fail("Запись не найдена.", 404);
        }

        if (original.Status != TaxRegisterStatuses.Approved || original.Type == TaxRegisterTypes.Correction)
        {
            return TaxRegisterResult<TaxRegisterEntry>.Fail("Исправить можно только утверждённую обычную запись.", 409);
        }

        if (original.CorrectedByEntryId.HasValue)
        {
            return TaxRegisterResult<TaxRegisterEntry>.Fail("Запись уже исправлена корректировкой.", 409);
        }

        // Сторно ставится в год исходной записи; если год закрыт, это запрещено — закрытый год не переоткрывается.
        if (await IsYearClosedAsync(original.LegalEntityId, original.OperationDate.Year, ct))
        {
            return TaxRegisterResult<TaxRegisterEntry>.Fail($"Год {original.OperationDate.Year} закрыт — корректировка запрещена.", 409);
        }

        var now = DateTime.UtcNow;
        var correction = new TaxRegisterEntry
        {
            LegalEntityId = original.LegalEntityId,
            BankConnectionAccountId = original.BankConnectionAccountId,
            OperationDate = original.OperationDate,
            PostingAt = now,
            Type = TaxRegisterTypes.Correction,
            Direction = original.Direction,
            SubType = original.SubType,
            Amount = original.Amount,
            Sign = -original.Sign,
            DocumentRef = original.DocumentRef,
            DocumentId = original.DocumentId,
            Description = $"Корректировка записи №{original.Id}",
            Category = original.Category,
            TaxBaseIncludesVat = original.TaxBaseIncludesVat,
            Status = TaxRegisterStatuses.Approved,
            CorrectsEntryId = original.Id,
            CorrectionReason = Trim(reason, 1000),
            CreatedByUserId = userId,
            ApprovedByUserId = userId,
            ApprovedAt = now,
        };
        _db.TaxRegisterEntries.Add(correction);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsCorrectionDuplicate(ex))
        {
            // Уникальный индекс по CorrectsEntryId: параллельный запрос уже создал корректировку этой записи.
            _db.Entry(correction).State = EntityState.Detached;
            return TaxRegisterResult<TaxRegisterEntry>.Fail("Запись уже исправлена корректировкой.", 409);
        }

        original.CorrectedByEntryId = correction.Id;
        AddEvent(original.Id, TaxRegisterActions.Corrected, original.Status, original.Status, userId, Trim(reason, 1000));
        AddEvent(correction.Id, TaxRegisterActions.Created, null, correction.Status, userId, Trim(reason, 1000));
        await _db.SaveChangesAsync(ct);
        return new(correction);
    }

    private async Task<TaxRegisterResult<TaxRegisterYearClose>> CloseYearCoreAsync(int legalEntityId, int year, int userId, CancellationToken ct)
    {
        if (year < 2000 || year > DateTime.UtcNow.Year)
        {
            return TaxRegisterResult<TaxRegisterYearClose>.Fail("Закрыть можно только год не позднее текущего.");
        }

        if (await IsYearClosedAsync(legalEntityId, year, ct))
        {
            return TaxRegisterResult<TaxRegisterYearClose>.Fail("Год уже закрыт.", 409);
        }

        var from = new DateTime(year, 1, 1);
        var to = from.AddYears(1);
        var drafts = await _db.TaxRegisterEntries.CountAsync(item => item.LegalEntityId == legalEntityId
            && item.Status == TaxRegisterStatuses.Draft && item.OperationDate >= from && item.OperationDate < to, ct);
        if (drafts > 0)
        {
            return TaxRegisterResult<TaxRegisterYearClose>.Fail($"В году остались черновики ({drafts}): утвердите или архивируйте их перед закрытием.", 409);
        }

        var close = new TaxRegisterYearClose
        {
            LegalEntityId = legalEntityId,
            Year = year,
            ClosedAt = DateTime.UtcNow,
            ClosedByUserId = userId,
            DataHash = await ComputeYearHashAsync(legalEntityId, year, ct),
        };
        _db.TaxRegisterYearCloses.Add(close);
        await _db.SaveChangesAsync(ct);
        return new(close);
    }

    public async Task<TaxRegisterJournalPage> GetJournalAsync(int legalEntityId, DateTime? from, DateTime? to, string? action, int page, int pageSize, CancellationToken ct)
    {
        var query = from entry in _db.TaxRegisterEntries.AsNoTracking()
                    where entry.LegalEntityId == legalEntityId
                    join history in _db.TaxRegisterEvents.AsNoTracking() on entry.Id equals history.TaxRegisterEntryId
                    select new { history, entry.Amount, entry.Sign, entry.Direction, entry.DocumentRef };
        if (from.HasValue)
        {
            query = query.Where(item => item.history.CreatedAt >= from.Value);
        }

        if (to.HasValue)
        {
            query = query.Where(item => item.history.CreatedAt < to.Value.Date.AddDays(1));
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            query = query.Where(item => item.history.Action == action);
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(item => item.history.CreatedAt).ThenByDescending(item => item.history.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new TaxRegisterJournalPage(total, page, pageSize, rows.Select(item => new TaxRegisterJournalItem(
            item.history.Id, item.history.TaxRegisterEntryId, item.history.Action, item.history.FromStatus, item.history.ToStatus,
            item.history.UserId, item.history.Comment, item.history.CreatedAt, item.Direction, item.Sign * item.Amount, item.DocumentRef)).ToList());
    }

    private const int BatchSize = 1000;

    private IQueryable<TaxRegisterEntry> ApprovedOfYear(int legalEntityId, int year)
    {
        var from = new DateTime(year, 1, 1);
        var to = from.AddYears(1);
        return _db.TaxRegisterEntries.AsNoTracking()
            .Where(item => item.LegalEntityId == legalEntityId && item.Status == TaxRegisterStatuses.Approved
                && item.OperationDate >= from && item.OperationDate < to);
    }

    public Task<bool> IsYearClosedPublicAsync(int legalEntityId, int year, CancellationToken ct) => IsYearClosedAsync(legalEntityId, year, ct);

    /// <summary>Утверждённые записи года по порядку книги партиями по ключу (дата, номер): память ограничена размером партии.</summary>
    public async IAsyncEnumerable<TaxRegisterEntry> StreamBookEntriesAsync(int legalEntityId, int year, [EnumeratorCancellation] CancellationToken ct)
    {
        DateTime? lastDate = null;
        var lastId = 0;
        while (true)
        {
            var query = ApprovedOfYear(legalEntityId, year);
            if (lastDate.HasValue)
            {
                var date = lastDate.Value;
                var id = lastId;
                query = query.Where(item => item.OperationDate > date || (item.OperationDate == date && item.Id > id));
            }

            var batch = await query.OrderBy(item => item.OperationDate).ThenBy(item => item.Id).Take(BatchSize).ToListAsync(ct);
            foreach (var entry in batch)
            {
                yield return entry;
            }

            if (batch.Count < BatchSize)
            {
                yield break;
            }

            lastDate = batch[^1].OperationDate;
            lastId = batch[^1].Id;
        }
    }

    /// <summary>Итоги считаются на сервере по тонкой проекции партиями; записи целиком в память не загружаются.</summary>
    private async Task<TaxRegisterBookTotals> ScanTotalsAsync(int legalEntityId, int year, CancellationToken ct)
    {
        decimal income = 0m, expense = 0m;
        var count = 0;
        var lastId = 0;
        while (true)
        {
            var id = lastId;
            var batch = await ApprovedOfYear(legalEntityId, year).Where(item => item.Id > id).OrderBy(item => item.Id)
                .Select(item => new { item.Id, item.Direction, item.Sign, item.Amount }).Take(BatchSize * 2).ToListAsync(ct);
            foreach (var row in batch)
            {
                if (row.Direction == TaxRegisterTypes.Income) income += row.Sign * row.Amount;
                else if (row.Direction == TaxRegisterTypes.Expense) expense += row.Sign * row.Amount;
            }

            count += batch.Count;
            if (batch.Count < BatchSize * 2)
            {
                return new TaxRegisterBookTotals(income, expense, income - expense, count);
            }

            lastId = batch[^1].Id;
        }
    }

    public async Task<TaxRegisterBookPage> GetBookPageAsync(int legalEntityId, int year, int page, int pageSize, CancellationToken ct)
    {
        var totals = await ScanTotalsAsync(legalEntityId, year, ct);
        var entries = await ApprovedOfYear(legalEntityId, year)
            .OrderBy(item => item.OperationDate).ThenBy(item => item.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new TaxRegisterBookPage(legalEntityId, year, await IsYearClosedAsync(legalEntityId, year, ct), totals, page, pageSize, entries);
    }

    /// <summary>Контрольная сумма года тем же форматом, что и <see cref="ComputeHash"/>, но без загрузки всех записей.</summary>
    private async Task<string> ComputeYearHashAsync(int legalEntityId, int year, CancellationToken ct)
    {
        var inv = CultureInfo.InvariantCulture;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var first = true;
        var lastId = 0;
        while (true)
        {
            var id = lastId;
            var batch = await ApprovedOfYear(legalEntityId, year).Where(item => item.Id > id).OrderBy(item => item.Id)
                .Take(BatchSize).ToListAsync(ct);
            foreach (var item in batch)
            {
                var line = string.Join('|', item.Id.ToString(inv), item.OperationDate.ToString("yyyy-MM-dd", inv), item.Direction,
                    item.Sign.ToString(inv), item.Amount.ToString("0.00", inv), item.DocumentRef ?? string.Empty);
                hash.AppendData(Encoding.UTF8.GetBytes(first ? line : "\n" + line));
                first = false;
            }

            if (batch.Count < BatchSize)
            {
                return Convert.ToHexString(hash.GetHashAndReset());
            }

            lastId = batch[^1].Id;
        }
    }

    public async Task<TaxRegisterBook> BuildBookAsync(int legalEntityId, int year, CancellationToken ct)
    {
        var from = new DateTime(year, 1, 1);
        var to = from.AddYears(1);
        var entries = await _db.TaxRegisterEntries.AsNoTracking()
            .Where(item => item.LegalEntityId == legalEntityId && item.Status == TaxRegisterStatuses.Approved
                && item.OperationDate >= from && item.OperationDate < to)
            .OrderBy(item => item.OperationDate).ThenBy(item => item.Id)
            .ToListAsync(ct);

        var income = entries.Where(item => item.Direction == TaxRegisterTypes.Income).Sum(item => item.Sign * item.Amount);
        var expense = entries.Where(item => item.Direction == TaxRegisterTypes.Expense).Sum(item => item.Sign * item.Amount);
        var closed = await IsYearClosedAsync(legalEntityId, year, ct);
        // Разность — арифметика по утверждённым записям; налоговую базу по режиму налогообложения определяет бухгалтерия.
        return new TaxRegisterBook(legalEntityId, year, closed, entries, new TaxRegisterBookTotals(income, expense, income - expense, entries.Count));
    }

    internal static string ComputeHash(IEnumerable<TaxRegisterEntry> entries)
    {
        var inv = CultureInfo.InvariantCulture;
        var text = string.Join('\n', entries.OrderBy(item => item.Id).Select(item =>
            string.Join('|', item.Id.ToString(inv), item.OperationDate.ToString("yyyy-MM-dd", inv), item.Direction,
                item.Sign.ToString(inv), item.Amount.ToString("0.00", inv), item.DocumentRef ?? string.Empty)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static bool IsCorrectionDuplicate(DbUpdateException ex) => ex.InnerException switch
    {
        Npgsql.PostgresException { SqlState: "23505", ConstraintName: "IX_TaxRegisterEntries_CorrectsEntryId" } => true,
        Microsoft.Data.Sqlite.SqliteException { SqliteExtendedErrorCode: 2067 } sqlite
            => sqlite.Message.Contains("TaxRegisterEntries.CorrectsEntryId", StringComparison.Ordinal),
        _ => false,
    };

    private Task<bool> IsYearClosedAsync(int legalEntityId, int year, CancellationToken ct)
        => _db.TaxRegisterYearCloses.AnyAsync(item => item.LegalEntityId == legalEntityId && item.Year == year, ct);

    private void AddEvent(int entryId, string action, string? from, string? to, int userId, string? comment)
        => _db.TaxRegisterEvents.Add(new TaxRegisterEvent
        {
            TaxRegisterEntryId = entryId,
            Action = action,
            FromStatus = from,
            ToStatus = to,
            UserId = userId,
            Comment = comment,
            CreatedAt = DateTime.UtcNow,
        });

    private static string? Trim(string? value, int max)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length > max ? text[..max] : text;
    }

    private async Task<(string Message, int Status)?> ApplyInputAsync(TaxRegisterEntry entry, TaxRegisterEntryInput input, CancellationToken ct)
    {
        if (!await _db.LegalEntities.AnyAsync(item => item.Id == input.LegalEntityId, ct))
        {
            return ("Компания не найдена.", 404);
        }

        var type = input.Type;
        var date = input.OperationDate?.Date;
        var amount = input.Amount;

        if (entry.BankStatementOperationId.HasValue && input.BankStatementOperationId.HasValue && entry.BankStatementOperationId != input.BankStatementOperationId)
            return ("Банковскую операцию существующей записи менять нельзя.", 409);

        if ((entry.BankStatementOperationId ?? input.BankStatementOperationId) is int operationId)
        {
            var operation = await _db.BankStatementOperations.AsNoTracking()
                .Where(item => item.Id == operationId)
                .Select(item => new { item.Id, item.OperationDate, item.Amount, item.IsIncome, item.IsBooked, item.BankConnectionAccountId, LegalEntityId = item.BankConnectionAccount.BankConnection.LegalEntityId })
                .FirstOrDefaultAsync(ct);
            if (operation is null || operation.LegalEntityId != input.LegalEntityId)
            {
                return ("Операция выписки не найдена у этой компании.", 404);
            }

            if (!operation.IsBooked)
            {
                return ("Операция ещё не проведена банком — в регистр её добавить нельзя.", 409);
            }

            if (await _db.TaxRegisterEntries.AnyAsync(item => item.BankStatementOperationId == operationId && item.Id != entry.Id, ct))
            {
                return ("Эта операция выписки уже добавлена в регистр.", 409);
            }

            // Данные операции — источник: дата, сумма и направление берутся из выписки, а не из ввода пользователя.
            entry.BankStatementOperationId = operation.Id;
            entry.BankConnectionAccountId = operation.BankConnectionAccountId;
            type = operation.IsIncome ? TaxRegisterTypes.Income : TaxRegisterTypes.Expense;
            date = operation.OperationDate.Date;
            amount = operation.Amount;
        }

        if (type is not (TaxRegisterTypes.Income or TaxRegisterTypes.Expense))
        {
            return ("Тип записи: Income (доход) или Expense (расход).", 400);
        }

        if (date is null)
        {
            return ("Укажите дату операции.", 400);
        }

        if (date.Value.Year is < 2000 or > 2100)
            return ("Укажите корректный год операции (2000–2100).", 400);

        if (amount.HasValue) amount = decimal.Round(amount.Value, 2, MidpointRounding.AwayFromZero);
        if (amount is not > 0m || amount > 1_000_000_000_000m)
        {
            return ("Сумма должна быть положительной.", 400);
        }

        if (await IsYearClosedAsync(input.LegalEntityId, date.Value.Year, ct))
        {
            return ($"Год {date.Value.Year} закрыт — записи в него не добавляются.", 409);
        }

        entry.LegalEntityId = input.LegalEntityId;
        entry.Type = type;
        entry.Direction = type;
        entry.OperationDate = date.Value;
        entry.Amount = decimal.Round(amount.Value, 2, MidpointRounding.AwayFromZero);
        entry.Sign = 1;
        entry.DocumentRef = Trim(input.DocumentRef, 300);
        entry.DocumentId = input.DocumentId;
        entry.Description = Trim(input.Description, 2000);
        entry.Category = Trim(input.Category, 200);
        entry.SubType = Trim(input.SubType, 32);
        entry.TaxBaseIncludesVat = input.TaxBaseIncludesVat;
        return null;
    }
}
