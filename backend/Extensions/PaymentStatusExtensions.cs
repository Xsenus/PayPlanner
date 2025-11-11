using PayPlanner.Api.Models;

namespace PayPlanner.Api.Extensions;

public static class PaymentStatusExtensions
{
    public static string ToDisplayString(this PaymentStatus status)
        => status switch
        {
            PaymentStatus.Pending => "Ожидается",
            PaymentStatus.Completed => "Выполнен",
            PaymentStatus.Overdue => "Просрочен",
            PaymentStatus.Processing => "В обработке",
            PaymentStatus.Cancelled => "Отменён",
            _ => status.ToString()
        };
}
