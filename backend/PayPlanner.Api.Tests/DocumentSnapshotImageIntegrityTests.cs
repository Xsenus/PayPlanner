using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Companies;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class ActContractImagePrintTests
{
    private static IDocumentSeller[] ImageDocuments(Fixture fixture) => [fixture.Act.Invoice!, fixture.Act, fixture.Contract];

    private static async Task<Fixture> FrozenImagesFixtureAsync()
    {
        var fixture = await DefaultsFixtureAsync();
        var seller = new DocumentSellerService(fixture.Db);
        foreach (var document in ImageDocuments(fixture))
            await seller.ResolveAsync(document, fixture.Company, true, default);
        await fixture.Db.SaveChangesAsync();
        return fixture;
    }

    private static async Task CorruptImageReferencesAsync(Fixture fixture, string corruption)
    {
        var foreign = new LegalEntity { ShortName = "Foreign image owner" };
        fixture.Db.LegalEntities.Add(foreign);
        await fixture.Db.SaveChangesAsync();
        var image = new CompanyBrandImageVersion { LegalEntityId = foreign.Id, Kind = CompanyBrandImageKinds.Logo, Sha256 = new string('a', 64), DataUrl = "data:image/png;base64,Rk9SRUlHTg==", CreatedAt = DateTime.UtcNow };
        fixture.Db.CompanyBrandImageVersions.Add(image);
        await fixture.Db.SaveChangesAsync();
        foreach (var document in ImageDocuments(fixture))
        {
            var json = JsonNode.Parse(document.SellerSnapshotJson!)!.AsObject();
            switch (corruption)
            {
                case "missing": json["LogoVersionId"] = int.MaxValue; break;
                case "foreign": json["LogoVersionId"] = image.Id; break;
                case "kind": json["LogoVersionId"] = json["SealVersionId"]!.GetValue<int>(); break;
                case "owner-zero": json["Id"] = 0; break;
                case "owner-missing": json.Remove("Id"); break;
                case "version-zero": json["LogoVersionId"] = 0; break;
                case "version-negative": json["LogoVersionId"] = -1; break;
                default: throw new ArgumentOutOfRangeException(nameof(corruption));
            }
            document.SellerSnapshotJson = json.ToJsonString();
        }
        await fixture.Db.SaveChangesAsync();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("foreign")]
    [InlineData("kind")]
    [InlineData("owner-zero")]
    [InlineData("owner-missing")]
    [InlineData("version-zero")]
    [InlineData("version-negative")]
    public async Task SnapshotImages_InvalidReferenceFailsWithoutReplacingSnapshotOrFinancialData(string corruption)
    {
        await using var fixture = await FrozenImagesFixtureAsync();
        await CorruptImageReferencesAsync(fixture, corruption);
        var snapshots = ImageDocuments(fixture).Select(row => row.SellerSnapshotJson).ToArray();
        var versionCount = await fixture.Db.CompanyBrandImageVersions.CountAsync();
        foreach (var document in ImageDocuments(fixture))
            await Assert.ThrowsAsync<SellerSelectionException>(() => new DocumentSellerService(fixture.Db).ResolveAsync(document, fixture.Company, false, default));
        Assert.Equal(snapshots, ImageDocuments(fixture).Select(row => row.SellerSnapshotJson).ToArray());
        Assert.Equal(versionCount, await fixture.Db.CompanyBrandImageVersions.CountAsync());
        Assert.Equal(100m, fixture.Act.Invoice!.Amount); Assert.Equal(100m, fixture.Act.Amount); Assert.Equal(100m, fixture.Contract.Amount);
        Assert.Empty(await fixture.Db.MoneyOperations.ToArrayAsync()); Assert.Empty(await fixture.Db.TaskJournalItems.ToArrayAsync());
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("kind")]
    public async Task SnapshotImages_HttpRefusesInvalidReferencesBeforeFileRendering(string corruption)
    {
        await using var fixture = await FrozenImagesFixtureAsync();
        await CorruptImageReferencesAsync(fixture, corruption);
        var pdf = new PdfRecorder();
        using var server = Server(fixture, pdf); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Print-User", fixture.User.Id.ToString());
        foreach (var (section, id) in DefaultsDocuments(fixture))
            foreach (var format in new[] { "html", "word", "pdf" })
            {
                using var response = await client.GetAsync($"/api/{section}/{id}/document?format={format}");
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                var text = await response.Content.ReadAsStringAsync();
                Assert.DoesNotContain("Foreign image owner", text); Assert.DoesNotContain("Rk9SRUlHTg", text);
            }
        Assert.Empty(pdf.Html);
    }

    [Fact]
    public async Task SnapshotImages_ValidFrozenOwnerSurvivesProfileDefaultAndSelectionChanges()
    {
        await using var fixture = await FrozenImagesFixtureAsync();
        var snapshots = ImageDocuments(fixture).Select(row => row.SellerSnapshotJson).ToArray();
        var foreign = new LegalEntity { ShortName = "New live company", LogoDataUrl = "new live logo" };
        fixture.Db.LegalEntities.Add(foreign);
        fixture.Company.ProfileStatus = CompanyProfileStatuses.Archived;
        fixture.Company.LogoDataUrl = fixture.Company.SealDataUrl = fixture.Company.DirectorSignatureDataUrl = null;
        await fixture.Db.SaveChangesAsync();
        foreach (var document in ImageDocuments(fixture))
        {
            document.SellerCompanyId = foreign.Id;
            var seller = await new DocumentSellerService(fixture.Db).ResolveAsync(document, foreign, false, default);
            Assert.NotNull(seller); Assert.Equal(fixture.Company.Id, seller.Id);
            Assert.Equal(Image, seller.LogoDataUrl); Assert.Equal(Image, seller.SealDataUrl); Assert.Equal(Image, seller.DirectorSignatureDataUrl);
        }
        Assert.Equal(snapshots, ImageDocuments(fixture).Select(row => row.SellerSnapshotJson).ToArray());
    }

    [Fact]
    public async Task SnapshotImages_ValidFrozenAbsenceNeverAcquiresNewLiveImages()
    {
        await using var fixture = await DefaultsFixtureAsync();
        fixture.Company.LogoDataUrl = fixture.Company.SealDataUrl = fixture.Company.DirectorSignatureDataUrl = null;
        await fixture.Db.SaveChangesAsync();
        var service = new DocumentSellerService(fixture.Db);
        foreach (var document in ImageDocuments(fixture)) await service.ResolveAsync(document, fixture.Company, true, default);
        fixture.Company.LogoDataUrl = fixture.Company.SealDataUrl = fixture.Company.DirectorSignatureDataUrl = Image;
        await fixture.Db.SaveChangesAsync();
        foreach (var document in ImageDocuments(fixture))
        {
            var seller = await service.ResolveAsync(document, fixture.Company, false, default);
            Assert.NotNull(seller); Assert.Null(seller.LogoDataUrl); Assert.Null(seller.SealDataUrl); Assert.Null(seller.DirectorSignatureDataUrl);
        }
        Assert.Empty(await fixture.Db.CompanyBrandImageVersions.ToArrayAsync());
    }

    [Fact]
    public async Task SnapshotImages_LegacySnapshotKeepsExistingLiveImageBehavior()
    {
        await using var fixture = await DefaultsFixtureAsync();
        var legacy = SellerLegalEntityComposer.ToSnapshotJson(fixture.Company);
        foreach (var document in ImageDocuments(fixture))
        {
            document.SellerCompanyId = fixture.Company.Id; document.SellerSnapshotJson = legacy;
            var seller = await new DocumentSellerService(fixture.Db).ResolveAsync(document, fixture.Company, false, default);
            Assert.NotNull(seller); Assert.Equal(Image, seller.LogoDataUrl); Assert.Equal(legacy, document.SellerSnapshotJson);
        }
        Assert.Empty(await fixture.Db.CompanyBrandImageVersions.ToArrayAsync());
    }
}
