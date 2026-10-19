using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services.Banking;

/// <summary>Existing statement parsing and matching rules shared by all import entry points.</summary>
public static class BankStatementImportRules
{
    public const string CashSourceToken = "\u043A\u0430\u0441";

    public const string CashSourceTitleToken = "\u041A\u0430\u0441";

    public const string CashSourceAlternativeToken = "\u043D\u0430\u043B\u0438\u0447";

    public const string CashSourceAlternativeTitleToken = "\u041D\u0430\u043B\u0438\u0447";

    public const string CardSourceToken = "\u043A\u0430\u0440\u0442";

    public const string CardSourceTitleToken = "\u041A\u0430\u0440\u0442";

    public const string EMoneySourceToken = "e-money";

    public const string EMoneySourceTitleToken = "E-money";

    public const string EMoneySourceCompactToken = "emoney";

    public const string EMoneySourceCompactTitleToken = "EMoney";

    public const string EMoneySourceRussianToken = "\u044D\u043B\u0435\u043A\u0442\u0440\u043E\u043D";

    public const string EMoneySourceRussianTitleToken = "\u042D\u043B\u0435\u043A\u0442\u0440\u043E\u043D";

    public const string EMoneySourceYuMoneyToken = "\u042EMoney";

    public const string EMoneySourceYooMoneyToken = "YooMoney";

    public const string EMoneySourceYandexToken = "\u042F\u043D\u0434\u0435\u043A\u0441";

    public static readonly BankStatementCategoryRule[] BankStatementCategoryRules =
    [
        new("Оплата от клиентов", PaymentType.Income,
        [
            "оплата по счету",
            "оплата услуг",
            "за услуги",
            "по договору оказания",
            "поступление от клиента",
            "оплата от покупателя",
        ]),
        new("Возврат от поставщика", PaymentType.Income,
        [
            "возврат от поставщика",
            "возврат оплаты",
            "возврат денежных средств",
            "возврат переплаты",
        ]),
        new("Эквайринг", PaymentType.Income,
        [
            "эквайринг",
            "поступление по эквайрингу",
            "зачисление по терминалу",
        ]),
        new("Банковская комиссия", PaymentType.Expense,
        [
            "банковская комиссия",
            "комиссия банка",
            "комиссия за",
            "расчетно кассов",
            "рко",
            "обслуживание счета",
            "эквайринг",
        ]),
        new("Налоги и сборы", PaymentType.Expense,
        [
            "налог",
            "ндс",
            "ндфл",
            "усн",
            "страховые взносы",
            "взносы",
            "пени",
            "штраф",
            "казначейство",
            "фнс",
        ]),
        new("Зарплата", PaymentType.Expense,
        [
            "заработная плата",
            "зарплата",
            "аванс",
            "реестр на выплату",
            "по ведомости",
        ]),
        new("Аренда", PaymentType.Expense,
        [
            "арендная плата",
            "аренда",
            "аренда офиса",
            "за помещение",
        ]),
        new("Расчеты с поставщиками", PaymentType.Expense,
        [
            "оплата поставщику",
            "за материалы",
            "за товары",
            "по договору поставки",
            "за оборудование",
            "за услуги связи",
        ]),
        new("Возврат клиенту", PaymentType.Expense,
        [
            "возврат клиенту",
            "возврат покупателю",
            "возврат оплаты",
            "возврат денежных средств",
        ]),
    ];

    public static bool IsBankPaymentSource(string? sourceName)
        => string.IsNullOrWhiteSpace(sourceName) ||
           (!sourceName.Contains(CashSourceToken) &&
            !sourceName.Contains(CashSourceTitleToken) &&
           !sourceName.Contains(CashSourceAlternativeToken) &&
           !sourceName.Contains(CashSourceAlternativeTitleToken) &&
            !sourceName.Contains(CardSourceToken) &&
            !sourceName.Contains(CardSourceTitleToken) &&
            !sourceName.Contains(EMoneySourceToken) &&
            !sourceName.Contains(EMoneySourceTitleToken) &&
            !sourceName.Contains(EMoneySourceCompactToken) &&
            !sourceName.Contains(EMoneySourceCompactTitleToken) &&
            !sourceName.Contains(EMoneySourceRussianToken) &&
            !sourceName.Contains(EMoneySourceRussianTitleToken) &&
            !sourceName.Contains(EMoneySourceYuMoneyToken) &&
            !sourceName.Contains(EMoneySourceYooMoneyToken) &&
            !sourceName.Contains(EMoneySourceYandexToken));

