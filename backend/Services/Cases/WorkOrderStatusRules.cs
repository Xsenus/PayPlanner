namespace PayPlanner.Api.Services;

internal static class WorkOrderStatusRules
{
    internal static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "approved" or "согласован" or "согласовано" => "approved",
        "pending_approval" or "pending-approval" or "on_approval" or "on-approval" or "на согласовании" => "pending_approval",
        "revision_required" or "revision-required" or "returned" or "returned_for_revision" or "на доработке" or "доработка" => "revision_required",
        "invoice_created" or "invoice-created" or "счет создан" or "счёт создан" => "invoice_created",
        "cancelled" or "canceled" or "отменен" or "отменён" => "cancelled",
        _ => "draft",
    };
}
