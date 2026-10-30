using PayPlanner.Api.Services.Companies;

namespace PayPlanner.Api.Models.Responses;

public sealed class CompanyBranchDto
{
    public string EditVersion { get; set; } = string.Empty;
    public int Id { get; set; }
    public int LegalEntityId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Kpp { get; set; }
    public string? Address { get; set; }
    public string? BankAccountId { get; set; }
    public string? SignerJson { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public static CompanyBranchDto From(CompanyBranch branch) => new()
    {
        EditVersion = CompanyProfileEditVersion.Compute(branch),
        Id = branch.Id,
        LegalEntityId = branch.LegalEntityId,
        Kind = branch.Kind,
        Name = branch.Name,
        Code = branch.Code,
        Status = branch.Status,
        Kpp = branch.Kpp,
        Address = branch.Address,
        BankAccountId = branch.BankAccountId,
        SignerJson = branch.SignerJson,
        Notes = branch.Notes,
        CreatedAt = branch.CreatedAt,
        UpdatedAt = branch.UpdatedAt,
    };
}

public sealed record CompanyEffectiveRequisitesResponse(CompanyRequisitesSnapshot Requisites);
