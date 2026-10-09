using System;
using System.Collections.Generic;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Models.Responses;

public class LegalEntityClientDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public bool IsActive { get; set; }
}

public class LegalEntityListItemDto
{
    public int Id { get; set; }
    public string ShortName { get; set; } = string.Empty;
    public string? FullName { get; set; }
    public string? Inn { get; set; }
    public string? Kpp { get; set; }
    public string? Ogrn { get; set; }
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Director { get; set; }
    public int? ResponsibleUserId { get; set; }
    public string? ResponsibleUserName { get; set; }
    public int? ClientStatusId { get; set; }
    public ClientStatusDto? ClientStatus { get; set; }
    public bool IsClientAndSupplier { get; set; }
    public string? Country { get; set; }
    public string? BaseCurrency { get; set; }
    public string? ServiceLanguage { get; set; }
    public string? OrganizationForm { get; set; }
    public string? Timezone { get; set; }
    public string? TaxSystem { get; set; }
    public string? TaxSystemMode { get; set; }
    public string? IncomeTaxRate { get; set; }
    public string? IncomeExpenseTaxRate { get; set; }
    public bool? VatWithoutVat { get; set; }
    public string? Okved { get; set; }
    public string? Okpo { get; set; }
    public string? Okato { get; set; }
    public string? Oktmo { get; set; }
    public string? Okogu { get; set; }
    public string? Okfs { get; set; }
    public string? Okopf { get; set; }
    public string? Pfr { get; set; }
    public string? Fss { get; set; }
    public string? CharterCapital { get; set; }
    public string? TaxRegistrationDate { get; set; }
    public string? TaxOfficeCode { get; set; }
    public string? TaxRegion { get; set; }
    public string? IfnsName { get; set; }
    public string? TaxOfficePhone { get; set; }
    public string? TreasuryInn { get; set; }
    public string? TreasuryKpp { get; set; }
    public string? TreasuryRecipientName { get; set; }
    public string? TreasuryBankBik { get; set; }
    public string? TreasuryBankName { get; set; }
    public string? TreasuryAccountNumber { get; set; }
    public string? TreasuryCorrAccountNumber { get; set; }
    public string? TreasuryKbk { get; set; }
    public string? BankAccountsJson { get; set; }
    public string? CashRegistersJson { get; set; }
    public string? CardsJson { get; set; }
    public string? RequisitesEnglishJson { get; set; }
    public string? LogoDataUrl { get; set; }
    public string? SealDataUrl { get; set; }
    public string? DirectorSignatureDataUrl { get; set; }
    public string? DocumentDirectorJson { get; set; }
    public string? ChiefAccountantJson { get; set; }
    public string? ContactPhonesJson { get; set; }
    public string? ContactEmailsJson { get; set; }
    public string? Notes { get; set; }
    public string ProfileStatus { get; set; } = CompanyProfileStatuses.Active;
    public int ClientsCount { get; set; }
    public int CasesCount { get; set; }
    public int ContractsCount { get; set; }
    public int InvoicesCount { get; set; }
    public int ActsCount { get; set; }
    public int PaymentsCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? AuditCreatedByUserId { get; set; }
    public string? AuditCreatedByUserName { get; set; }
    public DateTime? AuditUpdatedAt { get; set; }
    public int? AuditUpdatedByUserId { get; set; }
    public string? AuditUpdatedByUserName { get; set; }
    public bool AuditIsDeleted { get; set; }
    public DateTime? AuditDeletedAt { get; set; }
    public int? AuditDeletedByUserId { get; set; }
    public string? AuditDeletedByUserName { get; set; }
    public List<LegalEntityClientDto> Clients { get; set; } = new();
    public List<AddressRecordDto> Addresses { get; set; } = new();
    public List<RelatedDocumentLink> RelatedCases { get; set; } = new();
    public List<RelatedDocumentLink> RelatedContracts { get; set; } = new();
    public List<RelatedDocumentLink> RelatedInvoices { get; set; } = new();
    public List<RelatedDocumentLink> RelatedActs { get; set; } = new();
    public List<RelatedDocumentLink> RelatedPayments { get; set; } = new();
}

public class LegalEntityDetailDto : LegalEntityListItemDto
{
}

public class MainCompanySettingsDto
{
    public int? DefaultCompanyId { get; set; }
    public List<LegalEntityListItemDto> Companies { get; set; } = new();
}

public class LegalEntitySuggestionDto
{
    public string ShortName { get; set; } = string.Empty;
    public string? FullName { get; set; }
    public string? Inn { get; set; }
    public string? Kpp { get; set; }
    public string? Ogrn { get; set; }
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Director { get; set; }
    public int? ResponsibleUserId { get; set; }
    public string? ResponsibleUserName { get; set; }
}

public class AddressRecordDto
{
    public int Id { get; set; }
    public int Type { get; set; }
    public string Value { get; set; } = string.Empty;
    public string? UnrestrictedValue { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
    public string? RegionWithType { get; set; }
    public string? AreaWithType { get; set; }
    public string? CityWithType { get; set; }
    public string? SettlementWithType { get; set; }
    public string? StreetWithType { get; set; }
    public string? House { get; set; }
    public string? Flat { get; set; }
    public string? FiasId { get; set; }
    public string? KladrId { get; set; }
    public string? GeoLat { get; set; }
    public string? GeoLon { get; set; }
    public bool IsPrimary { get; set; }
    public int? ClientId { get; set; }
    public int? LegalEntityId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
