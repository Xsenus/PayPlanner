using System.Globalization;
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

public sealed record TaxRegisterBookTotals(decimal Income, decimal Expense, decimal TaxBase, int Entries);

public sealed record TaxRegisterBook(
    int LegalEntityId,
    int Year,
    bool IsClosed,
    IReadOnlyList<TaxRegisterEntry> Entries,
    TaxRegisterBookTotals Totals);

public interface ITaxRegisterService
{
    Task<TaxRegisterResult<TaxRegisterEntry>> CreateAsync(TaxRegisterEntryInput input, int userId, CancellationToken ct);
    Task<TaxRegisterResult<TaxRegisterEntry>> UpdateDraftAsync(int id, TaxRegisterEntryInput input, int userId, CancellationToken ct);
    Task<TaxRegisterResult<TaxRegisterEntry>> ApproveAsync(int id, int userId, CancellationToken ct);
    Task<TaxRegisterResult<TaxRegisterEntry>> ArchiveAsync(int id, int userId, string? comment, CancellationToken ct);
    Task<TaxRegisterResult<TaxRegisterEntry>> CorrectAsync(int id, string reason, int userId, CancellationToken ct);
    Task<TaxRegisterResult<TaxRegisterYearClose>> CloseYearAsync(int legalEntityId, int year, int userId, CancellationToken ct);
    Task<TaxRegisterBook> BuildBookAsync(int legalEntityId, int year, CancellationToken ct);
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

    public async Task<TaxRegisterResult<TaxRegisterEntry>> CreateAsync(TaxRegisterEntryInput input, int userId, CancellationToken ct)
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

    public async Task<TaxRegisterResult<TaxRegisterEntry>> UpdateDraftAsync(int id, TaxRegisterEntryInput input, int userId, CancellationToken ct)
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

    public async Task<TaxRegisterResult<TaxRegisterEntry>> ApproveAsync(int id, int userId, CancellationToken ct)
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

    public async Task<TaxRegisterResult<TaxRegisterEntry>> ArchiveAsync(int id, int userId, string? comment, CancellationToken ct)
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

    public async Task<TaxRegisterResult<TaxRegisterEntry>> CorrectAsync(int id, string reason, int userId, CancellationToken ct)
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
        await _db.SaveChangesAsync(ct);

        original.CorrectedByEntryId = correction.Id;
        AddEvent(original.Id, TaxRegisterActions.Corrected, original.Status, original.Status, userId, Trim(reason, 1000));
        AddEvent(correction.Id, TaxRegisterActions.Created, null, correction.Status, userId, Trim(reason, 1000));
        await _db.SaveChangesAsync(ct);
        return new(correction);
    }

    public async Task<TaxRegisterResult<TaxRegisterYearClose>> CloseYearAsync(int legalEntityId, int year, int userId, CancellationToken ct)
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

        var book = await BuildBookAsync(legalEntityId, year, ct);
        var close = new TaxRegisterYearClose
        {
            LegalEntityId = legalEntityId,
            Year = year,
            ClosedAt = DateTime.UtcNow,
            ClosedByUserId = userId,
            DataHash = ComputeHash(book.Entries),
        };
        _db.TaxRegisterYearCloses.Add(close);
        await _db.SaveChangesAsync(ct);
        return new(close);
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

    public static string ToCsv(TaxRegisterBook book)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append('﻿');
        sb.Append("№;Дата операции;Направление;Тип;Сумма со знаком;Первичный документ;Описание;Категория\r\n");
        var number = 0;
        foreach (var entry in book.Entries)
        {
            number++;
            sb.Append(number.ToString(inv)).Append(';')
              .Append(entry.OperationDate.ToString("dd.MM.yyyy", inv)).Append(';')
              .Append(entry.Direction == TaxRegisterTypes.Income ? "Доход" : "Расход").Append(';')
              .Append(entry.Type == TaxRegisterTypes.Correction ? "Корректировка" : "Обычная").Append(';')
              .Append((entry.Sign * entry.Amount).ToString("0.00", inv)).Append(';')
              .Append(CsvCell(entry.DocumentRef)).Append(';')
              .Append(CsvCell(entry.Description)).Append(';')
              .Append(CsvCell(entry.Category)).Append("\r\n");
        }

        sb.Append(";;;Итого доходов;").Append(book.Totals.Income.ToString("0.00", inv)).Append(";;;\r\n");
        sb.Append(";;;Итого расходов;").Append(book.Totals.Expense.ToString("0.00", inv)).Append(";;;\r\n");
        sb.Append(";;;Разность;").Append(book.Totals.TaxBase.ToString("0.00", inv)).Append(";;;\r\n");
        return sb.ToString();
    }

    internal static string ComputeHash(IEnumerable<TaxRegisterEntry> entries)
    {
        var inv = CultureInfo.InvariantCulture;
        var text = string.Join('\n', entries.OrderBy(item => item.Id).Select(item =>
            string.Join('|', item.Id.ToString(inv), item.OperationDate.ToString("yyyy-MM-dd", inv), item.Direction,
                item.Sign.ToString(inv), item.Amount.ToString("0.00", inv), item.DocumentRef ?? string.Empty)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static string CsvCell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var cell = value.Replace("\r", " ").Replace("\n", " ");
        // Защита от формул Excel: ячейка не должна начинаться с управляющего символа формулы.
        if (cell[0] is '=' or '+' or '-' or '@' or '\t')
        {
            cell = "'" + cell;
        }

        return cell.Contains(';') || cell.Contains('"') ? "\"" + cell.Replace("\"", "\"\"") + "\"" : cell;
    }

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

        if (input.BankStatementOperationId is int operationId)
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
