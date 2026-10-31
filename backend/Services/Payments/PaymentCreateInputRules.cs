using System.Collections;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Companies;

namespace PayPlanner.Api.Services.Payments;

internal static class PaymentCreateInputRules
{
    internal const string StructuralNotesMessage = "Служебные пометки платежа формируются отдельными действиями системы. Уберите добавленные технические строки и повторите сохранение.";
    internal const string InitialReviewMessage = "Новый платёж нельзя создавать с готовым результатом распределения, исключения или возврата аванса. Сначала сохраните платёж, затем выполните нужное действие.";
    internal const string RelatedRecordsMessage = "Связанные записи нельзя создавать одновременно с платежом. Создайте их отдельно и выберите в карточке платежа.";
    internal const string IdentityMessage = "Новый платёж нужно создавать из новой карточки. Обновите форму и повторите сохранение.";
    private static readonly HashSet<string> InputProperties = new(PaymentCreateCommand.InputPropertyNames.Concat(new[]
    {
        // These scalar business fields belong to the older invoice/payment API;
        // they are not audit, workflow or immutable seller snapshot metadata.
        nameof(Payment.IsNotForAccounting), nameof(Payment.ClientCaseReference), nameof(Payment.CounterpartyName),
        nameof(Payment.LegacyAccountNumber), nameof(Payment.LegacyAccountDate), nameof(Payment.InvoiceDocumentTemplateKey)
    }), StringComparer.OrdinalIgnoreCase);
    private static readonly PropertyInfo[] ServerProperties = typeof(Payment).GetProperties()
        .Where(property => property.CanRead && property.CanWrite && !InputProperties.Contains(property.Name)).ToArray();

    public static void InitializeServerFields(Payment model)
    {
        // Keep the model identity used by existing actions, but only permit
        // logical input fields. Future writable output fields default to server
        // values rather than becoming an implicit mass-assignment surface.
        var defaults = new Payment();
        foreach (var property in ServerProperties) property.SetValue(model, property.GetValue(defaults));
    }

    public static async Task<string?> ValidateSellerSelectionAsync(PaymentContext db, Payment model, CancellationToken ct)
    {
        // No explicit seller keeps the existing payment-only path: no new default
        // company requirement, profile permission or snapshot creation side effect.
        if (!model.SellerCompanyId.HasValue && !model.SellerBranchId.HasValue) return null;
        var seller = new DocumentSellerService(db);
        if (await seller.ValidateSelectionAsync(model.SellerCompanyId, model.SellerBranchId, ct) is { } error) return error;
        await LockSellerSelectionAsync(db, model.SellerCompanyId, model.SellerBranchId, ct);
        return await seller.ValidateSelectionAsync(model.SellerCompanyId, model.SellerBranchId, ct);
    }

    internal static async Task LockSellerSelectionAsync(PaymentContext db, int? companyId, int? branchId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Seller selection requires the creating transaction.");
        if (!db.Database.IsNpgsql()) return; // SQLite BEGIN IMMEDIATE owns writer admission.
        // Shared with create-attempts; existing registry -> company -> branch order.
        // Keep these locks until the outer financial transaction commits/rolls back.
        var keys = new[] { "legalEntities.mainCompanyId", "legalEntities.mainCompanyIds", "legalEntities.defaultMainCompanyId" };
        await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"SystemSettings\" WHERE \"Key\" = ANY({keys}) ORDER BY \"Id\" FOR SHARE NOWAIT").ToArrayAsync(ct);
        if (companyId.HasValue)
            await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"LegalEntities\" WHERE \"Id\" = {companyId.Value} FOR UPDATE NOWAIT").ToArrayAsync(ct);
        if (branchId.HasValue)
            await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"CompanyBranches\" WHERE \"Id\" = {branchId.Value} FOR SHARE NOWAIT").ToArrayAsync(ct);
    }

    public static string? Validate(PaymentContext db, Payment model)
    {
        if (model.Id != 0) return IdentityMessage;
        // Entity parameters are kept for the existing API shape. Never let Add
        // traverse caller-controlled related entities, including future mapped
        // navigations. Scalar foreign keys retain their normal ACL validation.
        var entity = db.Model.FindEntityType(typeof(Payment))!;
        foreach (var navigation in entity.GetNavigations().Cast<INavigationBase>().Concat(entity.GetSkipNavigations()))
        {
            var value = navigation.PropertyInfo?.GetValue(model) ?? navigation.FieldInfo?.GetValue(model);
            if (!navigation.IsCollection)
            {
                if (value is not null) return RelatedRecordsMessage;
            }
            else if (value is not IEnumerable collection || collection.Cast<object?>().Any()) return RelatedRecordsMessage;
        }
        return ValidateCreationState(model);
    }

    public static string? ValidateCreationState(Payment model)
    {
        if (model.AdvanceReviewStatus is not (PaymentAdvanceReviewStatus.None
            or PaymentAdvanceReviewStatus.RequiresDecision or PaymentAdvanceReviewStatus.AcceptedAsAdvance)) return InitialReviewMessage;
        return ValidateSystemNotesChange(null, model.SystemNotes);
    }

    public static string? ValidateSystemNotesChange(string? original, string? incoming)
    {
        // Check the reserved namespaces, including unknown/future markers. Do not
        // strip input: that would silently change a command's logical meaning.
        if (string.IsNullOrEmpty(incoming) || !ContainsReservedMarker(incoming)) return null;
        var existing = (original ?? string.Empty).Split('\n').Select(line => line.Trim()).ToHashSet(StringComparer.Ordinal);
        foreach (var line in incoming.Split('\n'))
            if (ContainsReservedMarker(line) && !existing.Contains(line.Trim())) return StructuralNotesMessage;
        return null;
    }

    private static bool ContainsReservedMarker(string text)
        => text.Contains("[pp:", StringComparison.OrdinalIgnoreCase) || text.Contains("[PayPlanner:", StringComparison.OrdinalIgnoreCase);
}
