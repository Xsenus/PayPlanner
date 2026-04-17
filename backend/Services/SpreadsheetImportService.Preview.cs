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
                    ? "Файл не распознан. Поддерживаются счета, акты и списки контрагентов."
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
                selectable = true;
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
                    CreateCell("contact", "Контакт", draft.ContactName),
                    CreateCell("phone", "Телефон", draft.Phone),
                    CreateCell("email", "Email", draft.Email),
                    CreateCell("contract", "Договор", draft.ContractName),
                    CreateCell("article", "Статья", draft.Article),
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
            .Select(draft => BuildInvoiceKey(draft.Number, draft.Date))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .GroupBy(key => key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var draft in drafts.OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.RowNumber))
        {
            var issues = new List<string>(draft.ParseWarnings);
            var sourceKey = BuildInvoiceKey(draft.Number, draft.Date);
            var action = SpreadsheetImportOperationAction.Error;
            var selectable = false;
            string? message;
            var existing = !string.IsNullOrWhiteSpace(sourceKey) && state.Invoices.TryGetValue(sourceKey, out var existingItems)
                ? existingItems.FirstOrDefault()
                : null;
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
                selectable = true;
                message = existing is not null
                    ? changes.Count > 0
                        ? "Будет обновлен существующий счет и связанный платеж."
                        : "Счет уже есть в базе. По основным полям обновления не обнаружены."
                    : "Будет создан новый счет и платеж в реестре.";
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
                    CreateCell("date", "Дата", FormatDate(draft.Date)),
                    CreateCell("amount", "Сумма", FormatAmount(draft.Amount)),
                    CreateCell("dueDate", "Оплатить до", FormatDate(draft.DueDate)),
                    CreateCell("counterparty", "Контрагент", draft.CounterpartyName),
                    CreateCell("status", "Статус", draft.StatusText),
                    CreateCell("paidDate", "Дата оплаты", FormatDate(draft.PaidDate)),
                    CreateCell("act", "Акт", draft.ActReference),
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
            .Select(draft => BuildActKey(draft.Number, draft.Date))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .GroupBy(key => key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var draft in drafts.OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.RowNumber))
        {
            var issues = new List<string>(draft.ParseWarnings);
            var sourceKey = BuildActKey(draft.Number, draft.Date);
            var action = SpreadsheetImportOperationAction.Error;
            var selectable = false;
            string? message;
            var existing = !string.IsNullOrWhiteSpace(sourceKey) && state.Acts.TryGetValue(sourceKey, out var existingActs)
                ? existingActs.FirstOrDefault()
                : null;
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
                selectable = true;
                message = existing is not null
                    ? changes.Count > 0
                        ? "Будет обновлен существующий акт."
                        : "Акт уже есть в базе. По основным полям обновления не обнаружены."
                    : "Будет создан новый акт.";
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
                    CreateCell("date", "Дата", FormatDate(draft.Date)),
                    CreateCell("amount", "Сумма", FormatAmount(draft.Amount)),
                    CreateCell("counterparty", "Контрагент", draft.CounterpartyName),
                    CreateCell("inn", "ИНН", draft.CounterpartyInn),
                    CreateCell("status", "Статус", draft.StatusText),
                    CreateCell("invoice", "Счет", draft.InvoiceNumber),
                    CreateCell("paymentDate", "Дата платежа", FormatDate(draft.PaymentDate)),
                    CreateCell("responsible", "Ответственный", draft.ResponsibleName),
                },
            });
        }

        return BuildSection("acts", "Акты", rows);
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
        var incomingResponsible = ResolveResponsiblePreviewValue(users, draft.ResponsibleName ?? draft.AutoInvoiceResponsibleName);
        var incomingLegalEntityName = NormalizeText(draft.Name);
        var incomingDirector = NormalizeText(BuildCounterpartyLegalEntityDirector(draft));
        var incomingOrganizationForm = NormalizeText(BuildCounterpartyOrganizationForm(draft));

        if (existingClient is null)
        {
            AddPreviewChange(changes, "name", "Название", null, incomingClientName);
            AddPreviewChange(changes, "company", "Компания", null, incomingClientCompany);
            AddPreviewChange(changes, "inn", "ИНН", null, incomingInn);
            AddPreviewChange(changes, "phone", "Телефон", null, incomingPhone);
            AddPreviewChange(changes, "email", "Email", null, incomingEmail);
            AddPreviewChange(changes, "responsible", "Ответственный", null, incomingResponsible);
            if (importMode == CounterpartyImportModeLegalEntityWithClient)
            {
                AddPreviewChange(changes, "legalEntityName", "Юридическое лицо", null, incomingLegalEntityName);
                AddPreviewChange(changes, "director", "Руководитель", null, incomingDirector);
                AddPreviewChange(changes, "organizationForm", "Форма", null, incomingOrganizationForm);
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

        AddPreviewChange(changes, "number", "Номер", existing?.Number, NormalizeText(draft.Number));
        AddPreviewChange(changes, "date", "Дата", existing?.Date.HasValue == true ? FormatDate(existing.Date) : null, FormatDate(draft.Date));
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

        AddPreviewChange(changes, "number", "Номер", existing?.Number, NormalizeText(draft.Number));
        AddPreviewChange(changes, "date", "Дата", existing?.Date.HasValue == true ? FormatDate(existing.Date) : null, FormatDate(draft.Date));
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

        messages.AddRange(parsed.Messages);
        return messages;
    }
}
