using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services;

public sealed partial class SpreadsheetImportService
{
    private SpreadsheetImportPreviewDto BuildPreview(ParsedImportBundle parsed, ImportState state)
    {
        var sections = new List<SpreadsheetImportPreviewSectionDto>();
        var counterpartyDrafts = BuildCounterpartyPlan(parsed.CounterpartyDrafts, parsed.InvoiceDrafts, parsed.ActDrafts);

        if (counterpartyDrafts.Count > 0)
        {
            sections.Add(BuildCounterpartySection(counterpartyDrafts, state));
        }

        if (parsed.InvoiceDrafts.Count > 0)
        {
            sections.Add(BuildInvoiceSection(parsed.InvoiceDrafts, state));
        }

        if (parsed.ActDrafts.Count > 0)
        {
            sections.Add(BuildActSection(parsed.ActDrafts, state));
        }

        return new SpreadsheetImportPreviewDto
        {
            StartedAt = DateTime.UtcNow,
            TotalSelectableRows = sections.Sum(section => section.SelectableRows),
            Files = parsed.Files.Select(file => new SpreadsheetImportPreviewFileDto
            {
                FileName = file.FileName,
                FileHash = file.FileHash,
                DetectedKind = file.DetectedKind,
                DetectedLabel = GetSectionLabel(file.DetectedKind),
                SheetName = file.SheetName,
                RowCount = file.RowCount,
                Message = file.DetectedKind == "unknown"
                    ? "Файл не распознан. Поддерживаются Excel-листы со счетами, актами и контрагентами, а также XML/ZIP выгрузки КУБ/1С."
                    : null,
            }).ToList(),
            Sections = sections,
            Messages = BuildPreviewMessages(parsed, sections),
        };
    }

    private List<CounterpartyDraft> BuildCounterpartyPlan(
        IReadOnlyCollection<CounterpartyDraft> selectedCounterpartyDrafts,
        IReadOnlyCollection<InvoiceDraft> selectedInvoiceDrafts,
        IReadOnlyCollection<ActDraft> selectedActDrafts)
    {
        var result = new List<CounterpartyDraft>();
        var indexByKey = new Dictionary<string, int>(StringComparer.Ordinal);

        void Upsert(CounterpartyDraft draft)
        {
            var keys = BuildCounterpartyKeys(draft.Name, draft.Inn, draft.Phone, draft.Email);
            if (keys.Count == 0)
            {
                return;
            }

            var existingIndex = keys
                .Where(key => indexByKey.TryGetValue(key, out _))
                .Select(key => indexByKey[key])
                .Cast<int?>()
                .FirstOrDefault();

            if (existingIndex.HasValue)
            {
                var merged = MergeCounterpartyDraft(result[existingIndex.Value], draft);
                result[existingIndex.Value] = merged;

                foreach (var key in BuildCounterpartyKeys(merged.Name, merged.Inn, merged.Phone, merged.Email))
                {
                    indexByKey[key] = existingIndex.Value;
                }

                return;
            }

            foreach (var key in keys)
            {
                indexByKey[key] = result.Count;
            }

            result.Add(draft);
        }

        foreach (var draft in selectedCounterpartyDrafts.OrderBy(item => item.RowNumber))
        {
            Upsert(draft);
        }

        foreach (var invoice in selectedInvoiceDrafts.OrderBy(item => item.RowNumber))
        {
            if (string.IsNullOrWhiteSpace(invoice.CounterpartyName))
            {
                continue;
            }

            Upsert(new CounterpartyDraft
            {
                PreviewId = $"{invoice.PreviewId}:counterparty",
                FileName = invoice.FileName,
                FileHash = invoice.FileHash,
                SheetName = invoice.SheetName,
                RowNumber = invoice.RowNumber,
                Name = invoice.CounterpartyName.Trim(),
                Inn = NormalizeInn(invoice.CounterpartyInn),
                ContractName = invoice.ContractName,
                Derived = true,
                DependencyPreviewIds = new List<string> { invoice.PreviewId },
                DependencyTitles = new List<string> { BuildInvoiceTitle(invoice.Number, invoice.Date) },
            });
        }

        foreach (var act in selectedActDrafts.OrderBy(item => item.RowNumber))
        {
            if (string.IsNullOrWhiteSpace(act.CounterpartyName))
            {
                continue;
            }

            Upsert(new CounterpartyDraft
            {
                PreviewId = $"{act.PreviewId}:counterparty",
                FileName = act.FileName,
                FileHash = act.FileHash,
                SheetName = act.SheetName,
                RowNumber = act.RowNumber,
                Name = act.CounterpartyName.Trim(),
                Inn = NormalizeInn(act.CounterpartyInn),
                ContractName = act.ContractName,
                Derived = true,
                DependencyPreviewIds = new List<string> { act.PreviewId },
                DependencyTitles = new List<string> { BuildActTitle(act.Number, act.Date) },
            });
        }

        return result;
    }

