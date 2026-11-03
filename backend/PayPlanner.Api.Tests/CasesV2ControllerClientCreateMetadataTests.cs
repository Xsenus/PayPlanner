using System.Collections;
using System.ComponentModel.DataAnnotations.Schema;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    private static readonly PropertyInfo[] ClientResponseMetadata = typeof(Client).GetProperties()
        .Where(property => property.CanWrite && property.GetCustomAttribute<NotMappedAttribute>() is not null).ToArray();

    private static Client ForgedClientMetadata(bool nullLists = false)
    {
        var model = new Client { Name = "Новый профиль без чужих отметок", Notes = "Синтетические заметки", Phone = "+70000000001", Email = "computed@example.local", IsActive = false };
        Assert.Equal(20, ClientResponseMetadata.Length);
        foreach (var property in ClientResponseMetadata)
        {
            object? value = property.PropertyType == typeof(string) ? "Подложная служебная подпись"
                : property.PropertyType == typeof(int) || property.PropertyType == typeof(int?) ? 987654
                : property.PropertyType == typeof(bool) ? true
                : property.PropertyType == typeof(DateTime?) ? new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc)
                : property.PropertyType == typeof(List<RelatedDocumentLink>) ? nullLists ? null : new List<RelatedDocumentLink> { new() { Id = 987654, Label = "Копия чужого документа", Amount = 999m, Type = "income" } }
                : throw new InvalidOperationException("Add an independent forgery for " + property.Name);
            property.SetValue(model, value);
        }
        return model;
    }

    private static void AssertClientMetadata(Client reply, string responsibleName)
    {
        var defaults = new Client(); var forged = new List<string>();
        foreach (var property in ClientResponseMetadata)
        {
            var value = property.GetValue(reply);
            if (property.Name == nameof(Client.ResponsibleUserName)) { if (!Equals(responsibleName, value)) forged.Add(property.Name); }
            else if (property.PropertyType == typeof(List<RelatedDocumentLink>)) { if (value is not IEnumerable rows || rows.Cast<object>().Any()) forged.Add(property.Name); }
            else if (!Equals(property.GetValue(defaults), value)) forged.Add(property.Name);
        }
        Assert.True(forged.Count == 0, "reflectedMetadata=" + string.Join(',', forged));
    }

    private static void AssertClientMetadataJson(JsonElement reply, string responsibleName)
    {
        var forged = new List<string>();
        foreach (var property in ClientResponseMetadata)
        {
            var key = JsonNamingPolicy.CamelCase.ConvertName(property.Name); var present = reply.TryGetProperty(key, out var value);
            if (property.Name == nameof(Client.ResponsibleUserName)) { if (!present || value.GetString() != responsibleName) forged.Add(key); }
            else if (property.PropertyType == typeof(List<RelatedDocumentLink>)) { if (!present || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 0) forged.Add(key); }
            else if (property.PropertyType == typeof(bool)) { if (!present || value.ValueKind != JsonValueKind.False) forged.Add(key); }
            else if (property.PropertyType == typeof(int)) { if (!present || value.GetInt32() != 0) forged.Add(key); }
            else if (present && value.ValueKind != JsonValueKind.Null) forged.Add(key);
        }
        Assert.True(forged.Count == 0, "reflectedWireMetadata=" + string.Join(',', forged));
    }

    public static IEnumerable<object[]> ClientCreateMetadataHttpCases()
        => from route in new[] { 0, 1, 2 } from postgres in new[] { false, true } from legacy in new[] { false, true } select new object[] { route, postgres, legacy };

    [Theory]
    [MemberData(nameof(ClientCreateMetadataHttpCases))]
    public async Task ClientCreateMetadata_HttpReplyIgnoresForgedCountersLinksAndAudit(int route, bool postgres, bool legacy)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var (actor, ids) = await SeedBulkCasesAsync(db);
        if (legacy) await db.RolePermissions.Where(row => row.RoleId == actor.RoleId).ExecuteDeleteAsync(); else await GrantSectionPermissionAsync(db, actor, RolePermissionCatalog.Clients);
        var old = await db.Clients.AsNoTracking().SingleAsync(); var input = ForgedClientMetadata();
        using var server = CaseWriteServer(db, actor); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString());
        using var response = await client.PostAsJsonAsync(route == 0 ? "/api/clients" : $"/api/v{route}/clients", input);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var reply = json.RootElement;
        AssertClientMetadataJson(reply, actor.FullName); var createdId = reply.GetProperty("id").GetInt32(); var saved = await db.Clients.AsNoTracking().SingleAsync(row => row.Id == createdId);
        Assert.Equal(input.Name, saved.Name); Assert.Equal(input.Notes, saved.Notes); Assert.Equal(input.Phone, saved.Phone); Assert.Equal(input.Email, saved.Email); Assert.False(saved.IsActive); Assert.Equal(actor.Id, saved.ResponsibleUserId);
        Assert.Equal(2, await db.Clients.CountAsync()); var previous = await db.Clients.AsNoTracking().SingleAsync(row => row.Id == old.Id); Assert.Equal(old.Name, previous.Name); Assert.Equal(old.CreatedAt, previous.CreatedAt); Assert.Equal(old.ResponsibleUserId, previous.ResponsibleUserId);
        Assert.Null(db.Database.CurrentTransaction); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    public static IEnumerable<object[]> ClientCreateMetadataDirectCases()
        => from route in new[] { 1, 2 } from postgres in new[] { false, true } from nullLists in new[] { false, true } select new object[] { route, postgres, nullLists };

    [Theory]
    [MemberData(nameof(ClientCreateMetadataDirectCases))]
    public async Task ClientCreateMetadata_DirectReplyResetsComputedStateWithoutEditingSourceLists(int route, bool postgres, bool nullLists)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var (actor, ids) = await SeedBulkCasesAsync(db); await GrantSectionPermissionAsync(db, actor, RolePermissionCatalog.Clients); var input = ForgedClientMetadata(nullLists); var suppliedList = input.RelatedPayments;
        var reply = Assert.IsType<Client>(Assert.IsType<CreatedResult>(await CreateAtomicClientAsync(db, actor, input, route)).Value);
        AssertClientMetadata(reply, actor.FullName); Assert.NotSame(suppliedList, reply.RelatedPayments);
        if (!nullLists) Assert.Single(suppliedList);
        Assert.Equal(input.Name, (await db.Clients.AsNoTracking().SingleAsync(row => row.Id == reply.Id)).Name); Assert.Equal(2, await db.Clients.CountAsync());
        Assert.Null(db.Database.CurrentTransaction); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ClientCreateMetadata_InvalidIdentityIsRejectedBeforeInputMetadataIsRewritten(int route)
    {
        await using var db = await CreateContextAsync(); var (actor, ids) = await SeedBulkCasesAsync(db); await GrantSectionPermissionAsync(db, actor, RolePermissionCatalog.Clients);
        var input = ForgedClientMetadata(); input.Id = 987654; var suppliedList = input.RelatedPayments;
        Assert.IsType<BadRequestObjectResult>(await CreateAtomicClientAsync(db, actor, input, route));
        Assert.True(input.AuditIsDeleted); Assert.Equal(987654, input.PaymentsCount); Assert.Same(suppliedList, input.RelatedPayments); Assert.Single(suppliedList);
        Assert.Equal(1, await db.Clients.CountAsync()); Assert.Null(db.Database.CurrentTransaction); await AssertBulkCasesUnchangedAsync(db, ids);
    }
}
