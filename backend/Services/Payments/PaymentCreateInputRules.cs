using System.Collections;
using Microsoft.EntityFrameworkCore.Metadata;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Payments;

internal static class PaymentCreateInputRules
{
    internal const string RelatedRecordsMessage = "Связанные записи нельзя создавать одновременно с платежом. Создайте их отдельно и выберите в карточке платежа.";
    internal const string IdentityMessage = "Новый платёж нужно создавать из новой карточки. Обновите форму и повторите сохранение.";

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
