using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public class LegalEntity
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(200)]
    public string ShortName { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? FullName { get; set; }

    [MaxLength(20)]
    public string? Inn { get; set; }

    [MaxLength(20)]
    public string? Kpp { get; set; }

    [MaxLength(20)]
    public string? Ogrn { get; set; }

    [MaxLength(500)]
    public string? Address { get; set; }

    [MaxLength(100)]
    public string? Phone { get; set; }

    [MaxLength(200)]
    [EmailAddress]
    public string? Email { get; set; }

    [MaxLength(200)]
    public string? Director { get; set; }

    public int? ResponsibleUserId { get; set; }
    public User? ResponsibleUser { get; set; }

    [MaxLength(100)]
    public string? Country { get; set; }

    [MaxLength(16)]
    public string? BaseCurrency { get; set; }

    [MaxLength(32)]
    public string? ServiceLanguage { get; set; }

    [MaxLength(64)]
    public string? OrganizationForm { get; set; }

    [MaxLength(100)]
    public string? Timezone { get; set; }

    [MaxLength(64)]
    public string? TaxSystem { get; set; }

    [MaxLength(64)]
    public string? TaxSystemMode { get; set; }

    [MaxLength(16)]
    public string? IncomeTaxRate { get; set; }

    [MaxLength(16)]
    public string? IncomeExpenseTaxRate { get; set; }

    public bool? VatWithoutVat { get; set; }

    [MaxLength(32)]
    public string? Okved { get; set; }

    [MaxLength(32)]
    public string? Okpo { get; set; }

    [MaxLength(64)]
    public string? Okato { get; set; }

    [MaxLength(32)]
    public string? Oktmo { get; set; }

    [MaxLength(32)]
    public string? Okogu { get; set; }

    [MaxLength(16)]
    public string? Okfs { get; set; }

    [MaxLength(32)]
    public string? Okopf { get; set; }

    [MaxLength(32)]
    public string? Pfr { get; set; }

    [MaxLength(32)]
    public string? Fss { get; set; }

    [MaxLength(64)]
    public string? CharterCapital { get; set; }

    [MaxLength(64)]
    public string? TaxRegistrationDate { get; set; }

    [MaxLength(16)]
    public string? TaxOfficeCode { get; set; }

    [MaxLength(100)]
    public string? TaxRegion { get; set; }

    [MaxLength(300)]
    public string? IfnsName { get; set; }

    [MaxLength(100)]
    public string? TaxOfficePhone { get; set; }

    [MaxLength(20)]
    public string? TreasuryInn { get; set; }

    [MaxLength(20)]
    public string? TreasuryKpp { get; set; }

    [MaxLength(300)]
    public string? TreasuryRecipientName { get; set; }

    [MaxLength(16)]
    public string? TreasuryBankBik { get; set; }

    [MaxLength(300)]
    public string? TreasuryBankName { get; set; }

    [MaxLength(34)]
    public string? TreasuryAccountNumber { get; set; }

    [MaxLength(34)]
    public string? TreasuryCorrAccountNumber { get; set; }

    [MaxLength(32)]
    public string? TreasuryKbk { get; set; }

    [MaxLength(16000)]
    public string? BankAccountsJson { get; set; }

    [MaxLength(8000)]
    public string? CashRegistersJson { get; set; }

    [MaxLength(8000)]
    public string? CardsJson { get; set; }

    [MaxLength(8000)]
    public string? RequisitesEnglishJson { get; set; }

    [MaxLength(12000000)]
    public string? LogoDataUrl { get; set; }

    [MaxLength(12000000)]
    public string? SealDataUrl { get; set; }

    [MaxLength(12000000)]
    public string? DirectorSignatureDataUrl { get; set; }

    [MaxLength(8000)]
    public string? DocumentDirectorJson { get; set; }

    [MaxLength(8000)]
    public string? ChiefAccountantJson { get; set; }

    [MaxLength(8000)]
    public string? ContactPhonesJson { get; set; }

    [MaxLength(8000)]
    public string? ContactEmailsJson { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }

    public ICollection<Client> Clients { get; set; } = new List<Client>();
    public ICollection<ClientCase> Cases { get; set; } = new List<ClientCase>();
    public ICollection<Payment> Payments { get; set; } = new List<Payment>();
    public ICollection<Act> Acts { get; set; } = new List<Act>();
    public ICollection<LegalEntityContract> LegalEntityContracts { get; set; } = new List<LegalEntityContract>();
    public ICollection<Contract> Contracts { get; set; } = new List<Contract>();

    public ICollection<AddressRecord> Addresses { get; set; } = new List<AddressRecord>();
    public ICollection<UserCompanyAccess> UserCompanyAccesses { get; set; } = new List<UserCompanyAccess>();
}
