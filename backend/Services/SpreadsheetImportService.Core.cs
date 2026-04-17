using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using System.Text.Json;

namespace PayPlanner.Api.Services;

public sealed partial class SpreadsheetImportService
{
    private async Task<SpreadsheetImportPreviewDto> PreviewCoreAsync(
        IReadOnlyCollection<SpreadsheetImportUploadFile> files,
        CancellationToken cancellationToken)
    {
        var parsed = ParseFiles(files);
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var state = await LoadStateAsync(db, cancellationToken);
        return BuildPreview(parsed, state);
    }

    private async Task<SpreadsheetImportBatchDetailDto> ApplyCoreAsync(
        IReadOnlyCollection<SpreadsheetImportUploadFile> files,
        SpreadsheetImportApplyRequest request,
        CancellationToken cancellationToken)
    {
        var selectedIds = request.SelectedPreviewIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToHashSet(StringComparer.Ordinal);

        if (selectedIds.Count == 0)
        {
            throw new InvalidOperationException("Не выбрана ни одна запись для импорта.");
        }

        var parsed = ParseFiles(files);
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var state = await LoadStateAsync(db, cancellationToken);
        var preview = BuildPreview(parsed, state);
        var counterpartyPreviewDrafts = BuildCounterpartyPlan(parsed.CounterpartyDrafts, parsed.InvoiceDrafts, parsed.ActDrafts);
        var requestedCounterpartyModes = request.CounterpartyOptions
            .Where(option => !string.IsNullOrWhiteSpace(option.PreviewId))
            .GroupBy(option => option.PreviewId.Trim(), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => NormalizeCounterpartyImportMode(group.Last().ImportMode),
                StringComparer.Ordinal);

        var selectedCounterpartyDrafts = counterpartyPreviewDrafts
            .Where(draft => selectedIds.Contains(draft.PreviewId))
            .ToList();
        var selectedInvoiceDrafts = parsed.InvoiceDrafts
            .Where(draft => selectedIds.Contains(draft.PreviewId))
            .ToList();
        var selectedActDrafts = parsed.ActDrafts
            .Where(draft => selectedIds.Contains(draft.PreviewId))
            .ToList();

        if (selectedCounterpartyDrafts.Count == 0
            && selectedInvoiceDrafts.Count == 0
            && selectedActDrafts.Count == 0)
        {
            throw new InvalidOperationException("После анализа не осталось записей, пригодных для импорта.");
        }

        var fallbackUserId = GetCurrentUserId();
        var fallbackUserName = GetCurrentUserName();
        var counterpartyPlan = selectedCounterpartyDrafts.ToList();

        var batch = new SpreadsheetImportBatch
        {
            Title = BuildBatchTitle(parsed.Files),
            Status = SpreadsheetImportBatchStatus.Applied,
            CreatedAt = DateTime.UtcNow,
            AppliedAt = DateTime.UtcNow,
            CreatedByUserId = fallbackUserId,
            CreatedByUserName = fallbackUserName,
            Files = parsed.Files.Select(file => new SpreadsheetImportBatchFile
            {
                FileName = file.FileName,
                FileHash = file.FileHash,
                DetectedKind = file.DetectedKind,
                SheetName = file.SheetName,
                RowCount = file.RowCount,
                CreatedAt = DateTime.UtcNow,
            }).ToList(),
        };

        db.SpreadsheetImportBatches.Add(batch);
        await db.SaveChangesAsync(cancellationToken);
        var batchId = batch.Id;
        db.ChangeTracker.Clear();

        foreach (var counterpartyDraft in counterpartyPlan)
        {
            var existingClientState = FindClient(state, counterpartyDraft.Name, counterpartyDraft.Inn, counterpartyDraft.Phone, counterpartyDraft.Email);
            var existingLegalEntityState = FindLegalEntity(
                state,
                counterpartyDraft.Name,
                counterpartyDraft.Inn,
                counterpartyDraft.Phone,
                counterpartyDraft.Email,
                existingClientState?.LegalEntityId);
            var defaultImportMode = ResolveDefaultCounterpartyImportMode(counterpartyDraft, existingClientState, existingLegalEntityState);
            var importMode = requestedCounterpartyModes.TryGetValue(counterpartyDraft.PreviewId, out var requestedImportMode)
                ? requestedImportMode
                : defaultImportMode;

            await ApplyCounterpartyAsync(db, batchId, counterpartyDraft, importMode, state, fallbackUserId, cancellationToken);
        }

        foreach (var invoiceDraft in selectedInvoiceDrafts)
        {
            await ApplyInvoiceAsync(db, batchId, invoiceDraft, state, cancellationToken);
        }

        foreach (var actDraft in selectedActDrafts)
        {
            await ApplyActAsync(db, batchId, actDraft, state, cancellationToken);
        }

        var appliedBatch = await db.SpreadsheetImportBatches
            .Include(item => item.Operations)
            .FirstAsync(item => item.Id == batchId, cancellationToken);

        appliedBatch.TotalRows = appliedBatch.Operations.Count;
        appliedBatch.CreatedRows = appliedBatch.Operations.Count(item => item.Action == SpreadsheetImportOperationAction.Created);
        appliedBatch.UpdatedRows = appliedBatch.Operations.Count(item => item.Action == SpreadsheetImportOperationAction.Updated);
        appliedBatch.SkippedRows = appliedBatch.Operations.Count(item => item.Action == SpreadsheetImportOperationAction.Skipped);
        appliedBatch.ErrorRows = appliedBatch.Operations.Count(item => item.Action == SpreadsheetImportOperationAction.Error);
        appliedBatch.Status = appliedBatch.ErrorRows > 0
            ? SpreadsheetImportBatchStatus.AppliedWithErrors
            : SpreadsheetImportBatchStatus.Applied;
        appliedBatch.SummaryJson = JsonSerializer.Serialize(new
        {
            preview.Messages,
            selectedCounterparties = selectedCounterpartyDrafts.Count,
            selectedInvoices = selectedInvoiceDrafts.Count,
            selectedActs = selectedActDrafts.Count,
            derivedCounterparties = counterpartyPlan.Count(item => item.DependencyPreviewIds.Count > 0 || item.Derived),
            legalEntityCounterparties = counterpartyPlan.Count(draft =>
            {
                var existingClientState = FindClient(state, draft.Name, draft.Inn, draft.Phone, draft.Email);
                var existingLegalEntityState = FindLegalEntity(
                    state,
                    draft.Name,
                    draft.Inn,
                    draft.Phone,
                    draft.Email,
                    existingClientState?.LegalEntityId);
                var defaultImportMode = ResolveDefaultCounterpartyImportMode(draft, existingClientState, existingLegalEntityState);
                var importMode = requestedCounterpartyModes.TryGetValue(draft.PreviewId, out var requestedImportMode)
                    ? requestedImportMode
                    : defaultImportMode;
                return string.Equals(importMode, CounterpartyImportModeLegalEntityWithClient, StringComparison.Ordinal);
            }),
        }, JsonOptions);

        await db.SaveChangesAsync(cancellationToken);

        return await GetBatchCoreAsync(batchId, cancellationToken)
               ?? throw new InvalidOperationException("Не удалось загрузить результат импорта.");
    }

