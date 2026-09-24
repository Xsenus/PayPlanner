using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public sealed class ClientCaseHearingPreparation
{
    public int Id { get; set; }
    public int ClientCaseId { get; set; }
    public ClientCase ClientCase { get; set; } = null!;
    public DateTime HearingAt { get; set; }

    public DateTime? ContractCheckedAt { get; set; }
    public int? ContractCheckedByUserId { get; set; }
    [MaxLength(256)] public string ContractCheckedByName { get; set; } = string.Empty;

    public DateTime? AuthorityCheckedAt { get; set; }
    public int? AuthorityCheckedByUserId { get; set; }
    [MaxLength(256)] public string AuthorityCheckedByName { get; set; } = string.Empty;

    public DateTime? MaterialsCheckedAt { get; set; }
    public int? MaterialsCheckedByUserId { get; set; }
    [MaxLength(256)] public string MaterialsCheckedByName { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<ClientCaseHearingPreparationDocument> Documents { get; set; } = new List<ClientCaseHearingPreparationDocument>();
}

public sealed class ClientCaseHearingPreparationDocument
{
    public int ClientCaseHearingPreparationId { get; set; }
    public ClientCaseHearingPreparation Preparation { get; set; } = null!;
    public int ClientCaseDocumentId { get; set; }
    public ClientCaseDocument Document { get; set; } = null!;
}
