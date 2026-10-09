namespace PayPlanner.Api.Models;

public class Act : PayPlanner.Api.Services.Companies.IDocumentSeller
{
    public int Id { get; set; }
    public string Number { get; set; } = string.Empty;
    public string? Title { get; set; }
    public DateTime Date { get; set; }
    public decimal Amount { get; set; }
    public string CreationMode { get; set; } = ActCreationMode.Manual;
    public string? InvoiceNumber { get; set; }
    public int? InvoiceId { get; set; }
    public Payment? Invoice { get; set; }
    public int? OriginalActId { get; set; }
    public Act? OriginalAct { get; set; }
    public string? CounterpartyInn { get; set; }
    public ActStatus Status { get; set; } = ActStatus.Created;
    public string WorkflowStatus { get; set; } = ActWorkflowStatus.Draft;
    public DateTime? SentForSigningAt { get; set; }
    public DateTime? SignedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public int? CancelledByUserId { get; set; }
    public string? CancelledReason { get; set; }
    public int? ClientId { get; set; }
    public Client? Client { get; set; }
    public int? LegalEntityId { get; set; }
    public LegalEntity? LegalEntity { get; set; }
    public int? ResponsibleId { get; set; }
    public User? Responsible { get; set; }
    public string? Comment { get; set; }

    /// <summary>Собственная компания-продавец, выбранная для документа; пусто — компания по умолчанию.</summary>
    public int? SellerCompanyId { get; set; }

    /// <summary>Филиал, офис или точка компании-продавца; пусто — реквизиты самой компании.</summary>
    public int? SellerBranchId { get; set; }

    /// <summary>
    /// Снимок реквизитов продавца, сохранённый при первом оформлении документа
    /// (см. <c>DocumentSellerService</c>). После этого правки профиля компании документ не меняют.
    /// </summary>
    public string? SellerSnapshotJson { get; set; }
    public ICollection<ActLineItem> LineItems { get; set; } = new List<ActLineItem>();
    public ICollection<ContractAct> ContractActs { get; set; } = new List<ContractAct>();
    public ICollection<Act> CorrectionActs { get; set; } = new List<Act>();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
