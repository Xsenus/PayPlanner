using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Controllers.V2;

public partial class CasesV2Controller
{
    private sealed class CourtBindingPlan(ClientCase clientCase, ClientCaseCourtInstance? instance)
    {
        public ClientCase Case { get; } = clientCase;
        public ClientCaseCourtInstance? Instance { get; } = instance;
        public CourtCheckSource Source { get; } = CourtCheckSource.Capture(clientCase);
        public CourtSyncCaseState Fields { get; } = CourtSyncCaseState.Capture(clientCase);
        public CourtBindingInstanceState? InstanceState { get; } = CourtBindingInstanceState.Capture(instance);
    }

    private sealed record CourtBindingAccepted(CourtBindingPlan Binding, CourtSyncPrepared? Sync);

    private sealed record CourtBindingInstanceState(int Id, int CaseId, string Instance, int? CourtId,
        string CourtName, string CaseNumber, string JudicialUid, string SourceUrl, string JudgeName)
    {
        public static CourtBindingInstanceState? Capture(ClientCaseCourtInstance? instance) => instance is null ? null
            : new(instance.Id, instance.ClientCaseId, instance.Instance, instance.CourtId, instance.CourtName,
                instance.CourtCaseNumber, instance.JudicialUid, instance.CourtUrl, instance.JudgeName);
    }

    private async Task<ClientCaseCourtInstance?> LoadCourtBindingInstanceAsync(int caseId, string instance, CancellationToken ct)
    {
        if (_db.Database.IsNpgsql())
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCaseCourtInstances\" WHERE \"ClientCaseId\" = {caseId} AND \"Instance\" = {instance} FOR UPDATE NOWAIT", ct);
        return await _db.ClientCaseCourtInstances.FirstOrDefaultAsync(row => row.ClientCaseId == caseId && row.Instance == instance, ct);
    }

    private Task LoadCourtBindingComplaintsAsync(ClientCaseCourtInstance instance, CancellationToken ct)
        => _db.Entry(instance).Collection(row => row.Complaints).Query()
            .Where(complaint => complaint.ClientCaseId == instance.ClientCaseId && !complaint.IsArchived).LoadAsync(ct);

    private async Task<bool> HasCurrentCourtBindingInstanceAsync(CourtBindingInstanceState expected, bool pin, CancellationToken ct)
    {
        if (pin && _db.Database.IsNpgsql())
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCaseCourtInstances\" WHERE \"Id\" = {expected.Id} AND \"ClientCaseId\" = {expected.CaseId} FOR UPDATE NOWAIT", ct);
        var current = await _db.ClientCaseCourtInstances.AsNoTracking().FirstOrDefaultAsync(row => row.Id == expected.Id
            && row.ClientCaseId == expected.CaseId && row.Instance == expected.Instance, ct);
        return CourtBindingInstanceState.Capture(current) == expected;
    }
}
