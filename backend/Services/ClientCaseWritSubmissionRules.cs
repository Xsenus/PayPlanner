using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public static class ClientCaseWritSubmissionRules
{
    public static string? ValidateEdit(ClientCase current, DateTime? receivedAt, DateTime? submittedAt, string? submittedTo)
        => current.ExecutiveWritReceivedAt == receivedAt && current.ExecutiveWritSubmittedAt == submittedAt
            && string.Equals(current.ExecutiveWritSubmittedTo, submittedTo, StringComparison.Ordinal)
            ? null
            : Validate(receivedAt, submittedAt, submittedTo);

    public static string? Validate(DateTime? receivedAt, DateTime? submittedAt, string? submittedTo)
    {
        if (!submittedAt.HasValue)
            return string.IsNullOrWhiteSpace(submittedTo) ? null : "Укажите дату подачи исполнительного листа.";
        if (!receivedAt.HasValue)
            return "Сначала укажите дату получения исполнительного листа.";
        if (submittedAt.Value.Date < receivedAt.Value.Date)
            return "Дата подачи исполнительного листа не может быть раньше даты его получения.";
        if (submittedTo?.Trim().ToLowerInvariant() is not ("fssp" or "bank"))
            return "Выберите, куда подан исполнительный лист: в ФССП или банк.";
        return null;
    }
}
