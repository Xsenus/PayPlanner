using System.Globalization;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Companies;
using Xunit;

namespace PayPlanner.Api.Tests;

public class CompanyProfileEditVersionTests
{
    private static readonly string Defaults = new('A', 64);
    private static LegalEntity Company() => new() { Id = 7, ShortName = "Company", Phone = "000", CreatedAt = new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc) };
    private static string Version(LegalEntity value, AddressRecord[]? addresses = null, int[]? clients = null, string? defaults = null)
        => CompanyProfileEditVersion.Compute(value, addresses ?? [], clients ?? [], defaults ?? Defaults);

    [Theory]
    [InlineData("name")]
    [InlineData("phone")]
    [InlineData("tax")]
    [InlineData("bank")]
    [InlineData("logo")]
    [InlineData("seal")]
    [InlineData("signature")]
    [InlineData("signatory")]
    [InlineData("notes")]
    [InlineData("status")]
    [InlineData("responsible")]
    [InlineData("company-id")]
    public void VersionChangesForEditableProfileState(string change)
    {
        var company = Company();
        var before = Version(company);
        switch (change)
        {
            case "name": company.FullName = "Changed"; break;
            case "phone": company.Phone = "111"; break;
            case "tax": company.TaxSystem = "Changed"; break;
            case "bank": company.BankAccountsJson = "[]"; break;
            case "logo": company.LogoDataUrl = "logo"; break;
            case "seal": company.SealDataUrl = "seal"; break;
            case "signature": company.DirectorSignatureDataUrl = "signature"; break;
            case "signatory": company.DocumentDirectorJson = "{}"; break;
            case "notes": company.Notes = "Changed"; break;
            case "status": company.ProfileStatus = CompanyProfileStatuses.Archived; break;
            case "responsible": company.ResponsibleUserId = 3; break;
            case "company-id": company.Id++; break;
        }
        Assert.NotEqual(before, Version(company));
        Assert.True(CompanyProfileEditVersion.IsValid(before));
    }

    [Fact]
    public void VersionIncludesAddressStateAndMembership()
    {
        var company = Company();
        var address = new AddressRecord { Id = 2, LegalEntityId = 7, Value = "Old address", CreatedAt = company.CreatedAt };
        var before = Version(company, [address]);
        address.Value = "New address";
        Assert.NotEqual(before, Version(company, [address]));
        Assert.NotEqual(before, Version(company));
        Assert.NotEqual(before, Version(company, [address, new AddressRecord { Id = 3, CreatedAt = company.CreatedAt }]));
    }

    [Fact]
    public void ClientLinkChangeIsDetectedWithoutChangingTheCompanyTimestamp()
    {
        var company = Company();
        var before = Version(company, clients: [1]);
        Assert.NotEqual(before, Version(company, clients: [1, 2]));
        Assert.NotEqual(before, Version(company, clients: [2]));
        Assert.Equal(Version(company, clients: [1, 2]), Version(company, clients: [2, 1, 2]));
        Assert.Null(company.UpdatedAt);
    }

    [Fact]
    public void SettingsVersionAndOrderHaveDistinctSemantics()
    {
        var company = Company();
        var first = new AddressRecord { Id = 2, Value = "First", CreatedAt = company.CreatedAt };
        var second = new AddressRecord { Id = 3, Value = "Second", CreatedAt = company.CreatedAt };
        Assert.Equal(Version(company, [first, second], [3, 1]), Version(company, [second, first], [1, 3]));
        Assert.Equal(Version(company), Version(company, defaults: Defaults.ToLowerInvariant()));
        Assert.NotEqual(Version(company), Version(company, defaults: new string('B', 64)));
    }

    [Fact]
    public void NullEmptyAndFieldBoundariesRemainDistinct()
    {
        var company = Company(); var before = Version(company);
        company.FullName = ""; Assert.NotEqual(before, Version(company));
        company.FullName = "ab"; company.Notes = "c"; var separated = Version(company);
        company.FullName = "a"; company.Notes = "bc"; Assert.NotEqual(separated, Version(company));
        company.FullName = "x\0y"; var nullByte = Version(company);
        company.FullName = "xy\0"; Assert.NotEqual(nullByte, Version(company));
        company.FullName = "\uD800"; var surrogate = Version(company);
        company.FullName = "\uD801"; Assert.NotEqual(surrogate, Version(company));
    }

    [Fact]
    public void DateKindsAndProviderMicrosecondPrecisionDoNotCreateFalseConflicts()
    {
        var company = Company(); company.UpdatedAt = company.CreatedAt.AddTicks(1234567);
        var before = Version(company);
        company.UpdatedAt = DateTime.SpecifyKind(company.UpdatedAt.Value.AddTicks(-7), DateTimeKind.Unspecified);
        Assert.Equal(before, Version(company));
        company.UpdatedAt = company.UpdatedAt.Value.AddTicks(10);
        Assert.NotEqual(before, Version(company));
    }

    [Fact]
    public void OutputDoesNotDependOnTheCurrentCulture()
    {
        var company = Company(); var before = Version(company); var culture = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA"); Assert.Equal(before, Version(company)); }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("old")]
    public void MissingOrMalformedImageDefaultsStateIsNotSilentlyIgnored(string? value)
        => Assert.Throws<ArgumentException>(() => CompanyProfileEditVersion.Compute(Company(), [], [], value!));

    [Fact]
    public void HashingLegacyImagesDoesNotAllocateAnotherImageSizedBuffer()
    {
        var company = Company(); _ = Version(company);
        var start = GC.GetAllocatedBytesForCurrentThread(); _ = Version(company); var small = GC.GetAllocatedBytesForCurrentThread() - start;
        company.LogoDataUrl = new string('A', 4 * 1024 * 1024);
        company.SealDataUrl = company.LogoDataUrl; company.DirectorSignatureDataUrl = company.LogoDataUrl;
        start = GC.GetAllocatedBytesForCurrentThread(); _ = Version(company); var large = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.True(large < small + 256 * 1024, $"Unexpected image-sized allocation: small={small}, large={large}.");
    }
}
