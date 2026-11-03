using System.Collections;
using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Cases;

internal static class CaseCreationInput
{
    private static readonly HashSet<string> ClearCommands =
    [
        nameof(ClientCase.ClearCourtClaimAmount), nameof(ClientCase.ClearCourtAwardedAmount), nameof(ClientCase.ClearCourtStateFeeAmount),
        nameof(ClientCase.ClearCourtRegisteredAt), nameof(ClientCase.ClearCourtDecisionEffectiveAt),
        nameof(ClientCase.ClearExecutiveWritReceivedAt), nameof(ClientCase.ClearExecutiveWritSubmission),
    ];
    private static readonly (PropertyInfo Property, object? DefaultValue)[] ComputedFields = BuildComputedFields();

    private static (PropertyInfo Property, object? DefaultValue)[] BuildComputedFields()
    {
        var defaults = new ClientCase();
        return typeof(ClientCase).GetProperties().Where(property => property.CanRead && property.CanWrite
            && property.GetCustomAttribute<NotMappedAttribute>() is not null && !ClearCommands.Contains(property.Name)
            && (property.PropertyType.IsValueType || property.PropertyType == typeof(string)))
            .Select(property => (property, property.GetValue(defaults))).ToArray();
    }

    public static void InitializeServerState(ClientCase model, int actorId, string actorName)
    {
        var utcNow = DateTime.UtcNow;
        // PostgreSQL stores microseconds; keep the stored stamp and reply identical.
        var now = new DateTime(utcNow.Ticks - utcNow.Ticks % 10, DateTimeKind.Utc);
        var caption = actorName.Length <= 256 ? actorName : actorName[..256].TrimEnd();
        model.CreatedAt = now;
        model.CourtSyncLastAttemptAt = null;
        model.CourtSyncLastSuccessAt = null;
        model.CourtSyncFailureStartedAt = null;
        model.CourtSyncLastError = string.Empty;
        model.NextHearingConfirmedForAt = null;
        model.NextHearingConfirmedAt = null;
        model.NextHearingConfirmedByUserId = null;
        model.NextHearingConfirmedByName = string.Empty;
        model.PositionReviewedAt = null;
        model.PositionReviewedByUserId = null;
        model.PositionReviewedByName = string.Empty;
        model.LastViewedAt = null;
        model.LastViewedByUserId = null;
        model.LastViewedByUserName = string.Empty;
        model.ActivityConfirmedAt = null;
        model.ActivityConfirmedByUserId = null;
        model.ActivityConfirmedByUserName = string.Empty;
        // Preserve the requested flags, but their author and time are server-owned.
        model.ControlMarkedAt = model.IsUnderControl ? now : null;
        model.ControlMarkedByUserId = model.IsUnderControl ? actorId : null;
        model.ControlMarkedByUserName = model.IsUnderControl ? caption : string.Empty;
        model.HiddenFromExecutorAt = model.IsHiddenFromExecutor ? now : null;
        model.HiddenFromExecutorByUserId = model.IsHiddenFromExecutor ? actorId : null;
        model.HiddenFromExecutorByUserName = model.IsHiddenFromExecutor ? caption : string.Empty;
        model.AccessRequestedAt = null;
        model.AccessRequestedByUserId = null;
        model.AccessRequestedByUserName = string.Empty;
        model.AccessRequestReason = string.Empty;
        foreach (var (property, value) in ComputedFields) property.SetValue(model, value);
    }

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
