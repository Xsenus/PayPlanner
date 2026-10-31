namespace PayPlanner.Api.Models;

/// <summary>
/// Server-owned confirmation of one profile account and one bank source.
/// Revocation closes this record; it never changes the identity of past money or documents.
/// </summary>
public sealed class CompanyBankAccountBinding
{
    public int Id { get; set; }
    public int LegalEntityId { get; set; }
    // The complete stable ID is retained even for a legacy long ID. The bounded
    // hash is indexed; writers must compare the full ID as well, never hash alone.
    public string ProfileAccountId { get; set; } = string.Empty;
    public string ProfileAccountKey { get; set; } = string.Empty;
    public int BankConnectionAccountId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string ExternalAccountId { get; set; } = string.Empty;
    public string AccountNumber { get; set; } = string.Empty;
    public string Bik { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public string ConfirmedMatchVersion { get; set; } = string.Empty;
    public int? CreatedByUserId { get; set; }
    public string? CreatedByUserName { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public int? RevokedByUserId { get; set; }
    public string? RevokedByUserName { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public string? RevocationMatchVersion { get; set; }
}
