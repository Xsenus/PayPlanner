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
