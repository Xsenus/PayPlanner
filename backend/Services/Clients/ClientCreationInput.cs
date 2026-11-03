using System.Collections;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore.Metadata;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Clients;

internal static class ClientCreationInput
{
    public static IActionResult? Validate(PaymentContext db, Client model)
    {
        // Addresses and passports form the client profile. Other entities have
        // their own authorized creation actions, as in the case input guard.
        foreach (var navigation in Relations(db, typeof(Client)))
        {
            if (navigation.Name is nameof(Client.Addresses) or nameof(Client.Passports)) continue;
            var value = navigation.PropertyInfo?.GetValue(model) ?? navigation.FieldInfo?.GetValue(model);
            if (HasValue(value)) return Invalid();
        }
        foreach (var address in model.Addresses ?? [])
            if (address is null || address.LegalEntityId.HasValue || HasOtherRelations(db, address, model)) return Invalid();
        foreach (var passport in model.Passports ?? [])
            if (passport is null || HasOtherRelations(db, passport, model)) return Invalid();
        return null;
    }

    private static bool HasOtherRelations<T>(PaymentContext db, T row, Client owner) where T : class
    {
        foreach (var navigation in Relations(db, typeof(T)))
        {
            var value = navigation.PropertyInfo?.GetValue(row) ?? navigation.FieldInfo?.GetValue(row);
            if (navigation.Name == nameof(AddressRecord.Client) && ReferenceEquals(value, owner)) continue;
            if (HasValue(value)) return true;
        }
        return false;
    }

    private static bool HasValue(object? value) => value is not null && (value is not IEnumerable items || items.Cast<object>().Any());

    private static IEnumerable<INavigationBase> Relations(PaymentContext db, Type type)
    {
        var entity = db.Model.FindEntityType(type)!;
        return entity.GetNavigations().Cast<INavigationBase>().Concat(entity.GetSkipNavigations());
    }

    private static IActionResult Invalid() => new BadRequestObjectResult(new
    {
        message = "Связанные дела, платежи и другие записи создавайте отдельно. Адреса и паспортные данные должны принадлежать только новому клиенту."
    });
}
