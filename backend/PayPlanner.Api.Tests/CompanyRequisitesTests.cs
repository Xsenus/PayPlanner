using System.Text.Json.Nodes;
using PayPlanner.Api.Services.Companies;
using Xunit;

namespace PayPlanner.Api.Tests;

public class RussianRequisitesValidatorTests
{
    [Theory]
    [InlineData("7707083893", true)]
    [InlineData("500100732259", true)]
    [InlineData("7707083894", false)]
    [InlineData("500100732250", false)]
    [InlineData("770708389", false)]
    [InlineData("77070838a3", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidInn_ChecksLengthAndChecksum(string? value, bool expected)
        => Assert.Equal(expected, RussianRequisitesValidator.IsValidInn(value));

    [Theory]
    [InlineData("773601001", true)]
    [InlineData("7736AB001", true)]
    [InlineData("77AB01001", false)]
    [InlineData("77360100", false)]
    [InlineData("77360100A", false)]
    public void IsValidKpp_AllowsLettersOnlyInFifthAndSixthPlace(string value, bool expected)
        => Assert.Equal(expected, RussianRequisitesValidator.IsValidKpp(value));

    [Theory]
    [InlineData("1027700132195", true)]
    [InlineData("1027700132196", false)]
    [InlineData("304500116000157", true)]
    [InlineData("304500116000158", false)]
    [InlineData("10277001321", false)]
    public void IsValidOgrn_ChecksControlDigitForOgrnAndOgrnip(string value, bool expected)
        => Assert.Equal(expected, RussianRequisitesValidator.IsValidOgrn(value));

    [Theory]
    [InlineData("044525225", true)]
    [InlineData("144525225", false)]
    [InlineData("04452522", false)]
    public void IsValidBik_RequiresNineDigitsStartingWith04(string value, bool expected)
        => Assert.Equal(expected, RussianRequisitesValidator.IsValidBik(value));

    [Fact]
    public void AccountChecks_UseBikToComputeControlDigit()
    {
        Assert.True(RussianRequisitesValidator.IsValidCorrespondentAccount("30101810400000000225", "044525225"));
        Assert.False(RussianRequisitesValidator.IsValidCorrespondentAccount("30101810400000000226", "044525225"));
        Assert.True(RussianRequisitesValidator.IsValidSettlementAccount("40702810200000000001", "044525225"));
        Assert.False(RussianRequisitesValidator.IsValidSettlementAccount("40702810200000000001", "044525104"));
        Assert.False(RussianRequisitesValidator.IsValidSettlementAccount("4070281020000000000", "044525225"));
    }
}

public class CompanyBankAccountListTests
{
    private static JsonArray Parse(string? json) => (JsonArray)JsonNode.Parse(json!)!;

    [Fact]
    public void Normalize_ReturnsNull_ForBlankOrEmptyList()
    {
        Assert.Null(CompanyBankAccountList.Normalize(null).Json);
        Assert.Null(CompanyBankAccountList.Normalize("  ").Json);
        Assert.Null(CompanyBankAccountList.Normalize("[]").Json);
    }

    [Fact]
    public void Normalize_RejectsMalformedJson()
    {
        Assert.False(CompanyBankAccountList.Normalize("{not json").IsValid);
        Assert.False(CompanyBankAccountList.Normalize("{\"a\":1}").IsValid);
        Assert.False(CompanyBankAccountList.Normalize("[1]").IsValid);
    }

    [Fact]
    public void Normalize_AssignsStableIds_AndKeepsExistingOnes()
    {
        var counter = 0;
        var result = CompanyBankAccountList.Normalize(
            "[{\"id\":\"keep\",\"accountNumber\":\"1\"},{\"accountNumber\":\"2\"},{\"id\":\"keep\",\"accountNumber\":\"3\"}]",
            () => $"new{++counter}");

        var items = Parse(result.Json);
        Assert.Equal("keep", (string?)items[0]!["id"]);
        Assert.Equal("new1", (string?)items[1]!["id"]);
        Assert.Equal("new2", (string?)items[2]!["id"]);
    }

    [Fact]
    public void Normalize_LeavesExactlyOneActivePrimary_AndKeepsUnknownFields()
    {
        var result = CompanyBankAccountList.Normalize(
            "[{\"accountNumber\":\"1\",\"isPrimary\":true,\"custom\":\"x\"},{\"accountNumber\":\"2\",\"isPrimary\":true},{\"accountNumber\":\"3\"}]");

        var items = Parse(result.Json);
        Assert.Equal([true, false, false], items.Select(i => (bool)i!["isPrimary"]!).ToArray());
        Assert.Equal("x", (string?)items[0]!["custom"]);
    }

    [Fact]
    public void Normalize_NeverMakesArchivedAccountPrimary()
    {
        var result = CompanyBankAccountList.Normalize(
            "[{\"accountNumber\":\"1\",\"isPrimary\":true,\"isArchived\":true},{\"accountNumber\":\"2\"}]");

        var items = Parse(result.Json);
        Assert.False((bool)items[0]!["isPrimary"]!);
        Assert.True((bool)items[1]!["isPrimary"]!);
    }

    [Fact]
    public void Normalize_UsesLegacyAccountType_WhenNoExplicitPrimary()
    {
        var result = CompanyBankAccountList.Normalize(
            "[{\"accountNumber\":\"1\",\"accountType\":\"расчетный\"},{\"accountNumber\":\"2\",\"accountType\":\"Основной\"}]");

        var items = Parse(result.Json);
        Assert.False((bool)items[0]!["isPrimary"]!);
        Assert.True((bool)items[1]!["isPrimary"]!);
    }

    [Fact]
    public void Normalize_RejectsDuplicateActiveAccount_ButAllowsArchivedDuplicate()
    {
        var duplicate = CompanyBankAccountList.Normalize(
            "[{\"bik\":\"044525225\",\"accountNumber\":\"1\"},{\"bik\":\"044525225\",\"accountNumber\":\"1\"}]");
        Assert.False(duplicate.IsValid);

        var archived = CompanyBankAccountList.Normalize(
            "[{\"bik\":\"044525225\",\"accountNumber\":\"1\"},{\"bik\":\"044525225\",\"accountNumber\":\"1\",\"isArchived\":true}]");
        Assert.True(archived.IsValid);
    }

    [Fact]
    public void Normalize_ReportsFormatProblemsAsWarningsOnly()
    {
        var result = CompanyBankAccountList.Normalize("[{\"bik\":\"123\",\"accountNumber\":\"12\"}]");

        Assert.True(result.IsValid);
        Assert.Equal(2, result.Warnings.Count);
    }

    [Fact]
    public void FindPrimaryActive_SkipsArchived_AndFallsBackForLegacyData()
    {
        var primary = CompanyBankAccountList.FindPrimaryActive(
            "[{\"accountNumber\":\"1\",\"isArchived\":true,\"isPrimary\":true},{\"accountNumber\":\"2\"}]");
        Assert.Equal("2", (string?)primary!["accountNumber"]);

        Assert.Null(CompanyBankAccountList.FindPrimaryActive("[{\"name\":\"без номера\"}]"));
        Assert.Null(CompanyBankAccountList.FindPrimaryActive(null));
    }
}

public class InlineImageDataUrlValidatorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("data:image/png;base64,QUFBQUFB")]
    [InlineData("data:image/jpeg;base64,QUFBQUFB")]
    [InlineData("DATA:IMAGE/WEBP;BASE64,QUFBQUFB")]
    public void Validate_AcceptsEmptyValuesAndRasterImages(string? value)
        => Assert.Null(InlineImageDataUrlValidator.Validate(value));

    [Theory]
    [InlineData("QUFBQUFB")]
    [InlineData("data:image/svg+xml;base64,QUFBQUFB")]
    [InlineData("data:image/gif;base64,QUFBQUFB")]
    [InlineData("data:text/html;base64,QUFBQUFB")]
    [InlineData("data:image/png,QUFBQUFB")]
    [InlineData("data:image/png;base64,")]
    [InlineData("data:image/png;base64,***not-base64***")]
    public void Validate_RejectsOtherTypesAndBrokenContent(string value)
        => Assert.NotNull(InlineImageDataUrlValidator.Validate(value));
}
