namespace PayPlanner.Api.Models.Responses;

public sealed class BackfillExpenseInvoiceCasesResponse
{
    public bool DryRun { get; init; }

    public int Scanned { get; init; }

    public int Changed { get; init; }

    public int Applied { get; init; }

    public int Skipped { get; init; }

    public IReadOnlyList<BackfillExpenseInvoiceCaseItemDto> Items { get; init; } = [];
}

public sealed class BackfillExpenseInvoiceCaseItemDto
{
    public int PaymentId { get; init; }

    public int? InvoiceId { get; init; }

    public string? Account { get; init; }

    public int? ClientCaseId { get; init; }

    public string? ClientCaseTitle { get; init; }

    public int? CurrentExpenseClientId { get; init; }

    public int? SuggestedExpenseClientId { get; init; }

    public string? SuggestedExpenseClientName { get; init; }

    public string Action { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}
