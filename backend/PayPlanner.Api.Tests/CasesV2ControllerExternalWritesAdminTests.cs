using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    public async Task ExternalWriteBoundary_AdminAssignmentRequiresActiveSelectedOwner(int op, bool active)
    {
        await using var db = await PassportBoundaryContextAsync();
        var seed = await SeedClientPromotionAsync(db);
        var admin = await db.Roles.FirstOrDefaultAsync(x => x.Name == "admin");
        if (admin is null) { admin = new Role { Name = "admin" }; db.Roles.Add(admin); await db.SaveChangesAsync(); }
        await db.Users.Where(x => x.Id == seed.Actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.RoleId, admin.Id));
        var owner = await CreateUserAsync(db, "external-admin-owner@example.local", "Выбранный ответственный");
        await db.Users.Where(x => x.Id == owner.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, active));
        db.ChangeTracker.Clear();
        var input = ExternalWriteInput(seed); input.ResponsibleUserId = owner.Id;
        var before = await ExternalWriteSnapshotAsync(db);
        var result = await RunExternalWriteAsync(db, seed, op, input);
        if (!active) { Assert.IsType<BadRequestObjectResult>(result); Assert.Equal(before, await ExternalWriteSnapshotAsync(db)); }
        else { var reply = Assert.IsType<ContactDirectoryItemDto>(op == 0 ? Assert.IsType<CreatedResult>(result).Value : Assert.IsType<OkObjectResult>(result).Value); Assert.Equal(owner.Id, reply.ResponsibleUserId); Assert.Equal(owner.Id, await db.ExternalContacts.Where(x => x.Id == reply.SourceId).Select(x => x.ResponsibleUserId).SingleAsync()); }
    }
}