    public static BankStatementCategoryRule? TryCreateManualBankStatementCategoryRule(
        BankStatementManualCategoryRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.CategoryName) || string.IsNullOrWhiteSpace(rule.Keywords))
        {
            return null;
        }

        var paymentType = rule.PaymentType == PaymentType.Expense ? PaymentType.Expense : PaymentType.Income;
        var keywords = rule.Keywords
            .Split(new[] { ';', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(keyword => NormalizeNameForMatch(keyword).Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();
        if (keywords.Length == 0)
        {
            return null;
        }

        var incomeTypeId = rule.IncomeTypeId.GetValueOrDefault() > 0 ? rule.IncomeTypeId : null;
        return new BankStatementCategoryRule(
            rule.CategoryName.Trim(),
            paymentType,
            keywords,
            incomeTypeId,
            IsManual: true);
    }

    public static BankStatementImportRow CreateImportRow(
        BankStatementImportBatch batch,
        BankStatementImportItem item,
        string importKey,
        string status,
        string? message,
        MoneyOperation? operation = null)
        => new()
        {
            Batch = batch,
            RowNumber = item.RowNumber,
            ImportKey = importKey,
            Status = status,
            Message = message,
            MoneyOperation = operation,
            Date = item.Date,
            Direction = item.Direction,
            Amount = item.Amount,
            AccountName = item.AccountName,
            CounterpartyName = item.CounterpartyName,
            Purpose = item.Purpose,
            PaymentOrderNumber = item.DocumentNumber,
            CreatedAt = DateTime.UtcNow,
        };

    public static bool LooksLikeClientBankExchange(string text)
        => text.Contains("1CClientBankExchange", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("СекцияДокумент=", StringComparison.OrdinalIgnoreCase);

    public static (List<BankStatementImportItem> Items, BankStatementImportResultDto Result) ParseClientBankExchange(
        string text, string? selectedBankAccountNumber = null)
    {
        var items = new List<BankStatementImportItem>();
        var result = new BankStatementImportResultDto();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var declaredAccounts = new HashSet<string>(StringComparer.Ordinal);
        var inDocument = false;
        var inBalance = false;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.StartsWith("СекцияДокумент=", StringComparison.OrdinalIgnoreCase)) { inDocument = true; continue; }
            if (line.Equals("КонецДокумента", StringComparison.OrdinalIgnoreCase)) { inDocument = false; continue; }
            if (line.Equals("СекцияРасчСчет", StringComparison.OrdinalIgnoreCase)) { inBalance = true; continue; }
            if (line.Equals("КонецРасчСчет", StringComparison.OrdinalIgnoreCase)
                || line.Equals("КонецРасчСчета", StringComparison.OrdinalIgnoreCase)) { inBalance = false; continue; }
            if (inDocument || inBalance) continue;
            var separator = line.IndexOf('=');
            if (separator <= 0 || !line[..separator].Trim().Equals("РасчСчет", StringComparison.OrdinalIgnoreCase)) continue;
            var declared = NormalizeBankAccount(line[(separator + 1)..]);
            if (declared.Length > 0) declaredAccounts.Add(declared);
        }
        // Standalone parsing remains available for a single declared account.
        // The upload caller always passes its selected number explicitly, including an empty number.
        var selected = selectedBankAccountNumber is null && declaredAccounts.Count == 1
            ? declaredAccounts.Single() : NormalizeBankAccount(selectedBankAccountNumber);
        var statementError = selected.Length == 0
            ? "Укажите номер банковского счёта в настройках выбранного денежного счёта."
            : declaredAccounts.Count == 0
                ? "В заголовке выписки не указан расчётный счёт."
                : !declaredAccounts.Contains(selected)
                    ? $"Выбранный счёт «{StatementValueForError(selected)}» отсутствует в заголовке выписки: {string.Join(", ", declaredAccounts.Take(3).Select(account => StatementValueForError(account)))}."
                    : null;
        Dictionary<string, string>? document = null;
        string? documentError = null;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("СекцияДокумент=", StringComparison.OrdinalIgnoreCase))
            {
                if (document is not null)
                    AddClientBankExchangeDocument(document, selected, items, result,
                        JoinErrors(statementError, documentError, "Не завершена секция документа."));
                document = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                documentError = null;
                continue;
            }
            if (line.Equals("КонецДокумента", StringComparison.OrdinalIgnoreCase))
            {
                if (document is not null)
                    AddClientBankExchangeDocument(document, selected, items, result, JoinErrors(statementError, documentError));
                document = null;
                documentError = null;
                continue;
            }
            if (document is null) continue;
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (document.TryGetValue(key, out var previous) && !string.Equals(previous, value, StringComparison.Ordinal)
                && (key.Equals("Сумма", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("Дата", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("ПлательщикСчет", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("ПолучательСчет", StringComparison.OrdinalIgnoreCase)))
                documentError ??= $"Повторяются разные значения реквизита «{StatementValueForError(key)}»: «{StatementValueForError(previous)}» и «{StatementValueForError(value)}».";
            document[key] = value;
        }
        if (document is not null)
            AddClientBankExchangeDocument(document, selected, items, result,
                JoinErrors(statementError, documentError, "Не найдена отметка конца документа."));
        if (items.Count == 0 && statementError is not null) result.Errors.Add(statementError);
        return (items, result);

        static string? JoinErrors(params string?[] errors)
        {
            var joined = string.Join(" ", errors.Where(error => !string.IsNullOrWhiteSpace(error)));
            return joined.Length == 0 ? null : joined;
        }
    }

    public static void AddClientBankExchangeDocument(
        IReadOnlyDictionary<string, string> document,
        string statementAccount,
        ICollection<BankStatementImportItem> items,
        BankStatementImportResultDto result,
        string? sourceError = null)
    {
        result.RowsRead++;
        var errors = new List<string>();
        if (sourceError is not null) errors.Add(sourceError);
        var documentNumber = NormalizeStatementText(GetDictionaryValue(document, "Номер"));
        var dateText = GetDictionaryValue(document, "Дата") ?? string.Empty;
        if (!TryParseStatementDate(dateText, out var date))
            errors.Add($"Не удалось прочитать дату «{StatementValueForError(dateText)}».");
        var amountText = GetDictionaryValue(document, "Сумма") ?? string.Empty;
        if (!TryParseStatementAmount(amountText, out var amount) || amount <= 0m)
        {
            errors.Add($"Не удалось однозначно прочитать положительную сумму «{StatementValueForError(amountText)}» без округления.");
            amount = 0m;
        }
        var payerAccount = NormalizeBankAccount(GetDictionaryValue(document, "ПлательщикСчет"));
        var recipientAccount = NormalizeBankAccount(GetDictionaryValue(document, "ПолучательСчет"));
        var selected = NormalizeBankAccount(statementAccount);
        var isExpense = selected.Length > 0 && selected == payerAccount;
        var isIncome = selected.Length > 0 && selected == recipientAccount;
        if (isExpense == isIncome)
            errors.Add($"Нельзя определить единственную сторону выбранного счёта: плательщик «{StatementValueForError(payerAccount)}», получатель «{StatementValueForError(recipientAccount)}».");
        var direction = isExpense && !isIncome ? MoneyOperationDirection.Expense : MoneyOperationDirection.Income;
        var sourceAccount = isExpense != isIncome
            ? isExpense ? payerAccount : recipientAccount
            : $"{StatementValueForError(payerAccount, 90)} → {StatementValueForError(recipientAccount, 90)}";
        items.Add(new BankStatementImportItem(
            result.RowsRead, date.Date, direction, amount, sourceAccount,
            NormalizeStatementText(direction == MoneyOperationDirection.Expense
                ? GetDictionaryValue(document, "Получатель") : GetDictionaryValue(document, "Плательщик")),
            NormalizeStatementText(GetDictionaryValue(document, "НазначениеПлатежа")), documentNumber)
        {
            ValidationError = BuildStatementValidationMessage(errors),
        });
    }

    public static string? GetDictionaryValue(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out var value) ? value : null;

    public static string NormalizeBankAccount(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty
            : string.Concat(value.Where(character => !char.IsWhiteSpace(character))).ToUpperInvariant();

    public static string StatementValueForError(string? value, int maximumLength = 160)
        => TruncateStatementText((value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' '), maximumLength);

    public static string TruncateStatementText(string text, int maximumLength)
    {
        if (text.Length <= maximumLength) return text;
        var end = Math.Max(0, maximumLength - 1);
        if (end > 0 && char.IsHighSurrogate(text[end - 1])) end--;
        return text[..end] + "…";
    }

    public static string? BuildStatementValidationMessage(IEnumerable<string> errors)
    {
        var message = string.Join(" ", errors.Where(error => !string.IsNullOrWhiteSpace(error)));
        if (message.Length == 0) return null;
        const string action = " Исправьте исходную строку и загрузите выписку заново.";
        var limit = 1000 - action.Length;
        return StatementValueForError(message, limit) + action;
    }

    public static string DecodeStatementBytes(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        var utf8 = new UTF8Encoding(false, true);
        try
        {
            return utf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1251).GetString(bytes);
        }
    }

    public static List<List<string>> ParseDelimitedRows(string text)
            => ParseDelimitedRows(text, out _);

    public static List<List<string>> ParseDelimitedRows(string text, out Dictionary<int, string> rowErrors)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var firstLine = normalized.Split('\n', 2)[0];
        var delimiter = firstLine.Count(ch => ch == ';') >= firstLine.Count(ch => ch == '\t') ? ';' : '\t';
        if (firstLine.Count(ch => ch == ',') > firstLine.Count(ch => ch == delimiter))
        {
            delimiter = ',';
        }

        var rows = new List<List<string>>();
        var errors = new Dictionary<int, string>();
        rowErrors = errors;
        var row = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;
        var afterQuote = false;
        var onlyLeadingWhitespace = true;
        string? rowError = null;

        for (var index = 0; index < normalized.Length; index++)
        {
            var ch = normalized[index];
            if (ch == '"')
            {
                if (inQuotes)
                {
                    if (index + 1 < normalized.Length && normalized[index + 1] == '"')
                    {
                        cell.Append('"');
                        index++;
                    }
                    else
                    {
                        inQuotes = false;
                        afterQuote = true;
                    }
                }
                else if (onlyLeadingWhitespace && !afterQuote)
                {
                    inQuotes = true;
                    onlyLeadingWhitespace = false;
                }
                else
                {
                    rowError ??= "Кавычка находится внутри незаключённого в кавычки поля CSV.";
                    cell.Append(ch);
                }
                continue;
            }

            if (!inQuotes && ch == delimiter)
            {
                FinishCell();
                continue;
            }

            if (!inQuotes && ch == '\n')
            {
                FinishCell();
                FinishRow();
                continue;
            }

            if (!inQuotes && afterQuote && !char.IsWhiteSpace(ch))
                rowError ??= "После закрывающей кавычки CSV обнаружены дополнительные символы.";
            cell.Append(ch);
            if (!char.IsWhiteSpace(ch)) onlyLeadingWhitespace = false;
        }

        if (inQuotes)
            rowError ??= $"Не закрыта кавычка поля CSV «{StatementValueForError(cell.ToString())}».";
        FinishCell();
        if (row.Any(value => !string.IsNullOrWhiteSpace(value)) || rowError is not null)
            FinishRow();

        return rows;

        void FinishCell()
        {
            row.Add(cell.ToString().Trim());
            cell.Clear();
            afterQuote = false;
            onlyLeadingWhitespace = true;
        }

        void FinishRow()
        {
            if (rowError is not null) errors.Add(rows.Count, rowError);
            rows.Add(row);
            row = new List<string>();
            rowError = null;
        }
    }

    public static StatementHeaderMap BuildStatementHeaderMap(IReadOnlyList<string> header)
    {
        var map = new StatementHeaderMap { ColumnCount = header.Count };
        for (var index = 0; index < header.Count; index++)
        {
            var name = NormalizeHeaderName(header[index]);
            if (map.Date < 0 && (name.Contains("дата") || name.Contains("date")))
            {
                map.Date = index;
            }
            // Counterparty and correspondent bank details do not identify our account.
            else if ((name.Contains("счет") || name.Contains("счёт") || name.Contains("account"))
                && !name.Contains("контрагент") && !name.Contains("counterparty")
                && !name.Contains("корр") && !name.Contains("correspondent"))
            {
                if (name.Contains("плательщик") || name.Contains("payer"))
                {
                    map.HasAmbiguousAccountColumns |= map.PayerAccount >= 0;
                    map.PayerAccount = index;
                }
                else if (name.Contains("получател") || name.Contains("recipient") || name.Contains("payee"))
                {
                    map.HasAmbiguousAccountColumns |= map.RecipientAccount >= 0;
                    map.RecipientAccount = index;
                }
                else
                {
                    map.HasAmbiguousAccountColumns |= map.Account >= 0;
                    map.Account = index;
                }
            }
            else if (map.DocumentNumber < 0 && (name.Contains("номер") || name.Contains("документ") || name.Contains("поручение")))
            {
                map.DocumentNumber = index;
            }
            else if (map.Counterparty < 0 && (name.Contains("контрагент") || name.Contains("плательщик") || name.Contains("получатель") || name.Contains("payer")))
            {
                map.Counterparty = index;
            }
            else if (map.Purpose < 0 && (name.Contains("назначение") || name.Contains("комментар") || name.Contains("purpose")))
            {
                map.Purpose = index;
            }
            else if (name.Contains("приход") || name.Contains("поступ") || name.Contains("кредит") || name.Contains("credit"))
            {
                map.HasAmbiguousAmountColumns |= map.Income >= 0;
                map.AmountColumns.Add(index);
                map.Income = index;
            }
            else if (name.Contains("расход") || name.Contains("спис") || name.Contains("дебет") || name.Contains("debit"))
            {
                map.HasAmbiguousAmountColumns |= map.Expense >= 0;
                map.AmountColumns.Add(index);
                map.Expense = index;
            }
            else if (name.Contains("сумма") || name.Contains("amount"))
            {
                map.HasAmbiguousAmountColumns |= map.Amount >= 0;
                map.AmountColumns.Add(index);
                map.Amount = index;
            }
        }

        return map;
    }

    public static string NormalizeHeaderName(string value)
        => value.Trim().ToLowerInvariant().Replace(" ", string.Empty).Replace("_", string.Empty);

    public static BankStatementImportItem ParseDelimitedStatementRow(
        IReadOnlyList<string> row, StatementHeaderMap map, int rowNumber, string? selectedBankAccountNumber,
        string? sourceError = null)
    {
        var errors = new List<string>();
        if (sourceError is not null) errors.Add(sourceError);
        if (map.ColumnCount > 0 && row.Count != map.ColumnCount)
            errors.Add($"Количество полей ({row.Count}) не совпадает с заголовком ({map.ColumnCount}). Проверьте разделители и кавычки в исходной строке.");
        var dateText = GetOptionalCell(row, map.Date) ?? string.Empty;
        if (!TryParseStatementDate(dateText, out var date))
            errors.Add($"Не удалось прочитать дату «{StatementValueForError(dateText)}».");

        var (amount, direction, explicitDirection) = ParseDelimitedAmount(row, map, errors);
        var own = NormalizeBankAccount(selectedBankAccountNumber);
        var accountText = GetOptionalCell(row, map.Account);
        var payerText = GetOptionalCell(row, map.PayerAccount);
        var recipientText = GetOptionalCell(row, map.RecipientAccount);
        var payer = NormalizeBankAccount(payerText);
        var recipient = NormalizeBankAccount(recipientText);
        if (map.HasAmbiguousAccountColumns)
            errors.Add("Несколько колонок задают один и тот же банковский реквизит.");
        if (map.Account >= 0 || map.PayerAccount >= 0 || map.RecipientAccount >= 0)
        {
            if (own.Length == 0)
                errors.Add("Укажите номер банковского счёта в настройках выбранного денежного счёта.");
            if (map.Account >= 0 && (own.Length == 0 || NormalizeBankAccount(accountText) != own))
                errors.Add($"Счёт строки «{StatementValueForError(accountText)}» не совпадает с выбранным счётом «{StatementValueForError(own)}».");

            var ownPayer = own.Length > 0 && payer == own;
            var ownRecipient = own.Length > 0 && recipient == own;
            if (map.PayerAccount >= 0 && map.RecipientAccount >= 0)
            {
                if (ownPayer == ownRecipient)
                    errors.Add($"Нельзя определить единственную сторону выбранного счёта: плательщик «{StatementValueForError(payerText)}», получатель «{StatementValueForError(recipientText)}».");
                else
                    ApplyDirection(ownPayer ? MoneyOperationDirection.Expense : MoneyOperationDirection.Income);
            }
            else if (map.PayerAccount >= 0 || map.RecipientAccount >= 0)
            {
                // A sole party column may describe the counterparty. Check it
                // against the amount direction rather than treating it as our account.
                if (ownPayer || ownRecipient)
                    ApplyDirection(ownPayer ? MoneyOperationDirection.Expense : MoneyOperationDirection.Income);
                else if ((map.PayerAccount >= 0 && (payer.Length == 0 || direction == MoneyOperationDirection.Expense))
                    || (map.RecipientAccount >= 0 && (recipient.Length == 0 || direction == MoneyOperationDirection.Income)))
                    errors.Add($"Реквизиты плательщика «{StatementValueForError(payerText)}» и получателя «{StatementValueForError(recipientText)}» не подтверждают выбранную сторону операции.");
            }
        }

        var sourceAccount = accountText ?? (map.PayerAccount >= 0 || map.RecipientAccount >= 0
            ? $"{StatementValueForError(payerText, 90)} → {StatementValueForError(recipientText, 90)}"
            : "Расчетный счет");
        return new BankStatementImportItem(rowNumber, date.Date, direction, amount,
            StatementValueForError(sourceAccount, 200),
            NormalizeStatementText(GetOptionalCell(row, map.Counterparty)),
            NormalizeStatementText(GetOptionalCell(row, map.Purpose)),
            NormalizeStatementText(GetOptionalCell(row, map.DocumentNumber)))
        {
            ValidationError = BuildStatementValidationMessage(errors),
        };

        void ApplyDirection(MoneyOperationDirection accountDirection)
        {
            if (explicitDirection && direction != accountDirection)
                errors.Add("Направление суммы противоречит стороне выбранного банковского счёта.");
            direction = accountDirection;
        }
    }

    private static (decimal Amount, MoneyOperationDirection Direction, bool ExplicitDirection) ParseDelimitedAmount(
        IReadOnlyList<string> row, StatementHeaderMap map, ICollection<string> errors)
    {
        if (map.HasAmbiguousAmountColumns)
        {
            var values = map.AmountColumns.Take(4).Select(index => $"«{StatementValueForError(GetOptionalCell(row, index), 90)}»");
            errors.Add($"Повторяются колонки сумм: {string.Join(", ", values)}.");
            return (0m, MoneyOperationDirection.Income, false);
        }
        var totalText = GetOptionalCell(row, map.Amount);
        var incomeText = GetOptionalCell(row, map.Income);
        var expenseText = GetOptionalCell(row, map.Expense);
        var validTotal = ReadAmount(totalText, "сумма", out var total);
        var validIncome = ReadAmount(incomeText, "приход", out var income);
        var validExpense = ReadAmount(expenseText, "расход", out var expense);
        if (!validTotal || !validIncome || !validExpense)
            return (0m, MoneyOperationDirection.Income, false);
        if (income < 0m || expense < 0m || (income > 0m && expense > 0m))
        {
            errors.Add($"Противоречивые приход «{StatementValueForError(incomeText)}» и расход «{StatementValueForError(expenseText)}».");
            return (0m, MoneyOperationDirection.Income, false);
        }
        var hasSides = incomeText is not null || expenseText is not null;
        var sideAmount = income > 0m ? income : expense;
        var direction = hasSides
            ? expense > 0m ? MoneyOperationDirection.Expense : MoneyOperationDirection.Income
            : total < 0m ? MoneyOperationDirection.Expense : MoneyOperationDirection.Income;
        var amount = totalText is not null ? Math.Abs(total) : sideAmount;
        if (amount <= 0m || (hasSides && (sideAmount <= 0m || amount != sideAmount
            || (totalText is not null && total < 0m && direction == MoneyOperationDirection.Income))))
        {
            errors.Add($"Нулевая или противоречивая сумма: «{StatementValueForError(totalText)}», приход «{StatementValueForError(incomeText)}», расход «{StatementValueForError(expenseText)}».");
            return (0m, direction, hasSides || total < 0m);
        }
        return (amount, direction, hasSides || total < 0m);

        bool ReadAmount(string? text, string title, out decimal value)
        {
            value = 0m;
            if (text is null || TryParseStatementAmount(text, out value)) return true;
            errors.Add($"Не удалось однозначно прочитать {title} «{StatementValueForError(text)}» без округления.");
            return false;
        }
    }

    public static bool TryGetCell(IReadOnlyList<string> row, int index, out string value)
    {
        if (index >= 0 && index < row.Count)
        {
            value = row[index].Trim();
            return !string.IsNullOrWhiteSpace(value);
        }

        value = string.Empty;
        return false;
    }

    public static string? GetOptionalCell(IReadOnlyList<string> row, int index)
        => TryGetCell(row, index, out var value) ? value : null;

    public static string? NormalizeStatementText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static string BuildBankStatementImportKey(
        DateTime date,
        MoneyOperationDirection direction,
        decimal amount,
        string? documentNumber,
        string? counterparty)
        => string.Join("|", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), direction, amount.ToString("0.00", CultureInfo.InvariantCulture), documentNumber ?? string.Empty, counterparty ?? string.Empty);

    public static BankStatementInvoiceCandidate? TryResolveBankStatementInvoiceMatch(
        BankStatementImportItem item,
        IReadOnlyCollection<BankStatementInvoiceCandidate> candidates,
        IReadOnlyDictionary<int, decimal> reservedByInvoice)
    {
        if (item.Direction is not (MoneyOperationDirection.Income or MoneyOperationDirection.Expense))
        {
            return null;
        }

        var targetPaymentType = item.Direction == MoneyOperationDirection.Expense
            ? PaymentType.Expense
            : PaymentType.Income;
        var tokens = ExtractInvoiceNumberTokens(item.Purpose).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (tokens.Count > 0)
        {
            var matchingCandidates = candidates
                .Where(candidate =>
                    candidate.PaymentType == targetPaymentType &&
                    (candidate.OutstandingAmount - reservedByInvoice.GetValueOrDefault(candidate.InvoiceId)) > 0m &&
                    item.Amount <= (candidate.OutstandingAmount - reservedByInvoice.GetValueOrDefault(candidate.InvoiceId)) + 0.009m &&
                    candidate.MatchingNumberTokens.Any(tokens.Contains))
                .GroupBy(candidate => candidate.InvoiceId)
                .Select(group => group.First())
                .ToList();

            if (matchingCandidates.Count == 1)
            {
                return matchingCandidates[0];
            }

            var exactAmountCandidates = matchingCandidates
                .Where(candidate => Math.Abs((candidate.OutstandingAmount - reservedByInvoice.GetValueOrDefault(candidate.InvoiceId)) - item.Amount) <= 0.009m)
                .ToList();
            if (exactAmountCandidates.Count == 1)
            {
                return exactAmountCandidates[0];
            }
        }

        var exactNamedCandidates = candidates
            .Where(candidate =>
                candidate.PaymentType == targetPaymentType &&
                (candidate.OutstandingAmount - reservedByInvoice.GetValueOrDefault(candidate.InvoiceId)) > 0m &&
                Math.Abs((candidate.OutstandingAmount - reservedByInvoice.GetValueOrDefault(candidate.InvoiceId)) - item.Amount) <= 0.009m &&
                NamesLookRelated(item.CounterpartyName, candidate.ClientName ?? candidate.CounterpartyName))
            .GroupBy(candidate => candidate.InvoiceId)
            .Select(group => group.First())
            .ToList();
        return exactNamedCandidates.Count == 1 ? exactNamedCandidates[0] : null;
    }

    public static string BuildBankStatementAutoMatchComment(BankStatementImportItem item, BankStatementInvoiceCandidate candidate)
    {
        var tokens = ExtractInvoiceNumberTokens(item.Purpose).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (tokens.Count > 0 && candidate.InvoiceNumberTokens.Any(tokens.Contains))
        {
            return $"Автоматически сверено по номеру счёта {candidate.InvoiceNumber} из назначения платежа.";
        }

        if (tokens.Count > 0 && candidate.ContractNumberTokens.Any(tokens.Contains))
        {
            return $"Автоматически сверено со счётом {candidate.InvoiceNumber} по номеру договора из назначения платежа.";
        }

        return candidate.PaymentType == PaymentType.Expense
            ? $"Автоматически сверено со счётом {candidate.InvoiceNumber} по сумме и поставщику из выписки."
            : $"Автоматически сверено со счётом {candidate.InvoiceNumber} по сумме и клиенту из выписки.";
    }

    public static BankStatementIncomeTypeCandidate? ResolveBankStatementIncomeType(
        BankStatementImportItem item,
        IReadOnlyCollection<BankStatementIncomeTypeCandidate> candidates,
        IReadOnlyCollection<BankStatementCategoryRule> manualCategoryRules)
    {
        var paymentType = item.Direction == MoneyOperationDirection.Income
            ? PaymentType.Income
            : PaymentType.Expense;
        var haystack = BuildBankStatementMatchHaystack(item);
        if (haystack.Length < 3)
        {
            return null;
        }

        var categoryRule = ResolveBankStatementCategoryRule(paymentType, haystack, manualCategoryRules);
        var matches = candidates
            .Where(candidate => candidate.PaymentType == paymentType)
            .Select(candidate => new
            {
                Candidate = candidate,
                Score = ScoreBankStatementIncomeTypeCandidate(candidate, haystack, categoryRule),
            })
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Candidate.Name)
            .ToList();

        if (matches.Count == 0)
        {
            return null;
        }

        return matches.Count == 1 || matches[0].Score > matches[1].Score
            ? matches[0].Candidate
            : null;
    }

    public static string ResolveBankStatementCategoryName(
        BankStatementImportItem item,
        IReadOnlyCollection<BankStatementCategoryRule> manualCategoryRules)
    {
        var paymentType = item.Direction == MoneyOperationDirection.Income
            ? PaymentType.Income
            : PaymentType.Expense;
        var haystack = BuildBankStatementMatchHaystack(item);
        return ResolveBankStatementCategoryRule(paymentType, haystack, manualCategoryRules)?.CategoryName
               ?? (item.Direction == MoneyOperationDirection.Income ? "Поступление по выписке" : "Списание по выписке");
    }

    public static BankStatementCategoryRule? ResolveBankStatementCategoryRule(
        PaymentType paymentType,
        string haystack,
        IReadOnlyCollection<BankStatementCategoryRule> manualCategoryRules)
    {
        if (haystack.Length < 3)
        {
            return null;
        }

        var matches = manualCategoryRules
            .Concat(BankStatementCategoryRules)
            .Where(rule => rule.PaymentType == paymentType)
            .Select(rule => new
            {
                Rule = rule,
                Score = rule.Keywords.Max(keyword => ScoreBankStatementKeyword(keyword, haystack)),
            })
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .ThenByDescending(match => match.Rule.IsManual)
            .ThenBy(match => match.Rule.CategoryName)
            .ToList();

        return matches.Count == 0
            ? null
            : matches[0].Rule;
    }

    public static int ScoreBankStatementIncomeTypeCandidate(
        BankStatementIncomeTypeCandidate candidate,
        string haystack,
        BankStatementCategoryRule? categoryRule)
    {
        var directScore = ScoreBankStatementIncomeType(candidate.Name, haystack);
        if (categoryRule is null)
        {
            return directScore;
        }

        if (categoryRule.IncomeTypeId == candidate.Id)
        {
            return Math.Max(directScore, 2200);
        }

        var categoryScore = ScoreBankStatementIncomeType(candidate.Name, NormalizeNameForMatch(categoryRule.CategoryName));
        return Math.Max(directScore, categoryScore > 0 ? 800 + categoryScore : 0);
    }

    public static int ScoreBankStatementIncomeType(string name, string haystack)
    {
        var normalizedName = NormalizeNameForMatch(name);
        if (normalizedName.Length < 3)
        {
            return 0;
        }

        if (haystack.Contains(normalizedName, StringComparison.Ordinal))
        {
            return 1000 + normalizedName.Length;
        }

        var words = normalizedName
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length >= 3)
            .ToArray();
        return words.Length > 1 && words.All(word => haystack.Contains(word, StringComparison.Ordinal))
            ? 500 + words.Sum(word => word.Length)
            : 0;
    }

    public static int ScoreBankStatementKeyword(string keyword, string haystack)
    {
        var normalizedKeyword = NormalizeNameForMatch(keyword);
        if (normalizedKeyword.Length < 3)
        {
            return 0;
        }

        if (haystack.Contains(normalizedKeyword, StringComparison.Ordinal))
        {
            return 1000 + normalizedKeyword.Length;
        }

        var words = normalizedKeyword
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length >= 3)
            .ToArray();
        return words.Length > 1 && words.All(word => haystack.Contains(word, StringComparison.Ordinal))
            ? 500 + words.Sum(word => word.Length)
            : 0;
    }

    public static string BuildBankStatementMatchHaystack(BankStatementImportItem item)
        => NormalizeNameForMatch(string.Join(' ', new[] { item.Purpose, item.CounterpartyName }
            .Where(value => !string.IsNullOrWhiteSpace(value))));

    public static IEnumerable<string> ExtractInvoiceNumberTokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        foreach (Match match in Regex.Matches(
                     text,
                     @"(?:сч[её]т\w*|договор\w*|contract|agreement|invoice|inv)\s*(?:№|#|n|no\.?)?\s*([A-Za-zА-Яа-я0-9][A-Za-zА-Яа-я0-9\/\-_]*)",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                     TimeSpan.FromMilliseconds(100)))
        {
            var token = NormalizeInvoiceNumberToken(match.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(token))
            {
                yield return token;
            }
        }

        foreach (Match match in Regex.Matches(
                     text,
                     @"(?:№|#)\s*([A-Za-zА-Яа-я0-9][A-Za-zА-Яа-я0-9\/\-_]*)",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                     TimeSpan.FromMilliseconds(100)))
        {
            var token = NormalizeInvoiceNumberToken(match.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(token))
            {
                yield return token;
            }
        }
    }

    public static HashSet<string> BuildInvoiceNumberTokenSet(params string?[] values)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var token = NormalizeInvoiceNumberToken(value);
            if (!string.IsNullOrWhiteSpace(token))
            {
                tokens.Add(token);
            }
        }

        return tokens;
    }

    public static string? NormalizeInvoiceNumberToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().Trim('№', '#', '.', ',', ';', ':', ')', '(', '[', ']');
        normalized = Regex.Replace(normalized, @"\s+", string.Empty, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        normalized = normalized.ToLowerInvariant();
        if (!normalized.All(char.IsDigit))
        {
            return normalized;
        }

        var withoutLeadingZeroes = normalized.TrimStart('0');
        return withoutLeadingZeroes.Length == 0 ? "0" : withoutLeadingZeroes;
    }

    public static decimal CalculateOutstandingAmount(decimal amount, decimal paidAmount, PaymentStatus status)
        => status == PaymentStatus.Completed
            ? 0m
            : Math.Round(Math.Max(amount - Math.Min(Math.Max(paidAmount, 0m), amount), 0m), 2, MidpointRounding.AwayFromZero);

    public static bool NamesLookRelated(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        var normalizedLeft = NormalizeNameForMatch(left);
        var normalizedRight = NormalizeNameForMatch(right);
        if (normalizedLeft.Length < 4 || normalizedRight.Length < 4)
        {
            return false;
        }

        return normalizedLeft.Contains(normalizedRight, StringComparison.OrdinalIgnoreCase) ||
               normalizedRight.Contains(normalizedLeft, StringComparison.OrdinalIgnoreCase) ||
               normalizedLeft.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                   .Intersect(normalizedRight.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase)
                   .Any(part => part.Length >= 4);
    }

    public static string NormalizeNameForMatch(string value)
    {
        var normalized = value.Trim().ToLowerInvariant().Replace('ё', 'е');
        normalized = Regex.Replace(normalized, @"[^\p{L}\p{Nd}]+", " ", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return Regex.Replace(normalized, @"\s+", " ", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)).Trim();
    }

    public static string? ResolveInvoiceCandidateCounterpartyName(Payment payment)
        => payment.Type == PaymentType.Expense
            ? FirstNonEmpty(
                payment.ExpenseClient?.Name,
                payment.ExpenseClient?.Company,
                payment.ExpenseLegalEntity?.ShortName,
                payment.ExpenseLegalEntity?.FullName,
                payment.CounterpartyName,
                payment.Client?.Name,
                payment.Client?.Company)
            : FirstNonEmpty(
                payment.Client?.Name,
                payment.Client?.Company,
                payment.CounterpartyName,
                payment.ExpenseClient?.Name,
                payment.ExpenseClient?.Company,
                payment.ExpenseLegalEntity?.ShortName,
                payment.ExpenseLegalEntity?.FullName);

    public static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    public sealed record BankStatementInvoiceCandidate(
        int PaymentId,
        int InvoiceId,
        string InvoiceNumber,
        string? Account,
        decimal Amount,
        decimal PaidAmount,
        PaymentStatus Status,
        PaymentType PaymentType,
        int? ClientId,
        string? ClientName,
        string? CounterpartyName,
        HashSet<string> InvoiceNumberTokens,
        HashSet<string> ContractNumberTokens)
    {
        public IEnumerable<string> MatchingNumberTokens => InvoiceNumberTokens.Concat(ContractNumberTokens);

        public decimal OutstandingAmount => CalculateOutstandingAmount(Amount, PaidAmount, Status);
    }

    public sealed record BankStatementIncomeTypeCandidate(
        int Id,
        string Name,
        PaymentType PaymentType);

    public sealed record BankStatementCategoryRule(
        string CategoryName,
        PaymentType PaymentType,
        IReadOnlyCollection<string> Keywords,
        int? IncomeTypeId = null,
        bool IsManual = false);

    public sealed class BankStatementManualCategoryRulesSettings
    {
        public List<BankStatementManualCategoryRule> Rules { get; set; } = [];
    }

    public sealed class BankStatementManualCategoryRule
    {
        public bool Enabled { get; set; } = true;
        public string CategoryName { get; set; } = string.Empty;
        public PaymentType PaymentType { get; set; } = PaymentType.Income;
        public string Keywords { get; set; } = string.Empty;
        public int? IncomeTypeId { get; set; }
    }

    public static bool TryParseStatementDate(string value, out DateTime date)
    {
        var formats = new[] { "dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy" };
        return DateTime.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date) ||
               DateTime.TryParse(value.Trim(), CultureInfo.GetCultureInfo("ru-RU"), DateTimeStyles.None, out date);
    }

    internal static bool IsSupportedStatementAmount(decimal amount)
        => amount is >= -9999999999999999.99m and <= 9999999999999999.99m
            && decimal.Round(amount, 2) == amount;

    public static bool TryParseStatementAmount(string value, out decimal amount)
    {
        amount = 0m;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128) return false;
        var normalized = value.Trim().Replace('\u00A0', ' ').Replace('\u202F', ' ');
        foreach (var suffix in new[] { "руб.", "руб", "₽" })
        {
            if (!normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            normalized = normalized[..^suffix.Length].TrimEnd();
            break;
        }
        if (normalized.Length == 0) return false;
        var sign = string.Empty;
        if (normalized[0] is '+' or '-')
        {
            sign = normalized[..1];
            normalized = normalized[1..];
        }
        if (normalized.Length == 0) return false;

        var commas = normalized.Count(character => character == ',');
        var dots = normalized.Count(character => character == '.');
        var integer = normalized;
        var fraction = string.Empty;
        char? grouping = normalized.Contains(' ') ? ' ' : null;
        if (commas > 0 && dots > 0)
        {
            var decimalIndex = Math.Max(normalized.LastIndexOf(','), normalized.LastIndexOf('.'));
            var decimalSeparator = normalized[decimalIndex];
            if (normalized.Count(character => character == decimalSeparator) != 1) return false;
            grouping = decimalSeparator == ',' ? '.' : ',';
            integer = normalized[..decimalIndex];
            fraction = normalized[(decimalIndex + 1)..];
            if (fraction.Length is < 1 or > 2 || !IsDigits(fraction)) return false;
        }
        else if (commas + dots == 1)
        {
            var decimalIndex = Math.Max(normalized.IndexOf(','), normalized.IndexOf('.'));
            integer = normalized[..decimalIndex];
            fraction = normalized[(decimalIndex + 1)..];
            // A lone three-digit suffix could be thousands or a fractional amount.
            if (fraction.Length is < 1 or > 2 || !IsDigits(fraction)) return false;
        }
        else if (commas + dots > 1)
        {
            grouping = commas > 0 ? ',' : '.';
        }
        if (integer.Length == 0 && fraction.Length > 0) integer = "0";
        if (!TryNormalizeIntegerPart(integer, grouping, out var digits)) return false;
        var canonical = sign + digits + (fraction.Length == 0 ? string.Empty : "." + fraction);
        if (!decimal.TryParse(canonical, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var parsed)
            || !IsSupportedStatementAmount(parsed))
            return false;
        amount = parsed;
        return true;

        static bool IsDigits(string text)
            => text.Length > 0 && text.All(character => character is >= '0' and <= '9');

        static bool TryNormalizeIntegerPart(string text, char? separator, out string digits)
        {
            digits = string.Empty;
            if (!separator.HasValue || !text.Contains(separator.Value))
            {
                if (!IsDigits(text)) return false;
                digits = text;
                return true;
            }
            var groups = text.Split(separator.Value);
            if (groups[0].Length is < 1 or > 3 || !IsDigits(groups[0])
                || groups.Skip(1).Any(group => group.Length != 3 || !IsDigits(group)))
                return false;
            digits = string.Concat(groups);
            return true;
        }
    }

    public sealed class StatementHeaderMap
    {
        public int ColumnCount { get; set; }
        public List<int> AmountColumns { get; } = [];
        public bool HasAmbiguousAmountColumns { get; set; }
        public int Date { get; set; } = -1;
        public int Amount { get; set; } = -1;
        public int Income { get; set; } = -1;
        public int Expense { get; set; } = -1;
        public int Counterparty { get; set; } = -1;
        public int Purpose { get; set; } = -1;
        public int DocumentNumber { get; set; } = -1;
        public int Account { get; set; } = -1;
        public int PayerAccount { get; set; } = -1;
        public int RecipientAccount { get; set; } = -1;
        public bool HasAmbiguousAccountColumns { get; set; }
    }

    public sealed record BankStatementImportItem(
        int RowNumber,
        DateTime Date,
        MoneyOperationDirection Direction,
        decimal Amount,
        string AccountName,
        string? CounterpartyName,
        string? Purpose,
        string? DocumentNumber)
    {
        public string? ValidationError { get; init; }
        public int? OriginRowId { get; init; }
        public int? BankStatementOperationId { get; init; }
        public string? ExternalOperationId { get; init; }
        public string? MovementKey { get; init; }
        public bool IsBooked { get; init; } = true;
    }

    public static void ApplyAdvanceReviewHint(MoneyOperation operation)
    {
        if (!IsUnallocatedAdvanceMoneyOperation(operation))
        {
            return;
        }

        operation.IsAdvancePayment = true;
        if (operation.MatchStatus == MoneyOperationMatchStatus.None)
        {
            operation.MatchStatus = MoneyOperationMatchStatus.ClarificationRequired;
        }

        operation.MatchComment = string.IsNullOrWhiteSpace(operation.MatchComment)
            ? "Поступление не связано со счетом. Проверьте его как возможный нераспределенный аванс."
            : operation.MatchComment.Trim();
    }

    public static bool IsUnallocatedAdvanceMoneyOperation(MoneyOperation operation)
        => operation.Source == MoneyOperationSource.Bank
           && operation.Direction == MoneyOperationDirection.Income
           && operation.AccountingType == MoneyOperationAccountingType.Fact
           && operation.Amount > 0m
           && !operation.LinkedInvoiceId.HasValue
           && operation.MatchStatus != MoneyOperationMatchStatus.Ignored;
}