    private SpreadsheetImportPreviewSectionDto BuildCounterpartySection(
        IReadOnlyCollection<CounterpartyDraft> drafts,
        ImportState state)
    {
        var rows = new List<SpreadsheetImportPreviewRowDto>();
        var duplicateCounts = drafts
            .Select(draft => BuildCounterpartyKey(draft.Name, draft.Inn, draft.Phone, draft.Email))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .GroupBy(key => key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var draft in drafts.OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.RowNumber))
        {
            var issues = new List<string>();
            var sourceKey = BuildCounterpartyKey(draft.Name, draft.Inn, draft.Phone, draft.Email);
            var action = SpreadsheetImportOperationAction.Error;
            var selectable = false;
            string? message;
            var existingClient = FindClient(state, draft.Name, draft.Inn, draft.Phone, draft.Email);
            var existingLegalEntity = FindLegalEntity(
                state,
                draft.Name,
                draft.Inn,
                draft.Phone,
                draft.Email,
                existingClient?.LegalEntityId);
            var defaultImportMode = ResolveDefaultCounterpartyImportMode(draft, existingClient, existingLegalEntity);
            var changes = BuildCounterpartyChanges(draft, existingClient, existingLegalEntity, state.Users, defaultImportMode);

            if (string.IsNullOrWhiteSpace(draft.Name))
            {
                message = "Не удалось определить название контрагента.";
            }
            else if (string.IsNullOrWhiteSpace(sourceKey))
            {
                message = "Недостаточно данных для поиска уникального контрагента.";
            }
            else if (!seen.Add(sourceKey))
            {
                message = "Дубликат контрагента внутри загруженного набора.";
                action = SpreadsheetImportOperationAction.Skipped;
            }
            else
            {
                action = existingClient is not null
                    ? SpreadsheetImportOperationAction.Updated
                    : SpreadsheetImportOperationAction.Created;
                selectable = action == SpreadsheetImportOperationAction.Created || changes.Count > 0;
                message = draft.DependencyPreviewIds.Count > 0
                    ? existingClient is not null
                        ? changes.Count > 0
                            ? "Связанный контрагент уже есть в базе и будет точечно обновлен."
                            : "Связанный контрагент уже есть в базе. По основным полям обновления не требуются."
                        : "Связанный контрагент будет создан для выбранных документов."
                    : existingClient is not null
                        ? changes.Count > 0
                            ? "Будет обновлен существующий контрагент."
                            : "Контрагент уже есть в базе. По основным полям обновления не требуются."
                        : "Будет создан новый контрагент.";

                if (existingClient is not null && changes.Count == 0)
                {
                    action = SpreadsheetImportOperationAction.Skipped;
                }
            }

            if (!string.IsNullOrWhiteSpace(sourceKey)
                && duplicateCounts.TryGetValue(sourceKey, out var duplicateCount)
                && duplicateCount > 1)
            {
                issues.Add($"В файлах найдено строк с тем же ключом: {duplicateCount}.");
            }

            if (draft.DependencyTitles.Count > 0)
            {
                issues.Add($"Нужен для документов: {string.Join(", ", draft.DependencyTitles)}.");
            }

            issues.AddRange(BuildCounterpartyUpdateWarnings(draft, existingClient, state.Users));

            rows.Add(new SpreadsheetImportPreviewRowDto
            {
                PreviewId = draft.PreviewId,
                SourceFileName = draft.FileName,
                SourceFileHash = draft.FileHash,
                SourceRowNumber = draft.RowNumber,
                Action = action,
                IsSelectable = selectable,
                IsDerived = draft.DependencyPreviewIds.Count > 0 || draft.Derived,
                Title = draft.Name,
                Subtitle = draft.Inn ?? draft.Email ?? draft.Phone,
                Message = message,
                DependencyPreviewIds = draft.DependencyPreviewIds.ToList(),
                DependencyTitles = draft.DependencyTitles.ToList(),
                Issues = issues,
                CounterpartyImportMode = selectable ? defaultImportMode : null,
                CounterpartyImportModeHint = selectable
                    ? BuildCounterpartyImportModeHint(draft, defaultImportMode, existingClient, existingLegalEntity)
                    : null,
                AvailableCounterpartyImportModes = selectable ? GetAvailableCounterpartyImportModes() : new List<string>(),
                Changes = changes,
                Cells = new List<SpreadsheetImportPreviewCellDto>
                {
                    CreateCell("name", "Название", draft.Name),
                    CreateCell("inn", "ИНН", draft.Inn),
                    CreateCell("kpp", "КПП", draft.Kpp),
                    CreateCell("ogrn", "ОГРН", draft.Ogrn),
                    CreateCell("contact", "Контакт", draft.ContactName),
                    CreateCell("phone", "Телефон", draft.Phone),
                    CreateCell("email", "Email", draft.Email),
                    CreateCell("address", "Адрес", draft.Address),
                    CreateCell("contract", "Договор", draft.ContractName),
                    CreateCell("article", "Статья", draft.Article),
                    CreateCell("bank", "Банк", draft.BankName ?? draft.BankBik),
                    CreateCell("bankAccount", "Расчетный счет", draft.BankAccount),
                    CreateCell("responsible", "Ответственный", draft.ResponsibleName ?? draft.AutoInvoiceResponsibleName),
                },
            });
        }

