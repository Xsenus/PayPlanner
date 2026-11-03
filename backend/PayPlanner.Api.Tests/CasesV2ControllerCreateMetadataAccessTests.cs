using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CaseCreateMetadata_CreateOnlyRoleGetsTheSameDisclosureFlagAsTheRead(int route)
    {
        await using var db = await CreateContextAsync(); var (actor, ids) = await SeedBulkCasesAsync(db); await GrantSectionPermissionAsync(db, actor, RolePermissionCatalog.Cases, canEdit: false); var owner = await db.Clients.SingleAsync(); owner.Notes = "Закрытая заметка владельца"; await db.SaveChangesAsync();
        using var server = CaseWriteServer(db, actor); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString()); using var response = await client.PostAsJsonAsync(CreationRoute(route), new ClientCase { ClientId = owner.Id, Title = "Разрешено создать, запрещено раскрывать", CanRevealSensitiveData = true }); Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } }; var created = await response.Content.ReadFromJsonAsync<ClientCase>(options); Assert.NotNull(created); using var read = await client.GetAsync($"{CreationRoute(route)}/{created.Id}"); Assert.Equal(HttpStatusCode.OK, read.StatusCode); var details = await read.Content.ReadFromJsonAsync<ClientCase>(options); Assert.NotNull(details);
        Assert.True(!created.CanRevealSensitiveData && !details.CanRevealSensitiveData, $"createCanReveal={created.CanRevealSensitiveData}; readCanReveal={details.CanRevealSensitiveData}"); Assert.DoesNotContain(owner.Notes, await response.Content.ReadAsStringAsync()); Assert.DoesNotContain(owner.Notes, await read.Content.ReadAsStringAsync()); Assert.Equal(owner.Notes, (await db.Clients.AsNoTracking().SingleAsync()).Notes); await AssertCreateMetadataHistoryAsync(db, ids, 3);
    }
}
