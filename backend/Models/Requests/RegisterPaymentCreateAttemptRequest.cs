using System.Text.Json;

namespace PayPlanner.Api.Models.Requests;

public sealed record RegisterPaymentCreateAttemptRequest(
    string Operation, int? SellerCompanyId, int? SellerBranchId, int? PrimaryPaymentId)
{
    // Exact documented shape, including duplicate/case aliases; no server-owned fields.
    public static bool TryRead(JsonElement body, out RegisterPaymentCreateAttemptRequest? request)
    {
        request = null;
        if (body.ValueKind != JsonValueKind.Object) return false;
        string? operation = null;
        int? company = null, branch = null, primary = null;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in body.EnumerateObject())
        {
            if (!names.Add(item.Name)) return false;
            if (item.Name == "operation")
            {
                if (item.Value.ValueKind != JsonValueKind.String) return false;
                operation = item.Value.GetString();
                continue;
            }
            if (item.Name is not ("sellerCompanyId" or "sellerBranchId" or "primaryPaymentId")) return false;
            int? id = null;
            if (item.Value.ValueKind != JsonValueKind.Null)
            {
                if (item.Value.ValueKind != JsonValueKind.Number || !item.Value.TryGetInt32(out var value) || value <= 0) return false;
                id = value;
            }
            switch (item.Name)
            {
                case "sellerCompanyId": company = id; break;
                case "sellerBranchId": branch = id; break;
                case "primaryPaymentId": primary = id; break;
            }
        }
        if (operation is not ("primary" or "remainder") || branch.HasValue && !company.HasValue
            || operation == "primary" && primary.HasValue || operation == "remainder" && !primary.HasValue) return false;
        request = new(operation, company, branch, primary);
        return true;
    }
}
