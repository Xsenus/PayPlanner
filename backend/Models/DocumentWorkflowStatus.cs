namespace PayPlanner.Api.Models;

public readonly record struct DocumentStatusSeed(
    string Code,
    string Name,
    string Description,
    string ColorHex,
    bool UseForInvoices,
    bool UseForActs,
    bool UseForContracts,
    int SortOrder);

public static class DocumentWorkflowStatus
{
    public const string Draft = "Draft";
    public const string NotSigned = "NotSigned";
    public const string RequiresClientSignature = "RequiresClientSignature";
    public const string OnApproval = "OnApproval";
    public const string Approved = "Approved";
    public const string ApprovalRejected = "ApprovalRejected";
    public const string NeedsCorrection = "NeedsCorrection";
    public const string OnSigning = "OnSigning";
    public const string SignedByClient = "SignedByClient";
    public const string Signed = "Signed";
    public const string Concluded = "Concluded";
    public const string Terminated = "Terminated";
    public const string Cancelled = "Cancelled";

    public const string Paid = "Paid";
    public const string PartiallyPaid = "PartiallyPaid";
    public const string Overdue = "Overdue";

    public static readonly IReadOnlyList<DocumentStatusSeed> Defaults =
    [
        new(Draft, "Черновик", "Документ готовится и еще не отправлен дальше", "#64748B", true, true, true, 10),
        new(NotSigned, "Не подписан", "Документ создан, но подпись еще не получена", "#64748B", true, true, true, 20),
        new(RequiresClientSignature, "Требуется подпись клиента", "Документ ожидает подпись клиента", "#F59E0B", true, true, true, 30),
        new(OnApproval, "На согласовании", "Документ проходит внутреннее согласование", "#06B6D4", true, true, true, 40),
        new(Approved, "Согласован", "Документ согласован", "#2563EB", true, true, true, 50),
        new(ApprovalRejected, "Отказ в согласовании", "По документу отказано в согласовании", "#E11D48", true, true, true, 60),
        new(NeedsCorrection, "Требует исправления", "Документ нужно исправить", "#F97316", true, true, true, 70),
        new(OnSigning, "На подписании", "Документ отправлен на подписание", "#8B5CF6", true, true, true, 80),
        new(SignedByClient, "Подписан клиентом", "Счет подписан клиентом", "#10B981", true, false, false, 90),
        new(Signed, "Подписан", "Акт подписан", "#10B981", false, true, false, 100),
        new(Concluded, "Подписан", "Договор подписан и действует", "#10B981", false, false, true, 110),
        new(Terminated, "Расторгнут", "Договор расторгнут", "#DC2626", false, false, true, 120),
        new(Cancelled, "Аннулирован", "Документ аннулирован", "#EF4444", true, true, true, 130),
    ];

    public static readonly IReadOnlySet<string> EditableInvoiceCodes = BuildSet(
        Draft,
        NotSigned,
        RequiresClientSignature,
        OnApproval,
        Approved,
        ApprovalRejected,
        NeedsCorrection,
        OnSigning,
        SignedByClient,
        Cancelled);

    public static readonly IReadOnlySet<string> InvoiceCodes = BuildSet(
        EditableInvoiceCodes.Concat([Paid, PartiallyPaid, Overdue]).ToArray());

    public static readonly IReadOnlySet<string> ActCodes = BuildSet(
        Draft,
        NotSigned,
        RequiresClientSignature,
        OnApproval,
        Approved,
        ApprovalRejected,
        NeedsCorrection,
        OnSigning,
        Signed,
        Cancelled);

    public static readonly IReadOnlySet<string> ContractCodes = BuildSet(
        Draft,
        NotSigned,
        RequiresClientSignature,
        OnApproval,
        Approved,
        ApprovalRejected,
        NeedsCorrection,
        OnSigning,
        Concluded,
        Terminated,
        Cancelled);

    public static readonly IReadOnlySet<string> AllCodes = BuildSet(
        InvoiceCodes.Concat(ActCodes).Concat(ContractCodes).ToArray());

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return AllCodes.FirstOrDefault(status => string.Equals(status, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    private static HashSet<string> BuildSet(params string[] values)
        => new(values, StringComparer.OrdinalIgnoreCase);
}