        return BuildSection("counterparties", "Контрагенты", rows);
    }

    private SpreadsheetImportPreviewSectionDto BuildInvoiceSection(
        IReadOnlyCollection<InvoiceDraft> drafts,
        ImportState state)
    {
        var rows = new List<SpreadsheetImportPreviewRowDto>();
        var duplicateCounts = drafts
            .Select(draft => BuildInvoiceNaturalKey(draft.Number, draft.Date, draft.Type))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .GroupBy(key => key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var draft in drafts.OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.RowNumber))
        {
            var issues = new List<string>(draft.ParseWarnings);
            var sourceKeys = BuildInvoiceMatchKeys(draft.ExternalId, draft.Number, draft.Date, draft.Type);
            var sourceKey = BuildInvoiceNaturalKey(draft.Number, draft.Date, draft.Type);
            var action = SpreadsheetImportOperationAction.Error;
            var selectable = false;
            string? message;
            var existing = FindExistingInvoiceStateItem(state, sourceKeys);
            var changes = BuildInvoiceChanges(draft, existing, state.Users);

            if (string.IsNullOrWhiteSpace(draft.Number))
            {
                message = "Не найден номер счета.";
            }
            else if (!draft.Date.HasValue)
            {
                message = "Не найдена дата счета.";
            }
            else if (!draft.Amount.HasValue)
            {
                message = "Не найдена сумма счета.";
            }
            else if (string.IsNullOrWhiteSpace(draft.CounterpartyName))
            {
                message = "Не найден контрагент для счета.";
            }
            else if (string.IsNullOrWhiteSpace(sourceKey))
            {
                message = "Не удалось сформировать уникальный ключ счета.";
            }
            else if (!seen.Add(sourceKey))
            {
                message = "Дубликат счета внутри загруженного набора.";
                action = SpreadsheetImportOperationAction.Skipped;
            }
            else
            {
                action = existing is not null
                    ? SpreadsheetImportOperationAction.Updated
                    : SpreadsheetImportOperationAction.Created;
                selectable = action == SpreadsheetImportOperationAction.Created || changes.Count > 0;
                message = existing is not null
                    ? changes.Count > 0
                        ? "Будет обновлен существующий счет и связанный платеж."
                        : "Счет уже есть в базе. По основным полям обновления не обнаружены."
                    : "Будет создан новый счет и платеж в реестре.";

                if (existing is not null && changes.Count == 0)
                {
                    action = SpreadsheetImportOperationAction.Skipped;
                }
            }

            if (!string.IsNullOrWhiteSpace(sourceKey)
                && duplicateCounts.TryGetValue(sourceKey, out var duplicateCount)
                && duplicateCount > 1)
            {
                issues.Add($"В файлах найдено строк с тем же номером и датой: {duplicateCount}.");
            }

            rows.Add(new SpreadsheetImportPreviewRowDto
            {
                PreviewId = draft.PreviewId,
                SourceFileName = draft.FileName,
                SourceFileHash = draft.FileHash,
                SourceRowNumber = draft.RowNumber,
                Action = action,
                IsSelectable = selectable,
                Title = BuildInvoiceTitle(draft.Number, draft.Date),
                Subtitle = draft.CounterpartyName,
                Message = message,
                Issues = issues,
                Changes = changes,
                Cells = new List<SpreadsheetImportPreviewCellDto>
                {
                    CreateCell("number", "Номер", draft.Number),
                    CreateCell("externalId", "UID 1С", draft.ExternalId),
                    CreateCell("date", "Дата", FormatDate(draft.Date)),
                    CreateCell("type", "Тип", FormatPaymentTypeLabel(draft.Type)),
                    CreateCell("amount", "Сумма", FormatAmount(draft.Amount)),
                    CreateCell("dueDate", "Оплатить до", FormatDate(draft.DueDate)),
                    CreateCell("counterparty", "Контрагент", draft.CounterpartyName),
                    CreateCell("status", "Статус", draft.StatusText),
                    CreateCell("paidDate", "Дата оплаты", FormatDate(draft.PaidDate)),
                    CreateCell("act", "Акт", draft.ActReference),
                    CreateCell("contract", "Договор", draft.ContractName),
                    CreateCell("responsible", "Ответственный", draft.ResponsibleName ?? draft.CreatedByName),
                },
            });
        }

        return BuildSection("invoices", "Счета", rows);
    }

    private SpreadsheetImportPreviewSectionDto BuildActSection(
        IReadOnlyCollection<ActDraft> drafts,
        ImportState state)
    {
        var rows = new List<SpreadsheetImportPreviewRowDto>();
        var duplicateCounts = drafts
            .Select(draft => BuildActNaturalKey(draft.Number, draft.Date, draft.Type))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .GroupBy(key => key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var draft in drafts.OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.RowNumber))
        {
            var issues = new List<string>(draft.ParseWarnings);
            var sourceKeys = BuildActMatchKeys(draft.ExternalId, draft.Number, draft.Date, draft.Type);
            var sourceKey = BuildActNaturalKey(draft.Number, draft.Date, draft.Type);
            var action = SpreadsheetImportOperationAction.Error;
            var selectable = false;
            string? message;
            var existing = FindExistingActStateItem(state, sourceKeys);
            var changes = BuildActChanges(draft, existing, state.Users);

            if (string.IsNullOrWhiteSpace(draft.Number))
            {
                message = "Не найден номер акта.";
            }
            else if (!draft.Date.HasValue)
            {
                message = "Не найдена дата акта.";
            }
            else if (!draft.Amount.HasValue)
            {
                message = "Не найдена сумма акта.";
            }
            else if (string.IsNullOrWhiteSpace(draft.CounterpartyName))
            {
                message = "Не найден контрагент для акта.";
            }
            else if (string.IsNullOrWhiteSpace(sourceKey))
            {
                message = "Не удалось сформировать уникальный ключ акта.";
            }
            else if (!seen.Add(sourceKey))
            {
                message = "Дубликат акта внутри загруженного набора.";
                action = SpreadsheetImportOperationAction.Skipped;
            }
            else
            {
                action = existing is not null
                    ? SpreadsheetImportOperationAction.Updated
                    : SpreadsheetImportOperationAction.Created;
                selectable = action == SpreadsheetImportOperationAction.Created || changes.Count > 0;
                message = existing is not null
                    ? changes.Count > 0
                        ? "Будет обновлен существующий акт."
                        : "Акт уже есть в базе. По основным полям обновления не обнаружены."
                    : "Будет создан новый акт.";

                if (existing is not null && changes.Count == 0)
                {
                    action = SpreadsheetImportOperationAction.Skipped;
                }
            }

            if (!string.IsNullOrWhiteSpace(sourceKey)
                && duplicateCounts.TryGetValue(sourceKey, out var duplicateCount)
                && duplicateCount > 1)
            {
                issues.Add($"В файлах найдено строк с тем же номером и датой: {duplicateCount}.");
            }

            rows.Add(new SpreadsheetImportPreviewRowDto
            {
                PreviewId = draft.PreviewId,
                SourceFileName = draft.FileName,
                SourceFileHash = draft.FileHash,
                SourceRowNumber = draft.RowNumber,
                Action = action,
                IsSelectable = selectable,
                Title = BuildActTitle(draft.Number, draft.Date),
                Subtitle = draft.CounterpartyName,
                Message = message,
                Issues = issues,
                Changes = changes,
                Cells = new List<SpreadsheetImportPreviewCellDto>
                {
                    CreateCell("number", "Номер", draft.Number),
                    CreateCell("externalId", "UID 1С", draft.ExternalId),
                    CreateCell("date", "Дата", FormatDate(draft.Date)),
                    CreateCell("type", "Тип", FormatPaymentTypeLabel(draft.Type)),
                    CreateCell("amount", "Сумма", FormatAmount(draft.Amount)),
                    CreateCell("counterparty", "Контрагент", draft.CounterpartyName),
                    CreateCell("inn", "ИНН", draft.CounterpartyInn),
                    CreateCell("status", "Статус", draft.StatusText),
                    CreateCell("invoice", "Счет", draft.InvoiceNumber),
                    CreateCell("paymentDate", "Дата платежа", FormatDate(draft.PaymentDate)),
                    CreateCell("contract", "Договор", draft.ContractName),
                    CreateCell("responsible", "Ответственный", draft.ResponsibleName),
                },
            });
        }

        return BuildSection("acts", "Акты", rows);
    }

    private static InvoiceStateItem? FindExistingInvoiceStateItem(ImportState state, IReadOnlyCollection<string> keys)
    {
        foreach (var key in keys.Where(key => key.Contains("|number:", StringComparison.Ordinal)))
        {
            if (state.Invoices.TryGetValue(key, out var existingItems))
            {
                return existingItems.FirstOrDefault();
            }
        }

        foreach (var key in keys.Where(key => key.Contains("|uid:", StringComparison.Ordinal)))
        {
            if (!state.Invoices.TryGetValue(key, out var existingItems))
            {
                continue;
            }

            var distinctItems = existingItems
                .Where(item => !item.ImportedInCurrentBatch)
                .GroupBy(item => item.PaymentId)
                .Select(group => group.First())
                .Take(2)
                .ToList();
            if (distinctItems.Count == 1)
            {
                return distinctItems[0];
            }
        }

        return null;
    }

    private static ActStateItem? FindExistingActStateItem(ImportState state, IReadOnlyCollection<string> keys)
    {
        foreach (var key in keys.Where(key => key.Contains("|number:", StringComparison.Ordinal)))
        {
            if (state.Acts.TryGetValue(key, out var existingItems))
            {
                return existingItems.FirstOrDefault();
            }
        }

        foreach (var key in keys.Where(key => key.Contains("|uid:", StringComparison.Ordinal)))
        {
            if (!state.Acts.TryGetValue(key, out var existingItems))
            {
                continue;
            }

            var distinctItems = existingItems
                .Where(item => !item.ImportedInCurrentBatch)
                .GroupBy(item => item.ActId)
                .Select(group => group.First())
                .Take(2)
                .ToList();
            if (distinctItems.Count == 1)
            {
                return distinctItems[0];
            }
        }

        return null;
    }

    private static SpreadsheetImportPreviewSectionDto BuildSection(
        string key,
        string label,
        IReadOnlyCollection<SpreadsheetImportPreviewRowDto> rows)
    {
        return new SpreadsheetImportPreviewSectionDto
        {
            Key = key,
            Label = label,
            TotalRows = rows.Count,
            CreateRows = rows.Count(row => row.Action == SpreadsheetImportOperationAction.Created),
            UpdateRows = rows.Count(row => row.Action == SpreadsheetImportOperationAction.Updated),
            SkipRows = rows.Count(row => row.Action == SpreadsheetImportOperationAction.Skipped),
            ErrorRows = rows.Count(row => row.Action == SpreadsheetImportOperationAction.Error),
            SelectableRows = rows.Count(row => row.IsSelectable),
            Rows = rows.ToList(),
        };
    }

    private static SpreadsheetImportPreviewCellDto CreateCell(string key, string label, string? value)
        => new()
        {
            Key = key,
            Label = label,
            Value = value,
        };

    private List<SpreadsheetImportPreviewChangeDto> BuildCounterpartyChanges(
        CounterpartyDraft draft,
        ClientStateItem? existingClient,
        LegalEntityStateItem? existingLegalEntity,
        IReadOnlyCollection<UserStateItem> users,
        string importMode)
    {
        var changes = new List<SpreadsheetImportPreviewChangeDto>();
        var incomingClientName = NormalizeText(BuildCounterpartyClientName(draft, importMode));
        var incomingClientCompany = NormalizeText(BuildCounterpartyClientCompany(draft, importMode));
        var incomingInn = NormalizeInn(draft.Inn);
        var incomingPhone = NormalizeText(draft.Phone);
        var incomingEmail = NormalizeEmail(draft.Email);
        var incomingAddress = NormalizeText(draft.Address);
        var incomingResponsible = ResolveResponsiblePreviewValue(users, draft.ResponsibleName ?? draft.AutoInvoiceResponsibleName);
        var incomingLegalEntityName = NormalizeText(draft.Name);
        var incomingDirector = NormalizeText(BuildCounterpartyLegalEntityDirector(draft));
        var incomingOrganizationForm = NormalizeText(BuildCounterpartyOrganizationForm(draft));
        var incomingKpp = NormalizeText(draft.Kpp);
        var incomingOgrn = NormalizeText(draft.Ogrn);
        var incomingBankAccount = NormalizeText(draft.BankAccount);

        if (existingClient is null)
        {
            AddPreviewChange(changes, "name", "Название", null, incomingClientName);
            AddPreviewChange(changes, "company", "Компания", null, incomingClientCompany);
            AddPreviewChange(changes, "inn", "ИНН", null, incomingInn);
            AddPreviewChange(changes, "phone", "Телефон", null, incomingPhone);
            AddPreviewChange(changes, "email", "Email", null, incomingEmail);
            AddPreviewChange(changes, "address", "Адрес", null, incomingAddress);
            AddPreviewChange(changes, "responsible", "Ответственный", null, incomingResponsible);
            if (importMode == CounterpartyImportModeLegalEntityWithClient)
            {
                AddPreviewChange(changes, "legalEntityName", "Юридическое лицо", null, incomingLegalEntityName);
                AddPreviewChange(changes, "kpp", "КПП", null, incomingKpp);
                AddPreviewChange(changes, "ogrn", "ОГРН", null, incomingOgrn);
                AddPreviewChange(changes, "legalAddress", "Юридический адрес", null, incomingAddress);
                AddPreviewChange(changes, "director", "Руководитель", null, incomingDirector);
                AddPreviewChange(changes, "organizationForm", "Форма", null, incomingOrganizationForm);
                AddPreviewChange(changes, "bankAccount", "Расчетный счет", null, incomingBankAccount);
            }

            return changes;
        }

        if (string.IsNullOrWhiteSpace(existingClient.Name))
        {
            AddPreviewChange(changes, "name", "Название", NormalizeText(existingClient.Name), incomingClientName);
        }

        if (string.IsNullOrWhiteSpace(existingClient.Company) && !string.IsNullOrWhiteSpace(incomingClientCompany))
        {
            AddPreviewChange(changes, "company", "Компания", NormalizeText(existingClient.Company), incomingClientCompany);
        }

        var currentInn = importMode == CounterpartyImportModeLegalEntityWithClient
            ? existingLegalEntity?.NormalizedInn ?? existingClient.NormalizedInn
            : existingClient.NormalizedInn;
        AddPreviewChange(changes, "inn", "ИНН", currentInn, incomingInn);

        if (string.IsNullOrWhiteSpace(existingClient.Phone))
        {
            AddPreviewChange(changes, "phone", "Телефон", NormalizeText(existingClient.Phone), incomingPhone);
        }

        if (string.IsNullOrWhiteSpace(existingClient.Email))
        {
            AddPreviewChange(changes, "email", "Email", existingClient.Email, incomingEmail);
        }

        if (string.IsNullOrWhiteSpace(existingClient.Address))
        {
            AddPreviewChange(changes, "address", "Адрес", existingClient.Address, incomingAddress);
        }

        if (string.IsNullOrWhiteSpace(existingClient.ResponsibleName))
        {
            AddPreviewChange(changes, "responsible", "Ответственный", existingClient.ResponsibleName, incomingResponsible);
        }

        if (importMode == CounterpartyImportModeLegalEntityWithClient)
        {
            AddPreviewChange(
                changes,
                "legalEntityName",
                "Юридическое лицо",
                NormalizeText(existingLegalEntity?.ShortName ?? existingClient.LegalEntityShortName),
                incomingLegalEntityName);
            AddPreviewChange(
                changes,
                "kpp",
                "КПП",
                NormalizeText(existingLegalEntity?.Kpp),
                incomingKpp);
            AddPreviewChange(
                changes,
                "ogrn",
                "ОГРН",
                NormalizeText(existingLegalEntity?.Ogrn),
                incomingOgrn);
            AddPreviewChange(
                changes,
                "legalAddress",
                "Юридический адрес",
                NormalizeText(existingLegalEntity?.Address),
                incomingAddress);
            AddPreviewChange(
                changes,
                "director",
                "Руководитель",
                NormalizeText(existingLegalEntity?.Director),
                incomingDirector);
            AddPreviewChange(
                changes,
                "organizationForm",
                "Форма",
                NormalizeText(existingLegalEntity?.OrganizationForm),
                incomingOrganizationForm);
            AddPreviewChange(
                changes,
                "bankAccount",
                "Расчетный счет",
                ExtractBankAccountPreview(existingLegalEntity?.BankAccountsJson),
                incomingBankAccount);
        }

        return changes;
    }

    private static List<string> BuildCounterpartyUpdateWarnings(
        CounterpartyDraft draft,
        ClientStateItem? existing,
        IReadOnlyCollection<UserStateItem> users)
    {
        if (existing is null)
        {
            return new List<string>();
        }

        var warnings = new List<string>();
        var incomingPhone = NormalizePhone(draft.Phone);
        var incomingEmail = NormalizeEmail(draft.Email);
        var incomingResponsible = ResolveResponsiblePreviewValue(users, draft.ResponsibleName ?? draft.AutoInvoiceResponsibleName);

        if (!string.IsNullOrWhiteSpace(incomingPhone)
            && !string.IsNullOrWhiteSpace(existing.NormalizedPhone)
            && !string.Equals(existing.NormalizedPhone, incomingPhone, StringComparison.Ordinal))
        {
            warnings.Add("Телефон в базе уже заполнен другим значением и не будет перезаписан автоматически.");
        }

        if (!string.IsNullOrWhiteSpace(incomingEmail)
            && !string.IsNullOrWhiteSpace(existing.NormalizedEmail)
            && !string.Equals(existing.NormalizedEmail, incomingEmail, StringComparison.Ordinal))
        {
            warnings.Add("Email в базе уже заполнен другим значением и не будет перезаписан автоматически.");
        }

        if (!string.IsNullOrWhiteSpace(incomingResponsible)
            && !string.IsNullOrWhiteSpace(existing.ResponsibleName)
            && !string.Equals(NormalizeText(existing.ResponsibleName), NormalizeText(incomingResponsible), StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("Ответственный уже назначен и не будет заменен автоматически.");
        }

        return warnings;
    }

    private List<SpreadsheetImportPreviewChangeDto> BuildInvoiceChanges(
        InvoiceDraft draft,
        InvoiceStateItem? existing,
        IReadOnlyCollection<UserStateItem> users)
    {
        var changes = new List<SpreadsheetImportPreviewChangeDto>();
        var incomingStatus = FormatPaymentStatusLabel(MapPaymentStatus(draft.StatusText, draft.DueDate ?? draft.Date, draft.PaidDate));
        var incomingResponsible = ResolveResponsiblePreviewValue(users, draft.ResponsibleName ?? draft.CreatedByName);

        AddPreviewChange(changes, "externalId", "UID 1С", existing?.ExternalId, NormalizeOneCExternalId(draft.ExternalId));
        AddPreviewChange(changes, "number", "Номер", existing?.Number, NormalizeText(draft.Number));
        AddPreviewChange(changes, "date", "Дата", existing?.Date.HasValue == true ? FormatDate(existing.Date) : null, FormatDate(draft.Date));
        AddPreviewChange(changes, "type", "Тип", existing is not null ? FormatPaymentTypeLabel(existing.Type) : null, FormatPaymentTypeLabel(draft.Type));
        AddPreviewChange(changes, "amount", "Сумма", existing is not null ? FormatAmount(existing.Amount) : null, FormatAmount(draft.Amount));
        AddPreviewChange(changes, "dueDate", "Оплатить до", existing?.DueDate.HasValue == true ? FormatDate(existing.DueDate) : null, FormatDate(draft.DueDate));
        AddPreviewChange(changes, "counterparty", "Контрагент", existing?.CounterpartyName, NormalizeText(draft.CounterpartyName));
        AddPreviewChange(changes, "status", "Статус", existing is not null ? FormatPaymentStatusLabel(existing.Status) : null, incomingStatus);
        AddPreviewChange(changes, "paidDate", "Дата оплаты", existing?.PaidDate.HasValue == true ? FormatDate(existing.PaidDate) : null, FormatDate(draft.PaidDate));
        AddPreviewChange(changes, "act", "Акт", existing?.ActReference, NormalizeText(draft.ActReference));
        AddPreviewChange(changes, "responsible", "Ответственный", existing?.ResponsibleName, incomingResponsible);

        return changes;
    }

    private List<SpreadsheetImportPreviewChangeDto> BuildActChanges(
        ActDraft draft,
        ActStateItem? existing,
        IReadOnlyCollection<UserStateItem> users)
    {
        var changes = new List<SpreadsheetImportPreviewChangeDto>();
        var incomingStatus = FormatActStatusLabel(MapActStatus(draft.StatusText));
        var incomingResponsible = ResolveResponsiblePreviewValue(users, draft.ResponsibleName);

        AddPreviewChange(changes, "externalId", "UID 1С", existing?.ExternalId, NormalizeOneCExternalId(draft.ExternalId));
        AddPreviewChange(changes, "number", "Номер", existing?.Number, NormalizeText(draft.Number));
        AddPreviewChange(changes, "date", "Дата", existing?.Date.HasValue == true ? FormatDate(existing.Date) : null, FormatDate(draft.Date));
        AddPreviewChange(changes, "type", "Тип", existing is not null ? FormatPaymentTypeLabel(existing.Type) : null, FormatPaymentTypeLabel(draft.Type));
        AddPreviewChange(changes, "amount", "Сумма", existing is not null ? FormatAmount(existing.Amount) : null, FormatAmount(draft.Amount));
        AddPreviewChange(changes, "counterparty", "Контрагент", existing?.CounterpartyName, NormalizeText(draft.CounterpartyName));
        AddPreviewChange(changes, "inn", "ИНН", existing?.CounterpartyInn, NormalizeInn(draft.CounterpartyInn));
        AddPreviewChange(changes, "status", "Статус", existing is not null ? FormatActStatusLabel(existing.Status) : null, incomingStatus);
        AddPreviewChange(changes, "invoice", "Счет", existing?.InvoiceNumber, NormalizeText(draft.InvoiceNumber));
        AddPreviewChange(changes, "responsible", "Ответственный", existing?.ResponsibleName, incomingResponsible);

        return changes;
    }

    private static void AddPreviewChange(
        ICollection<SpreadsheetImportPreviewChangeDto> changes,
        string fieldKey,
        string fieldLabel,
        string? currentValue,
        string? incomingValue)
    {
        var normalizedCurrent = NormalizeText(currentValue);
        var normalizedIncoming = NormalizeText(incomingValue);

        if (string.IsNullOrWhiteSpace(normalizedCurrent) && string.IsNullOrWhiteSpace(normalizedIncoming))
        {
            return;
        }

        if (string.Equals(normalizedCurrent, normalizedIncoming, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        changes.Add(new SpreadsheetImportPreviewChangeDto
        {
            FieldKey = fieldKey,
            FieldLabel = fieldLabel,
            Kind = string.IsNullOrWhiteSpace(normalizedCurrent)
                ? "Added"
                : string.IsNullOrWhiteSpace(normalizedIncoming)
                    ? "Removed"
                    : "Updated",
            CurrentValue = normalizedCurrent,
            IncomingValue = normalizedIncoming,
        });
    }

    private static string? ResolveResponsiblePreviewValue(IReadOnlyCollection<UserStateItem> users, string? value)
    {
        var userId = FindUserId(users, value);
        if (!userId.HasValue)
        {
            return NormalizeText(value);
        }

        return users.FirstOrDefault(user => user.Id == userId.Value)?.FullName ?? NormalizeText(value);
    }

    private static string FormatPaymentStatusLabel(PaymentStatus status)
        => status switch
        {
            PaymentStatus.Completed => "Оплачен",
            PaymentStatus.Overdue => "Просрочен",
            PaymentStatus.Processing => "В обработке",
            PaymentStatus.Cancelled => "Отменен",
            _ => "Ожидает",
        };

    private static string FormatActStatusLabel(ActStatus status)
        => status switch
        {
            ActStatus.Signed => "Подписан",
            ActStatus.Transferred => "Передан",
            ActStatus.Terminated => "Расторгнут",
            _ => "Создан",
        };

    private static List<string> BuildPreviewMessages(
        ParsedImportBundle parsed,
        IReadOnlyCollection<SpreadsheetImportPreviewSectionDto> sections)
    {
        var messages = new List<string>
        {
            $"Файлов обработано: {parsed.Files.Count}.",
            $"Записей доступно к импорту: {sections.Sum(section => section.SelectableRows)}.",
        };

        if (sections.Count > 0)
        {
            messages.AddRange(sections.Select(section =>
                $"{section.Label}: создать {section.CreateRows}, обновить {section.UpdateRows}, пропустить {section.SkipRows}, ошибки {section.ErrorRows}."));
        }

        if (parsed.InvoiceDrafts.Count > 0 || parsed.ActDrafts.Count > 0)
        {
            messages.Add("Связанные контрагенты из счетов и актов вынесены в раздел контрагентов и подтверждаются отдельно.");
        }

        if (parsed.InvoiceDrafts.Count > 0)
        {
            messages.Add("Каждый импортированный счет сохраняется как платеж типа «Счет» и сразу связывается с карточкой счета.");
        }

        if (parsed.OneCNonImportObjectCounts.Count > 0)
        {
            messages.Add($"XML также содержит объекты без отдельных строк импорта: {FormatOneCNonImportObjectCounts(parsed.OneCNonImportObjectCounts)}.");
        }

        messages.AddRange(parsed.Messages);
        return messages;
    }

    private static string FormatOneCNonImportObjectCounts(IReadOnlyDictionary<string, int> counts)
    {
        const int maxDisplayedTypes = 8;
        var parts = counts
            .OrderByDescending(item => item.Value)
            .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Take(maxDisplayedTypes)
            .Select(item => $"{item.Key}: {item.Value}")
            .ToList();

        var hiddenTypes = counts.Count - parts.Count;
        if (hiddenTypes > 0)
        {
            parts.Add($"еще {FormatOneCObjectTypeCount(hiddenTypes)}");
        }

        return string.Join(", ", parts);
    }

    private static string FormatOneCObjectTypeCount(int count)
    {
        var lastTwo = count % 100;
        var last = count % 10;
        var suffix = lastTwo is >= 11 and <= 14
            ? "типов"
            : last switch
            {
                1 => "тип",
                >= 2 and <= 4 => "типа",
                _ => "типов",
            };

        return $"{count} {suffix}";
    }
}
