using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Payments;

internal static class CancelledReceiptAnnotations
{
    public static bool HasFinancialChanges(Payment current, Payment model)
        => current.Amount != model.Amount || current.PaidAmount != model.PaidAmount || current.Type != model.Type || current.Status != model.Status
            || current.InvoiceId != model.InvoiceId || current.DocumentKind != (model.DocumentKind == default ? DocumentKind.Payment : model.DocumentKind)
            || current.ClientId != model.ClientId || current.LegalEntityId != model.LegalEntityId
            || current.ExpenseClientId != model.ExpenseClientId || current.ExpenseLegalEntityId != model.ExpenseLegalEntityId
            || current.ClientCaseId != model.ClientCaseId || current.ContractId != model.ContractId
            || current.DealTypeId != model.DealTypeId || current.IncomeTypeId != model.IncomeTypeId || current.ServiceId != model.ServiceId
            || current.PaymentSourceId != model.PaymentSourceId || current.PaymentStatusId != model.PaymentStatusId
            || current.Date.Date != model.Date.Date
            || (current.PlannedDate == default ? current.Date.Date : current.PlannedDate.Date)
                != (model.PlannedDate == default ? (current.PlannedDate == default ? current.Date.Date : current.PlannedDate.Date) : model.PlannedDate.Date)
            || current.PaidDate?.Date != model.PaidDate?.Date
            || current.LastPaymentDate?.Date != (model.LastPaymentDate ?? model.PaidDate ?? current.LastPaymentDate)?.Date
            || Normalize(current.Account) != Normalize(model.Account) || current.AccountDate?.Date != model.AccountDate?.Date
            || Normalize(current.PaymentOrderNumber) != Normalize(model.PaymentOrderNumber) || current.PaymentDocumentDate?.Date != model.PaymentDocumentDate?.Date
            || current.AdvanceReviewStatus != model.AdvanceReviewStatus || Normalize(current.AdvanceReviewReason) != Normalize(model.AdvanceReviewReason)
            || current.OverpaymentHandling != model.OverpaymentHandling || current.OverpaymentTargetPaymentId != model.OverpaymentTargetPaymentId
            || Normalize(current.OverpaymentComment) != Normalize(model.OverpaymentComment);

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
