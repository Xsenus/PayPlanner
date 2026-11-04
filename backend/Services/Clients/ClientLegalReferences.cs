using System.Linq.Expressions;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Companies;

namespace PayPlanner.Api.Services.Clients;

// A visible client does not grant access to its linked counterparty/company.
// Reuse the same two authorities as the dedicated legal-entity detail action.
internal sealed class ClientLegalReferences(IQueryable<int> allowedIds)
{
    public IQueryable<int> AllowedIds { get; } = allowedIds;

    public static async Task<ClientLegalReferences> ResolveAsync(PaymentContext db, ClaimsPrincipal? actor,
        CancellationToken ct, IReadOnlyCollection<int>? relevantIds = null,
        IReadOnlyDictionary<string, DataVisibilityAccess?>? visibilityBySection = null)
    {
        var ownIds = await OwnCompanyRegistry.LoadIdsAsync(db, ct);
        var needCounterparties = relevantIds is null || relevantIds.Any(id => !ownIds.Contains(id));
        var allowed = db.LegalEntities.AsNoTracking().Where(_ => false).Select(row => row.Id);
        if (needCounterparties)
        {
            var visibility = visibilityBySection is not null && visibilityBySection.TryGetValue(RolePermissionCatalog.LegalEntities, out var resolved)
                ? resolved
                : await db.ResolveDataVisibilityAsync(actor, RolePermissionCatalog.LegalEntities, ct);
            allowed = db.LegalEntities.AsNoTracking().ApplyLegalEntityVisibility(visibility)
                .Where(row => !ownIds.Contains(row.Id)).Select(row => row.Id);
        }
        if (ownIds.Count > 0 && (relevantIds is null || relevantIds.Any(ownIds.Contains)))
        {
            var profile = await CompanyProfileAccess.ResolveAsync(db, actor, ct);
            if (profile?.CompanyIds.Count > 0)
            {
                var assigned = profile.CompanyIds.ToArray();
                var own = db.LegalEntities.AsNoTracking().Where(row => assigned.Contains(row.Id)).Select(row => row.Id);
                allowed = needCounterparties ? allowed.Concat(own) : own;
            }
        }
        return new ClientLegalReferences(allowed);
    }

    public static async Task PopulateAsync(PaymentContext db, ClaimsPrincipal? actor, IReadOnlyList<Client> clients,
        CancellationToken ct, ClientLegalReferences? access = null,
        IReadOnlyDictionary<string, DataVisibilityAccess?>? visibilityBySection = null)
    {
        var ids = clients.Where(row => row.LegalEntityId.HasValue).Select(row => row.LegalEntityId!.Value).Distinct().ToArray();
        var profiles = new Dictionary<int, LegalEntity>();
        if (ids.Length > 0)
        {
            access ??= await ResolveAsync(db, actor, ct, ids, visibilityBySection);
            profiles = await db.LegalEntities.AsNoTracking()
                .Where(row => ids.Contains(row.Id) && access.AllowedIds.Contains(row.Id))
                .Select(ProfileProjection).ToDictionaryAsync(row => row.Id, ct);
        }
        // Keep the structural FK, including historical links to hidden/deleted
        // records. These response objects must never replace persisted profiles.
        foreach (var client in clients)
            client.LegalEntity = client.LegalEntityId is int id ? profiles.GetValueOrDefault(id) : null;
    }

    // Explicit scalar allowlist: future fields need an intentional disclosure
    // review. Images and all navigation graphs stay on their dedicated APIs;
    // excluding the image columns here avoids fetching them before redaction.
    private static readonly Expression<Func<LegalEntity, LegalEntity>> ProfileProjection = row => new LegalEntity
    {
        Id = row.Id,
        ShortName = row.ShortName,
        FullName = row.FullName,
        Inn = row.Inn,
        Kpp = row.Kpp,
        Ogrn = row.Ogrn,
        Address = row.Address,
        Phone = row.Phone,
        Email = row.Email,
        Director = row.Director,
        ResponsibleUserId = row.ResponsibleUserId,
        ClientStatusId = row.ClientStatusId,
        IsClientAndSupplier = row.IsClientAndSupplier,
        Country = row.Country,
        BaseCurrency = row.BaseCurrency,
        ServiceLanguage = row.ServiceLanguage,
        OrganizationForm = row.OrganizationForm,
        Timezone = row.Timezone,
        TaxSystem = row.TaxSystem,
        TaxSystemMode = row.TaxSystemMode,
        IncomeTaxRate = row.IncomeTaxRate,
        IncomeExpenseTaxRate = row.IncomeExpenseTaxRate,
        VatWithoutVat = row.VatWithoutVat,
        Okved = row.Okved,
        Okpo = row.Okpo,
        Okato = row.Okato,
        Oktmo = row.Oktmo,
        Okogu = row.Okogu,
        Okfs = row.Okfs,
        Okopf = row.Okopf,
        Pfr = row.Pfr,
        Fss = row.Fss,
        CharterCapital = row.CharterCapital,
        TaxRegistrationDate = row.TaxRegistrationDate,
        TaxOfficeCode = row.TaxOfficeCode,
        TaxRegion = row.TaxRegion,
        IfnsName = row.IfnsName,
        TaxOfficePhone = row.TaxOfficePhone,
        TreasuryInn = row.TreasuryInn,
        TreasuryKpp = row.TreasuryKpp,
        TreasuryRecipientName = row.TreasuryRecipientName,
        TreasuryBankBik = row.TreasuryBankBik,
        TreasuryBankName = row.TreasuryBankName,
        TreasuryAccountNumber = row.TreasuryAccountNumber,
        TreasuryCorrAccountNumber = row.TreasuryCorrAccountNumber,
        TreasuryKbk = row.TreasuryKbk,
        BankAccountsJson = row.BankAccountsJson,
        CashRegistersJson = row.CashRegistersJson,
        CardsJson = row.CardsJson,
        RequisitesEnglishJson = row.RequisitesEnglishJson,
        DocumentDirectorJson = row.DocumentDirectorJson,
        ChiefAccountantJson = row.ChiefAccountantJson,
        ContactPhonesJson = row.ContactPhonesJson,
        ContactEmailsJson = row.ContactEmailsJson,
        Notes = row.Notes,
        ProfileStatus = row.ProfileStatus,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
    };
}
