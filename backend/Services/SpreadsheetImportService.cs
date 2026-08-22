using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ExcelDataReader;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Invoices;

namespace PayPlanner.Api.Services;

public sealed record SpreadsheetImportUploadFile(string FileName, byte[] Content);

public interface ISpreadsheetImportService
{
    Task<SpreadsheetImportPreviewDto> PreviewAsync(
        IReadOnlyCollection<SpreadsheetImportUploadFile> files,
        CancellationToken cancellationToken = default);

    Task<SpreadsheetImportBatchDetailDto> ApplyAsync(
        IReadOnlyCollection<SpreadsheetImportUploadFile> files,
        SpreadsheetImportApplyRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<SpreadsheetImportBatchSummaryDto>> GetBatchesAsync(CancellationToken cancellationToken = default);
    Task<SpreadsheetImportBatchDetailDto?> GetBatchAsync(int batchId, CancellationToken cancellationToken = default);
    Task<SpreadsheetImportBatchDetailDto?> GetBatchAsync(int batchId, int page, int pageSize, CancellationToken cancellationToken = default);
    Task<SpreadsheetImportBatchDetailDto> RollbackAsync(int batchId, CancellationToken cancellationToken = default);
}

public sealed partial class SpreadsheetImportService : ISpreadsheetImportService
{
    private const string ClientProfileNotesPrefix = "__PP_CLIENT_PROFILE_V2__:";
    private const string CounterpartyImportModeClientOnly = "ClientOnly";
    private const string CounterpartyImportModeLegalEntityWithClient = "LegalEntityWithClient";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private static readonly Regex LineItemRegex = new(
        @"^(?<name>.+?)\s+(?<quantity>\d+(?:[.,]\d+)?)\s+(?<unit>[^=]+?)\s+x\s+(?<price>[\d\s]+(?:[.,]\d+)?)\s*р?\.?\s*=\s*(?<amount>[\d\s]+(?:[.,]\d+)?)\s*р?\.?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] OrganizationMarkers =
    [
        "ооо",
        "ип",
        "ао",
        "пао",
        "оао",
        "зао",
        "мку",
        "уфпс",
        "инспекция",
        "налоговой",
        "банк",
        "администрация",
        "фнс",
    ];

    private readonly IDbContextFactory<PaymentContext> _contextFactory;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<SpreadsheetImportService> _logger;

    static SpreadsheetImportService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public SpreadsheetImportService(
        IDbContextFactory<PaymentContext> contextFactory,
        IHttpContextAccessor httpContextAccessor,
        ILogger<SpreadsheetImportService> logger)
    {
        _contextFactory = contextFactory;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public Task<SpreadsheetImportPreviewDto> PreviewAsync(
        IReadOnlyCollection<SpreadsheetImportUploadFile> files,
        CancellationToken cancellationToken = default)
        => PreviewCoreAsync(files, cancellationToken);

    public Task<SpreadsheetImportBatchDetailDto> ApplyAsync(
        IReadOnlyCollection<SpreadsheetImportUploadFile> files,
        SpreadsheetImportApplyRequest request,
        CancellationToken cancellationToken = default)
        => ApplyCoreAsync(files, request, cancellationToken);

    public Task<IReadOnlyCollection<SpreadsheetImportBatchSummaryDto>> GetBatchesAsync(CancellationToken cancellationToken = default)
        => GetBatchesCoreAsync(cancellationToken);

    public Task<SpreadsheetImportBatchDetailDto?> GetBatchAsync(int batchId, CancellationToken cancellationToken = default)
        => GetBatchCoreAsync(batchId, 1, 25, cancellationToken);

    public Task<SpreadsheetImportBatchDetailDto?> GetBatchAsync(
        int batchId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
        => GetBatchCoreAsync(batchId, page, pageSize, cancellationToken);

    public Task<SpreadsheetImportBatchDetailDto> RollbackAsync(int batchId, CancellationToken cancellationToken = default)
        => RollbackCoreAsync(batchId, cancellationToken);

    private sealed class ParsedImportBundle
    {
        public List<ParsedFile> Files { get; } = new();
        public List<CounterpartyDraft> CounterpartyDrafts { get; } = new();
        public List<InvoiceDraft> InvoiceDrafts { get; } = new();
        public List<ActDraft> ActDrafts { get; } = new();
        public List<string> Messages { get; } = new();
        public Dictionary<string, int> OneCNonImportObjectCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ParsedFile
    {
        public string FileName { get; init; } = string.Empty;
        public string FileHash { get; init; } = string.Empty;
        public string DetectedKind { get; init; } = string.Empty;
        public string? SheetName { get; init; }
        public int RowCount { get; init; }
    }

    private sealed class ImportState
    {
        public List<UserStateItem> Users { get; init; } = new();
        public List<ClientStateItem> Clients { get; init; } = new();
        public List<LegalEntityStateItem> LegalEntities { get; init; } = new();
        public Dictionary<string, List<InvoiceStateItem>> Invoices { get; init; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<ActStateItem>> Acts { get; init; } = new(StringComparer.Ordinal);

        public void UpsertClient(ClientStateItem item)
        {
            var existing = Clients.FindIndex(client => client.Id == item.Id);
            if (existing >= 0)
            {
                Clients[existing] = item;
            }
            else
            {
                Clients.Add(item);
            }
        }

        public void UpsertInvoice(InvoiceStateItem item)
        {
            foreach (var list in Invoices.Values)
            {
                list.RemoveAll(current => current.PaymentId == item.PaymentId);
            }

            var keys = BuildInvoiceMatchKeys(item.ExternalId, item.Number, item.Date, item.Type);
            if (keys.Count == 0 && !string.IsNullOrWhiteSpace(item.Key))
            {
                keys.Add(item.Key);
            }

            foreach (var key in keys)
            {
                if (!Invoices.TryGetValue(key, out var list))
                {
                    list = new List<InvoiceStateItem>();
                    Invoices[key] = list;
                }

                list.Add(new InvoiceStateItem
                {
                    PaymentId = item.PaymentId,
                    Key = key,
                    ExternalId = item.ExternalId,
                    Number = item.Number,
                    Date = item.Date,
                    Amount = item.Amount,
                    DueDate = item.DueDate,
                    CounterpartyName = item.CounterpartyName,
                    Status = item.Status,
                    Type = item.Type,
                    PaidDate = item.PaidDate,
                    ActReference = item.ActReference,
                    ResponsibleName = item.ResponsibleName,
                    ImportedInCurrentBatch = item.ImportedInCurrentBatch,
                });
            }
        }

        public void UpsertLegalEntity(LegalEntityStateItem item)
        {
            var existing = LegalEntities.FindIndex(entity => entity.Id == item.Id);
            if (existing >= 0)
            {
                LegalEntities[existing] = item;
            }
            else
            {
                LegalEntities.Add(item);
            }
        }

        public void UpsertAct(ActStateItem item)
        {
            foreach (var list in Acts.Values)
            {
                list.RemoveAll(current => current.ActId == item.ActId);
            }

            var keys = BuildActMatchKeys(item.ExternalId, item.Number, item.Date, item.Type);
            if (keys.Count == 0 && !string.IsNullOrWhiteSpace(item.Key))
            {
                keys.Add(item.Key);
            }

            foreach (var key in keys)
            {
                if (!Acts.TryGetValue(key, out var list))
                {
                    list = new List<ActStateItem>();
                    Acts[key] = list;
                }

                list.Add(new ActStateItem
                {
                    ActId = item.ActId,
                    Key = key,
                    ExternalId = item.ExternalId,
                    Number = item.Number,
                    Date = item.Date,
                    Amount = item.Amount,
                    CounterpartyName = item.CounterpartyName,
                    CounterpartyInn = item.CounterpartyInn,
                    Status = item.Status,
                    Type = item.Type,
                    InvoiceNumber = item.InvoiceNumber,
                    ResponsibleName = item.ResponsibleName,
                    ImportedInCurrentBatch = item.ImportedInCurrentBatch,
                });
            }
        }
    }

    private sealed class UserStateItem
    {
        public int Id { get; init; }
        public string FullName { get; init; } = string.Empty;
        public List<string> MatchKeys { get; init; } = new();
    }

    private sealed class ClientStateItem
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string NormalizedName { get; init; } = string.Empty;
        public string? Company { get; init; }
        public string? NormalizedCompany { get; init; }
        public string? NormalizedInn { get; init; }
        public string? NormalizedPhone { get; init; }
        public string? NormalizedEmail { get; init; }
        public string? Phone { get; init; }
        public string? Email { get; init; }
        public string? Address { get; init; }
        public string? ResponsibleName { get; init; }
        public int? LegalEntityId { get; init; }
        public string? LegalEntityShortName { get; init; }
        public string? LegalEntityFullName { get; init; }
        public List<string> MatchKeys { get; init; } = new();
    }

    private sealed class LegalEntityStateItem
    {
        public int Id { get; init; }
        public string ShortName { get; init; } = string.Empty;
        public string? FullName { get; init; }
        public string? Inn { get; init; }
        public string? Kpp { get; init; }
        public string? Ogrn { get; init; }
        public string? Address { get; init; }
        public string? Phone { get; init; }
        public string? Email { get; init; }
        public string? Director { get; init; }
        public string? ResponsibleName { get; init; }
        public string? OrganizationForm { get; init; }
        public string? BankAccountsJson { get; init; }
        public string NormalizedShortName { get; init; } = string.Empty;
        public string? NormalizedFullName { get; init; }
        public string? NormalizedInn { get; init; }
        public string? NormalizedPhone { get; init; }
        public string? NormalizedEmail { get; init; }
        public List<string> MatchKeys { get; init; } = new();
    }

    private sealed class InvoiceStateItem
    {
        public int PaymentId { get; init; }
        public string Key { get; init; } = string.Empty;
        public string? ExternalId { get; init; }
        public string? Number { get; init; }
        public DateTime? Date { get; init; }
        public decimal Amount { get; init; }
        public DateTime? DueDate { get; init; }
        public string? CounterpartyName { get; init; }
        public PaymentStatus Status { get; init; }
        public PaymentType Type { get; init; }
        public DateTime? PaidDate { get; init; }
        public string? ActReference { get; init; }
        public string? ResponsibleName { get; init; }
        public bool ImportedInCurrentBatch { get; init; }
    }

    private sealed class ActStateItem
    {
        public int ActId { get; init; }
        public string Key { get; init; } = string.Empty;
        public string? ExternalId { get; init; }
        public string? Number { get; init; }
        public DateTime? Date { get; init; }
        public decimal Amount { get; init; }
        public string? CounterpartyName { get; init; }
        public string? CounterpartyInn { get; init; }
        public ActStatus Status { get; init; }
        public PaymentType Type { get; init; }
        public string? InvoiceNumber { get; init; }
        public string? ResponsibleName { get; init; }
        public bool ImportedInCurrentBatch { get; init; }
    }

    private record CounterpartyDraft
    {
        public string PreviewId { get; init; } = string.Empty;
        public string FileName { get; init; } = string.Empty;
        public string FileHash { get; init; } = string.Empty;
        public string? SheetName { get; init; }
        public int RowNumber { get; init; }
        public string Name { get; init; } = string.Empty;
        public string? Inn { get; init; }
        public string? ContactName { get; init; }
        public string? Phone { get; init; }
        public string? Email { get; init; }
        public string? ContractName { get; init; }
        public int? DelayDays { get; init; }
        public string? Article { get; init; }
        public string? ResponsibleName { get; init; }
        public string? AutoInvoiceResponsibleName { get; init; }
        public string? ExternalId { get; init; }
        public string? Kpp { get; init; }
        public string? Ogrn { get; init; }
        public string? Address { get; init; }
        public string? OrganizationForm { get; init; }
        public string? OrganizationFormFull { get; init; }
        public string? BankName { get; init; }
        public string? BankBik { get; init; }
        public string? BankAccount { get; init; }
        public string? BankCorrAccount { get; init; }
        public bool Derived { get; init; }
        public List<string> DependencyPreviewIds { get; init; } = new();
        public List<string> DependencyTitles { get; init; } = new();
    }

    private sealed class InvoiceDraft
    {
        public string PreviewId { get; init; } = string.Empty;
        public string FileName { get; init; } = string.Empty;
        public string FileHash { get; init; } = string.Empty;
        public string? SheetName { get; init; }
        public int RowNumber { get; init; }
        public string? Number { get; init; }
        public DateTime? Date { get; init; }
        public DateTime? DueDate { get; init; }
        public decimal? Amount { get; init; }
        public PaymentType Type { get; init; } = PaymentType.Income;
        public string? CounterpartyName { get; init; }
        public string? CounterpartyInn { get; init; }
        public string? StatusText { get; init; }
        public DateTime? PaidDate { get; init; }
        public string? ActReference { get; init; }
        public string? Nomenclature { get; init; }
        public string? ContractName { get; init; }
        public string? Comment { get; init; }
        public string? ExternalId { get; init; }
        public string? CreatedByName { get; init; }
        public string? ResponsibleName { get; init; }
        public List<ImportedLineItem> ParsedLineItems { get; init; } = new();
        public List<string> ParseWarnings { get; init; } = new();
    }

    private sealed class ActDraft
    {
        public string PreviewId { get; init; } = string.Empty;
        public string FileName { get; init; } = string.Empty;
        public string FileHash { get; init; } = string.Empty;
        public string? SheetName { get; init; }
        public int RowNumber { get; init; }
        public string? Number { get; init; }
        public DateTime? Date { get; init; }
        public decimal? Amount { get; init; }
        public PaymentType Type { get; init; } = PaymentType.Income;
        public string? CounterpartyName { get; init; }
        public string? CounterpartyInn { get; init; }
        public string? StatusText { get; init; }
        public string? InvoiceNumber { get; set; }
        public string? InvoiceExternalId { get; init; }
        public DateTime? InvoiceDate { get; set; }
        public DateTime? PaymentDate { get; init; }
        public string? Nomenclature { get; init; }
        public string? ContractName { get; init; }
        public string? Comment { get; init; }
        public string? ExternalId { get; init; }
        public string? ResponsibleName { get; init; }
        public List<ImportedLineItem> ParsedLineItems { get; init; } = new();
        public List<string> ParseWarnings { get; init; } = new();
    }

    private sealed record SheetData(string Name, List<object?[]> Rows);
    private sealed record SheetRow(int RowNumber, object?[] Values);
    private sealed record ImportedLineItem(string Name, decimal Quantity, string Unit, decimal Price);
    private sealed record LineItemParseResult(List<ImportedLineItem> Items, List<string> Warnings);

    private sealed class ClientSnapshot
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Email { get; init; } = string.Empty;
        public string Phone { get; init; } = string.Empty;
        public string Company { get; init; } = string.Empty;
        public string Address { get; init; } = string.Empty;
        public string Notes { get; init; } = string.Empty;
        public bool IsActive { get; init; }
        public int? LegalEntityId { get; init; }
        public int? ResponsibleUserId { get; init; }
        public int? ClientStatusId { get; init; }
    }

    private sealed class LegalEntitySnapshot
    {
        public int Id { get; init; }
        public string ShortName { get; init; } = string.Empty;
        public string? FullName { get; init; }
        public string? Inn { get; init; }
        public string? Kpp { get; init; }
        public string? Ogrn { get; init; }
        public string? Address { get; init; }
        public string? Phone { get; init; }
        public string? Email { get; init; }
        public string? Director { get; init; }
        public int? ResponsibleUserId { get; init; }
        public string? OrganizationForm { get; init; }
        public string? BankAccountsJson { get; init; }
        public string? Notes { get; init; }
    }

    private sealed class CounterpartySnapshot
    {
        public ClientSnapshot? Client { get; init; }
        public LegalEntitySnapshot? LegalEntity { get; init; }
    }

    private sealed class CounterpartyOperationMetadata
    {
        public bool Derived { get; init; }
        public string ImportMode { get; init; } = CounterpartyImportModeClientOnly;
        public int? LegalEntityId { get; init; }
        public bool LegalEntityCreated { get; init; }
    }

    private sealed class InvoiceSnapshot
    {
        public PaymentSnapshot Payment { get; init; } = new();
        public InvoiceEntitySnapshot? InvoiceEntity { get; init; }
        public List<InvoiceLineItemSnapshot> LineItems { get; init; } = new();
        public List<int> ResponsibleUserIds { get; init; } = new();
    }

    private sealed class PaymentSnapshot
    {
        public int Id { get; init; }
        public int? InvoiceId { get; init; }
        public string? Account { get; init; }
        public DateTime? AccountDate { get; init; }
        public decimal Amount { get; init; }
        public DateTime Date { get; init; }
        public DateTime PlannedDate { get; init; }
        public string Description { get; init; } = string.Empty;
        public PaymentStatus Status { get; init; }
        public PaymentType Type { get; init; }
        public bool IsPaid { get; init; }
        public decimal PaidAmount { get; init; }
        public DateTime? PaidDate { get; init; }
        public DateTime? LastPaymentDate { get; init; }
        public string Notes { get; init; } = string.Empty;
        public string SystemNotes { get; init; } = string.Empty;
        public int? ClientId { get; init; }
        public int? ExpenseClientId { get; init; }
        public int? ContractId { get; init; }
        public DocumentKind DocumentKind { get; init; }
        public int? PaymentSourceId { get; init; }
        public int? ServiceId { get; init; }
        public int? IncomeTypeId { get; init; }
        public int? DealTypeId { get; init; }
        public int? PaymentStatusId { get; init; }
        public bool IsNotForAccounting { get; init; }
    }

    private sealed class InvoiceEntitySnapshot
    {
        public int Id { get; init; }
        public string Number { get; init; } = string.Empty;
        public DateTime Date { get; init; }
    }

    private sealed class InvoiceLineItemSnapshot
    {
        public int Id { get; init; }
        public int SortOrder { get; init; }
        public string Name { get; init; } = string.Empty;
        public decimal Quantity { get; init; }
        public string Unit { get; init; } = string.Empty;
        public decimal Price { get; init; }
    }

    private sealed class ActSnapshot
    {
        public ActEntitySnapshot Act { get; init; } = new();
        public List<ActLineItemSnapshot> LineItems { get; init; } = new();
    }

    private sealed class ActEntitySnapshot
    {
        public int Id { get; init; }
        public string Number { get; init; } = string.Empty;
        public string? Title { get; init; }
        public DateTime Date { get; init; }
        public decimal Amount { get; init; }
        public string CreationMode { get; init; } = ActCreationMode.Manual;
        public string? InvoiceNumber { get; init; }
        public int? InvoiceId { get; init; }
        public string? CounterpartyInn { get; init; }
        public ActStatus Status { get; init; }
        public int? ClientId { get; init; }
        public int? ResponsibleId { get; init; }
        public string? Comment { get; init; }
    }

    private sealed class ActLineItemSnapshot
    {
        public int Id { get; init; }
        public int SortOrder { get; init; }
        public string SourceKey { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public decimal Quantity { get; init; }
        public string Unit { get; init; } = string.Empty;
        public decimal Price { get; init; }
    }
}
