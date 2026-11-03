using System.Collections;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Cases;

internal static class CaseCreationInput
{
    public static IActionResult? Validate(PaymentContext db, ClientCase model)
    {
        // Only mapped relations can be attached as an EF graph. Read-only scalar
        // lists such as CourtInstanceKeys are not relations and stay compatible.
        foreach (var navigation in db.Model.FindEntityType(typeof(ClientCase))!.GetNavigations())
        {
            var value = navigation.PropertyInfo?.GetValue(model) ?? navigation.FieldInfo?.GetValue(model);
            if (value is null) continue;
            if (navigation.IsCollection && value is IEnumerable items && !items.Cast<object>().Any()) continue;
            return new BadRequestObjectResult(new
            {
                message = "Создавайте связанные платежи, документы, клиентов и сотрудников отдельно, через соответствующие действия. В новом деле указывайте только их идентификаторы."
            });
        }
        return null;
    }
}
