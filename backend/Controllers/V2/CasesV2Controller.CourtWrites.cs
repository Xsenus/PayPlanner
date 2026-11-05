using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Cases;

namespace PayPlanner.Api.Controllers.V2;

public partial class CasesV2Controller
{
    private Task<IActionResult> WithCurrentCourtWriteAsync(int id, Func<ClientCase, Task<IActionResult>> write, CancellationToken ct)
        => CaseEditTransaction.RunRelatedAsync(_db, User, id, async (clientCase, _) =>
        {
            if (!await HasCurrentCourtCheckActorAsync(ct)) return Unauthorized();
            return await write(clientCase);
        }, ct);

    private async Task CompleteCourtWriteAsync(IActionResult result, bool journalChanged, Func<Task>? notifyAfterCommit, CancellationToken ct)
    {
        if (result is not (OkObjectResult or CreatedResult)) return;
        if (notifyAfterCommit is not null)
        {
            await notifyAfterCommit();
            await _db.SaveChangesAsync(ct);
        }
        if (journalChanged) await _realtimePublisher.PublishTaskJournalChangedAsync(ct);
    }

    private async Task PinCourtEventDeadlineRowsAsync(int caseId, CancellationToken ct)
    {
        if (!_db.Database.IsNpgsql()) return;
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT "Id" FROM "ClientCaseProceduralDeadlines" WHERE "ClientCaseId" = {caseId} ORDER BY "Id" FOR UPDATE NOWAIT;
            SELECT "Id" FROM "ClientCaseCourtEvents" WHERE "ClientCaseId" = {caseId} ORDER BY "Id" FOR UPDATE NOWAIT;
            """, ct);
        await CaseEditTransaction.LockLinkedTasksAsync(_db, [caseId], ct);
    }

    private async Task PinCourtRelatedPartyAsync(int caseId, int? id, CancellationToken ct)
    {
        if (id.HasValue && _db.Database.IsNpgsql())
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCaseRelatedParties\" WHERE \"Id\" = {id.Value} AND \"ClientCaseId\" = {caseId} FOR SHARE NOWAIT", ct);
    }

    private async Task PinCourtPartyForWriteAsync(int caseId, int id, CancellationToken ct)
    {
        if (_db.Database.IsNpgsql())
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCaseCourtParties\" WHERE \"Id\" = {id} AND \"ClientCaseId\" = {caseId} FOR UPDATE NOWAIT", ct);
    }

    private async Task PinCourtDirectoryItemAsync(int? id, CancellationToken ct)
    {
        if (id.HasValue && _db.Database.IsNpgsql())
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCaseCourts\" WHERE \"Id\" = {id.Value} FOR SHARE NOWAIT", ct);
    }

    private async Task PinCourtComplaintDocumentAsync(int caseId, int? id, CancellationToken ct)
    {
        if (id.HasValue && _db.Database.IsNpgsql())
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCaseDocuments\" WHERE \"Id\" = {id.Value} AND \"ClientCaseId\" = {caseId} FOR SHARE NOWAIT", ct);
    }

    private async Task<ClientCaseCourtComplaint?> LoadCourtComplaintForWriteAsync(int caseId, string instance, int complaintId, CancellationToken ct)
    {
        var parent = await LoadCourtBindingInstanceAsync(caseId, instance, ct);
        if (parent is null) return null;
        if (_db.Database.IsNpgsql())
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCaseCourtComplaints\" WHERE \"Id\" = {complaintId} AND \"ClientCaseId\" = {caseId} AND \"CourtInstanceId\" = {parent.Id} FOR UPDATE NOWAIT", ct);
        return await _db.ClientCaseCourtComplaints.FirstOrDefaultAsync(row => row.Id == complaintId
            && row.ClientCaseId == caseId && row.CourtInstanceId == parent.Id && !row.IsArchived, ct);
    }
}
