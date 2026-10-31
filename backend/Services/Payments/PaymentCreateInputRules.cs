using System.Collections;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Metadata;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Payments;

internal static class PaymentCreateInputRules
{
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
        return null;
    }
}
