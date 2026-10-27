namespace PayPlanner.Api.Models.Requests;

/// <summary>Выбор собственной компании и подразделения-продавца для счёта или акта. Пустые значения — компания по умолчанию.</summary>
public class DocumentSellerRequest
{
    public int? CompanyId { get; set; }
    public int? BranchId { get; set; }
}

public sealed record DocumentSellerResponse(int? CompanyId, int? BranchId, bool Frozen);

public sealed record DocumentSellerSnapshotPolicyResponse(string Timing);
