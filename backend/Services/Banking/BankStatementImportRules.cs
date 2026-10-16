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

    public static (List<BankStatementImportItem> Items, BankStatementImportResultDto Result) ParseClientBankExchange(string text)
    {
        var items = new List<BankStatementImportItem>();
        var result = new BankStatementImportResultDto();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var statementAccount = string.Empty;
        Dictionary<string, string>? document = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("СекцияДокумент=", StringComparison.OrdinalIgnoreCase))
            {
                document = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            if (line.Equals("КонецДокумента", StringComparison.OrdinalIgnoreCase))
            {
                if (document is not null)
                {
                    AddClientBankExchangeDocument(document, statementAccount, items, result);
                    document = null;
                }

                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            if (document is not null)
            {
                document[key] = value;
            }
            else if (key.Equals("РасчСчет", StringComparison.OrdinalIgnoreCase))
            {
                statementAccount = NormalizeBankAccount(value);
            }
        }

        if (document is not null)
        {
            AddClientBankExchangeDocument(document, statementAccount, items, result);
        }

        return (items, result);
    }

    public static void AddClientBankExchangeDocument(
        IReadOnlyDictionary<string, string> document,
        string statementAccount,
        ICollection<BankStatementImportItem> items,
        BankStatementImportResultDto result)
    {
        result.RowsRead++;
        var documentNumber = NormalizeStatementText(GetDictionaryValue(document, "Номер"));

        if (!TryParseStatementDate(GetDictionaryValue(document, "Дата") ?? string.Empty, out var date))
        {
            result.Skipped++;
            result.Errors.Add($"Документ {documentNumber ?? result.RowsRead.ToString(CultureInfo.InvariantCulture)}: не удалось прочитать дату.");
            return;
        }

        if (!TryParseStatementAmount(GetDictionaryValue(document, "Сумма") ?? string.Empty, out var amount) || amount == 0m)
        {
            result.Skipped++;
            result.Errors.Add($"Документ {documentNumber ?? result.RowsRead.ToString(CultureInfo.InvariantCulture)}: не удалось прочитать сумму.");
            return;
        }

        var payerAccount = NormalizeBankAccount(GetDictionaryValue(document, "ПлательщикСчет"));
        var recipientAccount = NormalizeBankAccount(GetDictionaryValue(document, "ПолучательСчет"));
        var isExpense = !string.IsNullOrWhiteSpace(statementAccount) &&
                        string.Equals(statementAccount, payerAccount, StringComparison.Ordinal);
        var isIncome = !string.IsNullOrWhiteSpace(statementAccount) &&
                       string.Equals(statementAccount, recipientAccount, StringComparison.Ordinal);
        var direction = isExpense && !isIncome ? MoneyOperationDirection.Expense : MoneyOperationDirection.Income;
        var counterparty = NormalizeStatementText(direction == MoneyOperationDirection.Expense
            ? GetDictionaryValue(document, "Получатель")
            : GetDictionaryValue(document, "Плательщик"));
        var purpose = NormalizeStatementText(GetDictionaryValue(document, "НазначениеПлатежа"));
        var account = statementAccount;
        if (string.IsNullOrWhiteSpace(account))
        {
            account = direction == MoneyOperationDirection.Expense ? payerAccount : recipientAccount;
        }

        items.Add(new BankStatementImportItem(
            result.RowsRead,
            date.Date,
            direction,
            Math.Abs(amount),
            string.IsNullOrWhiteSpace(account) ? "Расчетный счет" : account,
            counterparty,
            purpose,
            documentNumber));
    }

    public static string? GetDictionaryValue(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out var value) ? value : null;

    public static string NormalizeBankAccount(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().Replace(" ", string.Empty);

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
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var firstLine = normalized.Split('\n', 2)[0];
        var delimiter = firstLine.Count(ch => ch == ';') >= firstLine.Count(ch => ch == '\t') ? ';' : '\t';
        if (firstLine.Count(ch => ch == ',') > firstLine.Count(ch => ch == delimiter))
        {
            delimiter = ',';
        }

        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;

        for (var index = 0; index < normalized.Length; index++)
        {
            var ch = normalized[index];
            if (ch == '"')
            {
                if (inQuotes && index + 1 < normalized.Length && normalized[index + 1] == '"')
                {
                    cell.Append('"');
                    index++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                continue;
            }

            if (!inQuotes && ch == delimiter)
            {
                row.Add(cell.ToString().Trim());
                cell.Clear();
                continue;
            }

            if (!inQuotes && ch == '\n')
            {
                row.Add(cell.ToString().Trim());
                cell.Clear();
                rows.Add(row);
                row = new List<string>();
                continue;
            }

            cell.Append(ch);
        }

        row.Add(cell.ToString().Trim());
        if (row.Any(value => !string.IsNullOrWhiteSpace(value)))
        {
            rows.Add(row);
        }

        return rows;
    }

    public static StatementHeaderMap BuildStatementHeaderMap(IReadOnlyList<string> header)
    {
        var map = new StatementHeaderMap();
        for (var index = 0; index < header.Count; index++)
        {
            var name = NormalizeHeaderName(header[index]);
            if (map.Date < 0 && (name.Contains("дата") || name.Contains("date")))
            {
                map.Date = index;
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
            else if (map.Account < 0 && (name.Contains("счет") || name.Contains("счёт") || name.Contains("account")))
            {
                map.Account = index;
            }
            else if (map.Income < 0 && (name.Contains("приход") || name.Contains("поступ") || name.Contains("кредит") || name.Contains("credit")))
            {
                map.Income = index;
            }
            else if (map.Expense < 0 && (name.Contains("расход") || name.Contains("спис") || name.Contains("дебет") || name.Contains("debit")))
            {
                map.Expense = index;
            }
            else if (map.Amount < 0 && (name.Contains("сумма") || name.Contains("amount")))
            {
                map.Amount = index;
            }
        }

        return map;
    }

    public static string NormalizeHeaderName(string value)
        => value.Trim().ToLowerInvariant().Replace(" ", string.Empty).Replace("_", string.Empty);

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

    public static bool TryParseStatementAmount(string value, out decimal amount)
    {
        var normalized = value
            .Trim()
            .Replace("\u00A0", string.Empty)
            .Replace(" ", string.Empty)
            .Replace("₽", string.Empty)
            .Replace("руб.", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("руб", string.Empty, StringComparison.OrdinalIgnoreCase);

        if (normalized.Contains(',') && normalized.Contains('.'))
        {
            normalized = normalized.Replace(".", string.Empty).Replace(',', '.');
        }
        else
        {
            normalized = normalized.Replace(',', '.');
        }

        return decimal.TryParse(normalized, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out amount);
    }

    public sealed class StatementHeaderMap
    {
        public int Date { get; set; } = -1;
        public int Amount { get; set; } = -1;
        public int Income { get; set; } = -1;
        public int Expense { get; set; } = -1;
        public int Counterparty { get; set; } = -1;
        public int Purpose { get; set; } = -1;
        public int DocumentNumber { get; set; } = -1;
        public int Account { get; set; } = -1;
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