    private async Task<IReadOnlyCollection<SpreadsheetImportBatchSummaryDto>> GetBatchesCoreAsync(CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var batches = await db.SpreadsheetImportBatches
            .AsNoTracking()
            .Include(item => item.Files)
            .OrderByDescending(item => item.CreatedAt)
            .Take(50)
            .ToListAsync(cancellationToken);

        return batches.Select(MapBatchSummary).ToList();
    }

    private async Task<SpreadsheetImportBatchDetailDto?> GetBatchCoreAsync(int batchId, CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var batch = await db.SpreadsheetImportBatches
            .AsNoTracking()
            .Include(item => item.Files)
            .Include(item => item.Operations)
            .FirstOrDefaultAsync(item => item.Id == batchId, cancellationToken);

        return batch is null ? null : MapBatchDetail(batch);
    }

    private async Task<SpreadsheetImportBatchDetailDto> RollbackCoreAsync(int batchId, CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var batch = await db.SpreadsheetImportBatches
            .Include(item => item.Operations)
            .FirstOrDefaultAsync(item => item.Id == batchId, cancellationToken);

        if (batch is null)
        {
            throw new InvalidOperationException("Сессия импорта не найдена.");
        }

        var orderedOperations = batch.Operations
            .Where(item => item.Action == SpreadsheetImportOperationAction.Created || item.Action == SpreadsheetImportOperationAction.Updated)
            .OrderByDescending(GetRollbackPriority)
            .ThenByDescending(item => item.Id)
            .ToList();

        var rolledBackCount = 0;
        foreach (var operation in orderedOperations)
        {
            var success = operation.SectionKey switch
            {
                "acts" => await RollbackActAsync(db, operation, cancellationToken),
                "invoices" => await RollbackInvoiceAsync(db, operation, cancellationToken),
                "counterparties" => await RollbackCounterpartyAsync(db, operation, cancellationToken),
                _ => false,
            };

            if (success)
            {
                rolledBackCount += 1;
            }
        }

        batch.RollbackRows = rolledBackCount;
        batch.RolledBackAt = DateTime.UtcNow;
        batch.Status = batch.Operations.Any(item =>
                item.Action is SpreadsheetImportOperationAction.Created or SpreadsheetImportOperationAction.Updated
                && item.RollbackAction is SpreadsheetImportRollbackAction.Skipped or SpreadsheetImportRollbackAction.Error)
            ? SpreadsheetImportBatchStatus.RolledBackWithConflicts
            : SpreadsheetImportBatchStatus.RolledBack;

        await db.SaveChangesAsync(cancellationToken);

        return await GetBatchCoreAsync(batchId, cancellationToken)
               ?? throw new InvalidOperationException("Не удалось загрузить результат отката.");
    }

    private static int GetRollbackPriority(SpreadsheetImportBatchOperation operation)
    {
        return operation.SectionKey switch
        {
            "acts" => 3,
            "invoices" => 2,
            "counterparties" => 1,
            _ => 0,
        };
    }
}
