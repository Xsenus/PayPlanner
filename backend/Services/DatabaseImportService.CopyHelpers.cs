using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public sealed partial class DatabaseImportService
{
    private static void CopyRole(Role source, Role target)
    {
        target.Name = source.Name.Trim();
        target.Description = source.Description;
        target.CreatedAt = source.CreatedAt;
    }

    private static void CopyRolePermission(RolePermission source, RolePermission target, ImportMaps maps)
    {
        target.RoleId = MapRequired(maps.Roles, source.RoleId);
        target.Section = source.Section;
        target.CanView = source.CanView;
        target.CanCreate = source.CanCreate;
        target.CanEdit = source.CanEdit;
        target.CanDelete = source.CanDelete;
        target.CanExport = source.CanExport;
        target.CanViewAnalytics = source.CanViewAnalytics;
    }

    private static void CopyDealType(DealType source, DealType target)
    {
        target.Name = source.Name.Trim();
        target.Description = source.Description;
        target.ColorHex = source.ColorHex;
        target.IsActive = source.IsActive;
        target.CreatedAt = source.CreatedAt;
    }

    private static void CopyIncomeType(IncomeType source, IncomeType target)
    {
        target.Name = source.Name.Trim();
        target.Description = source.Description;
        target.ColorHex = source.ColorHex;
        target.IsActive = source.IsActive;
        target.CreatedAt = source.CreatedAt;
        target.PaymentType = source.PaymentType;
    }

    private static void CopyPaymentSource(PaymentSource source, PaymentSource target)
    {
        target.Name = source.Name.Trim();
        target.Description = source.Description;
        target.ColorHex = source.ColorHex;
        target.IsActive = source.IsActive;
        target.CreatedAt = source.CreatedAt;
    }

    private static void CopyServiceDictionaryItem(ServiceDictionaryItem source, ServiceDictionaryItem target, ImportMaps maps)
    {
        target.Name = source.Name.Trim();
        target.Description = source.Description;
        target.Unit = string.IsNullOrWhiteSpace(source.Unit) ? "шт" : source.Unit;
        target.DefaultPrice = source.DefaultPrice;
        target.ColorHex = source.ColorHex;
        target.IsActive = source.IsActive;
        target.CreatedAt = source.CreatedAt;
        target.CategoryId = source.CategoryId.HasValue ? MapId(maps.ServiceCategories, source.CategoryId.Value) : null;
    }

    private static void CopyServiceCategory(ServiceCategoryDictionaryItem source, ServiceCategoryDictionaryItem target)
    {
        target.Name = source.Name.Trim();
        target.Description = source.Description;
        target.ColorHex = source.ColorHex;
        target.IsActive = source.IsActive;
        target.CreatedAt = source.CreatedAt;
    }

    private static void CopyPaymentStatus(PaymentStatusEntity source, PaymentStatusEntity target)
    {
        target.Name = source.Name.Trim();
        target.Description = source.Description;
        target.ColorHex = source.ColorHex;
        target.IsActive = source.IsActive;
        target.CreatedAt = source.CreatedAt;
    }

    private static void CopyClientStatus(ClientStatusEntity source, ClientStatusEntity target)
    {
        target.Name = source.Name.Trim();
        target.Description = source.Description;
        target.ColorHex = source.ColorHex;
        target.IsActive = source.IsActive;
        target.CreatedAt = source.CreatedAt;
    }

    private static void CopyCountry(CountryDictionaryItem source, CountryDictionaryItem target)
    {
        target.Name = source.Name.Trim();
        target.Description = source.Description;
        target.ColorHex = source.ColorHex;
        target.IsActive = source.IsActive;
        target.CreatedAt = source.CreatedAt;
    }

    private static void CopyBaseCurrency(BaseCurrencyDictionaryItem source, BaseCurrencyDictionaryItem target)
    {
        target.Name = source.Name.Trim();
        target.Description = source.Description;
        target.ColorHex = source.ColorHex;
        target.IsActive = source.IsActive;
        target.CreatedAt = source.CreatedAt;
    }

    private static void CopyServiceLanguage(ServiceLanguageDictionaryItem source, ServiceLanguageDictionaryItem target)
    {
        target.Name = source.Name.Trim();
        target.Description = source.Description;
        target.ColorHex = source.ColorHex;
        target.IsActive = source.IsActive;
        target.CreatedAt = source.CreatedAt;
    }

    private static void CopyOrganizationForm(OrganizationFormDictionaryItem source, OrganizationFormDictionaryItem target)
    {
        target.Name = source.Name.Trim();
        target.Description = source.Description;
        target.ColorHex = source.ColorHex;
        target.IsActive = source.IsActive;
        target.CreatedAt = source.CreatedAt;
    }

    private static void CopyTimezone(TimezoneDictionaryItem source, TimezoneDictionaryItem target)
    {
        target.Name = source.Name.Trim();
        target.Description = source.Description;
        target.ColorHex = source.ColorHex;
        target.IsActive = source.IsActive;
        target.CreatedAt = source.CreatedAt;
    }

    private static void CopyTaxSystem(TaxSystemDictionaryItem source, TaxSystemDictionaryItem target)
    {
        target.Name = source.Name.Trim();
        target.Description = source.Description;
        target.ColorHex = source.ColorHex;
        target.IsActive = source.IsActive;
        target.CreatedAt = source.CreatedAt;
    }

    private static void CopyTaxSystemMode(TaxSystemModeDictionaryItem source, TaxSystemModeDictionaryItem target)
    {
        target.Name = source.Name.Trim();
        target.Description = source.Description;
        target.ColorHex = source.ColorHex;
        target.IsActive = source.IsActive;
        target.CreatedAt = source.CreatedAt;
    }

    private static void CopyInvoiceUnit(InvoiceUnitDictionaryItem source, InvoiceUnitDictionaryItem target)
    {
        target.Name = source.Name.Trim();
        target.Description = source.Description;
        target.ColorHex = source.ColorHex;
        target.IsActive = source.IsActive;
        target.CreatedAt = source.CreatedAt;
    }

    private static void CopyInvoiceItemName(InvoiceItemNameDictionaryItem source, InvoiceItemNameDictionaryItem target)
    {
        target.Name = source.Name.Trim();
        target.Unit = source.Unit;
        target.DefaultPrice = source.DefaultPrice;
        target.Description = source.Description;
        target.ColorHex = source.ColorHex;
        target.IsActive = source.IsActive;
        target.CreatedAt = source.CreatedAt;
    }

    private static void CopyOkin(OkinDictionaryItem source, OkinDictionaryItem target)
    {
        target.Code = source.Code.Trim();
        target.Name = source.Name;
        target.IsActive = source.IsActive;
    }

    private static void CopyOkso(OksoDictionaryItem source, OksoDictionaryItem target)
    {
        target.Code = source.Code.Trim();
        target.Name = source.Name;
        target.IsActive = source.IsActive;
    }

    private static void CopyOkpdtr(OkpdtrDictionaryItem source, OkpdtrDictionaryItem target)
    {
        target.Code = source.Code.Trim();
        target.Name = source.Name;
        target.IsActive = source.IsActive;
    }

    private static void CopySystemSetting(SystemSetting source, SystemSetting target)
    {
        target.Key = source.Key.Trim();
        target.Value = source.Value;
        target.CreatedAt = source.CreatedAt;
        target.UpdatedAt = source.UpdatedAt;
    }

    private static void CopyFormFieldRequirement(FormFieldRequirement source, FormFieldRequirement target)
    {
        target.Section = source.Section;
        target.FieldKey = source.FieldKey;
        target.IsRequired = source.IsRequired;
        target.CreatedAt = source.CreatedAt;
        target.UpdatedAt = source.UpdatedAt;
    }

    private static void CopyUser(User source, User target, int roleId, int? approvedByUserId)
    {
        target.Email = NormalizeEmail(source.Email);
        target.PasswordHash = source.PasswordHash;
        target.FullName = source.FullName;
        target.FirstName = source.FirstName;
        target.LastName = source.LastName;
        target.MiddleName = source.MiddleName;
        target.DateOfBirth = source.DateOfBirth;
        target.PhotoUrl = source.PhotoUrl;
        target.PhoneNumber = source.PhoneNumber;
        target.WhatsApp = source.WhatsApp;
        target.Telegram = source.Telegram;
        target.Instagram = source.Instagram;
        target.Messenger = source.Messenger;
        target.Viber = source.Viber;
        target.IsEmployee = source.IsEmployee;
        target.EmploymentStartDate = source.EmploymentStartDate;
        target.EmploymentEndDate = source.EmploymentEndDate;
        target.RoleId = roleId;
        target.IsActive = source.IsActive;
        target.IsApproved = source.IsApproved;
        target.ApprovedAt = source.ApprovedAt;
        target.ApprovedByUserId = approvedByUserId;
        target.CreatedAt = source.CreatedAt;
        target.UpdatedAt = source.UpdatedAt;
    }

    private static void CopyLegalEntity(LegalEntity source, LegalEntity target, ImportMaps maps)
    {
        target.ShortName = source.ShortName;
        target.FullName = source.FullName;
        target.Inn = source.Inn;
        target.Kpp = source.Kpp;
        target.Ogrn = source.Ogrn;
        target.Address = source.Address;
        target.Phone = source.Phone;
        target.Email = source.Email;
        target.Director = source.Director;
        target.ResponsibleUserId = MapId(maps.Users, source.ResponsibleUserId);
        target.Country = source.Country;
        target.BaseCurrency = source.BaseCurrency;
        target.ServiceLanguage = source.ServiceLanguage;
        target.OrganizationForm = source.OrganizationForm;
        target.Timezone = source.Timezone;
        target.TaxSystem = source.TaxSystem;
        target.TaxSystemMode = source.TaxSystemMode;
        target.IncomeTaxRate = source.IncomeTaxRate;
        target.IncomeExpenseTaxRate = source.IncomeExpenseTaxRate;
        target.VatWithoutVat = source.VatWithoutVat;
        target.Okved = source.Okved;
        target.Okpo = source.Okpo;
        target.Okato = source.Okato;
        target.Oktmo = source.Oktmo;
        target.Okogu = source.Okogu;
        target.Okfs = source.Okfs;
        target.Okopf = source.Okopf;
        target.Pfr = source.Pfr;
        target.Fss = source.Fss;
        target.CharterCapital = source.CharterCapital;
        target.TaxRegistrationDate = source.TaxRegistrationDate;
        target.TaxOfficeCode = source.TaxOfficeCode;
        target.TaxRegion = source.TaxRegion;
        target.IfnsName = source.IfnsName;
        target.TaxOfficePhone = source.TaxOfficePhone;
        target.TreasuryInn = source.TreasuryInn;
        target.TreasuryKpp = source.TreasuryKpp;
        target.TreasuryRecipientName = source.TreasuryRecipientName;
        target.TreasuryBankBik = source.TreasuryBankBik;
        target.TreasuryBankName = source.TreasuryBankName;
        target.TreasuryAccountNumber = source.TreasuryAccountNumber;
        target.TreasuryCorrAccountNumber = source.TreasuryCorrAccountNumber;
        target.TreasuryKbk = source.TreasuryKbk;
        target.BankAccountsJson = source.BankAccountsJson;
        target.CashRegistersJson = source.CashRegistersJson;
        target.CardsJson = source.CardsJson;
        target.RequisitesEnglishJson = source.RequisitesEnglishJson;
        target.LogoDataUrl = source.LogoDataUrl;
        target.SealDataUrl = source.SealDataUrl;
        target.DirectorSignatureDataUrl = source.DirectorSignatureDataUrl;
        target.DocumentDirectorJson = source.DocumentDirectorJson;
        target.ChiefAccountantJson = source.ChiefAccountantJson;
        target.ContactPhonesJson = source.ContactPhonesJson;
        target.ContactEmailsJson = source.ContactEmailsJson;
        target.Notes = source.Notes;
        target.CreatedAt = source.CreatedAt;
        target.UpdatedAt = source.UpdatedAt;
    }

    private static void CopyClient(Client source, Client target, ImportMaps maps)
    {
        target.Name = source.Name;
        target.Email = source.Email;
        target.Phone = source.Phone;
        target.Company = source.Company;
        target.Address = source.Address;
        target.Notes = source.Notes;
        target.IsActive = source.IsActive;
        target.CreatedAt = source.CreatedAt;
        target.LegalEntityId = MapId(maps.LegalEntities, source.LegalEntityId);
        target.ClientStatusId = MapId(maps.ClientStatuses, source.ClientStatusId);
        target.ResponsibleUserId = MapId(maps.Users, source.ResponsibleUserId);
    }

    private static void CopyClientCase(ClientCase source, ClientCase target, ImportMaps maps)
    {
        target.ClientId = MapId(maps.Clients, source.ClientId);
        target.LegalEntityId = MapId(maps.LegalEntities, source.LegalEntityId);
        target.Title = source.Title;
        target.Description = source.Description;
        target.Status = source.Status;
        target.CreatedAt = source.CreatedAt;
    }

    private static void CopyContract(Contract source, Contract target)
    {
        target.Number = source.Number;
        target.Title = source.Title;
        target.Date = source.Date;
        target.StartDate = source.StartDate;
        target.Description = source.Description;
        target.BillingMode = source.BillingMode;
        target.IsOpenEnded = source.IsOpenEnded;
        target.Amount = source.Amount;
        target.ValidUntil = source.ValidUntil;
        target.TerminationDate = source.TerminationDate;
        target.CreatedAt = source.CreatedAt;
        target.UpdatedAt = source.UpdatedAt;
    }

    private static void CopyInvoice(Invoice source, Invoice target)
    {
        target.Number = source.Number;
        target.Date = source.Date;
        target.CreatedAt = source.CreatedAt;
        target.UpdatedAt = source.UpdatedAt;
    }

    private static void CopyPayment(Payment source, Payment target, ImportMaps maps)
    {
        target.Account = source.Account;
        target.AccountDate = source.AccountDate;
        target.InvoiceId = MapId(maps.Invoices, source.InvoiceId);
        target.LegacyAccountNumber = source.LegacyAccountNumber;
        target.LegacyAccountDate = source.LegacyAccountDate;
        target.Amount = source.Amount;
        target.ClientId = MapId(maps.Clients, source.ClientId);
        target.ClientCaseId = MapId(maps.ClientCases, source.ClientCaseId);
        target.ContractId = MapId(maps.Contracts, source.ContractId);
        target.DocumentKind = source.DocumentKind;
        target.CreatedAt = source.CreatedAt;
        target.Date = source.Date;
        target.PlannedDate = source.PlannedDate;
        target.DealTypeId = MapId(maps.DealTypes, source.DealTypeId);
        target.Description = source.Description;
        target.CounterpartyName = source.CounterpartyName;
        target.IncomeTypeId = MapId(maps.IncomeTypes, source.IncomeTypeId);
        target.IsPaid = source.IsPaid;
        target.PaidAmount = source.PaidAmount;
        target.LastPaymentDate = source.LastPaymentDate;
        target.Notes = source.Notes;
        target.ClientCaseReference = source.ClientCaseReference;
        target.SystemNotes = source.SystemNotes;
        target.RescheduleCount = source.RescheduleCount;
        target.PaidDate = source.PaidDate;
        target.PaymentSourceId = MapId(maps.PaymentSources, source.PaymentSourceId);
        target.ServiceId = MapId(maps.Services, source.ServiceId);
        target.PaymentStatusId = MapId(maps.PaymentStatuses, source.PaymentStatusId);
        target.Status = source.Status;
        target.Type = source.Type;
    }

    private static void CopyAct(Act source, Act target, ImportMaps maps)
    {
        target.Number = source.Number;
        target.Title = source.Title;
        target.Date = source.Date;
        target.Amount = source.Amount;
        target.InvoiceNumber = source.InvoiceNumber;
        target.InvoiceId = MapId(maps.Invoices, source.InvoiceId);
        target.CounterpartyInn = source.CounterpartyInn;
        target.Status = source.Status;
        target.ClientId = MapId(maps.Clients, source.ClientId);
        target.ResponsibleId = MapId(maps.Users, source.ResponsibleId);
        target.Comment = source.Comment;
        target.CreatedAt = source.CreatedAt;
        target.UpdatedAt = source.UpdatedAt;
    }

    private static void CopyAddressRecord(AddressRecord source, AddressRecord target, ImportMaps maps)
    {
        target.Type = source.Type;
        target.Value = source.Value;
        target.UnrestrictedValue = source.UnrestrictedValue;
        target.PostalCode = source.PostalCode;
        target.Country = source.Country;
        target.RegionWithType = source.RegionWithType;
        target.CityWithType = source.CityWithType;
        target.StreetWithType = source.StreetWithType;
        target.House = source.House;
        target.Flat = source.Flat;
        target.FiasId = source.FiasId;
        target.KladrId = source.KladrId;
        target.GeoLat = source.GeoLat;
        target.GeoLon = source.GeoLon;
        target.IsPrimary = source.IsPrimary;
        target.ClientId = MapId(maps.Clients, source.ClientId);
        target.LegalEntityId = MapId(maps.LegalEntities, source.LegalEntityId);
        target.CreatedAt = source.CreatedAt;
        target.UpdatedAt = source.UpdatedAt;
    }

    private static void CopyClientPassport(ClientPassport source, ClientPassport target, ImportMaps maps)
    {
        target.ClientId = MapRequired(maps.Clients, source.ClientId);
        target.Series = source.Series;
        target.Number = source.Number;
        target.IssuedBy = source.IssuedBy;
        target.IssuedAt = source.IssuedAt;
        target.DepartmentCode = source.DepartmentCode;
        target.BirthPlacePassport = source.BirthPlacePassport;
        target.BirthPlaceFias = source.BirthPlaceFias;
        target.ValidFrom = source.ValidFrom;
        target.ValidTo = source.ValidTo;
        target.IsPrimary = source.IsPrimary;
        target.CreatedAt = source.CreatedAt;
        target.UpdatedAt = source.UpdatedAt;
    }

    private static void CopyPowerOfAttorney(PowerOfAttorney source, PowerOfAttorney target, ImportMaps maps)
    {
        target.ClientId = MapRequired(maps.Clients, source.ClientId);
        target.Number = source.Number;
        target.IssuedAt = source.IssuedAt;
        target.ValidUntil = source.ValidUntil;
        target.Issuer = source.Issuer;
        target.Notes = source.Notes;
        target.IsActive = source.IsActive;
        target.CreatedAt = source.CreatedAt;
        target.UpdatedAt = source.UpdatedAt;
    }

    private static void CopyMoneyOperation(MoneyOperation source, MoneyOperation target)
    {
        target.Source = source.Source;
        target.Direction = source.Direction;
        target.AccountingType = source.AccountingType;
        target.AccountName = source.AccountName;
        target.CounterpartyName = source.CounterpartyName;
        target.CategoryName = source.CategoryName;
        target.Amount = source.Amount;
        target.Currency = source.Currency;
        target.PaymentOrderNumber = source.PaymentOrderNumber;
        target.Date = source.Date;
        target.RecognitionDate = source.RecognitionDate;
        target.IsAdvancePayment = source.IsAdvancePayment;
        target.Purpose = source.Purpose;
        target.PaidInvoiceNumber = source.PaidInvoiceNumber;
        target.PaidAmount = source.PaidAmount;
        target.ProjectName = source.ProjectName;
        target.DirectionName = source.DirectionName;
        target.SalesPointName = source.SalesPointName;
        target.PaymentPriority = source.PaymentPriority;
        target.PaidAccountName = source.PaidAccountName;
        target.CreatedAt = source.CreatedAt;
        target.UpdatedAt = source.UpdatedAt;
    }

    private static void CopyUserSetting(UserSetting source, UserSetting target, ImportMaps maps)
    {
        target.UserId = MapRequired(maps.Users, source.UserId);
        target.Key = source.Key;
        target.Value = source.Value;
        target.CreatedAt = source.CreatedAt;
        target.UpdatedAt = source.UpdatedAt;
    }
}
