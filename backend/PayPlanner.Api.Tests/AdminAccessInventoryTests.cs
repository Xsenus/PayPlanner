using Xunit;

namespace PayPlanner.Api.Tests;

public class AdminAccessInventoryTests
{
    public static IEnumerable<object[]> AdminGatekeeperControllers()
    {
        yield return ["AdminUsersController.cs"];
        yield return ["AppVersionController.cs"];
        yield return ["AuthProvidersAdminController.cs"];
        yield return ["CalendarSyncController.cs"];
        yield return ["DatabaseAdminController.cs"];
        yield return ["FormRequirementsController.cs"];
        yield return ["LegalEntitiesController.cs"];
        yield return ["SystemJobsController.cs"];
        yield return ["UserActivityController.cs"];
        yield return ["UsersController.cs"];
        yield return ["UserSettingsController.cs"];
    }

    [Theory]
    [MemberData(nameof(AdminGatekeeperControllers))]
    public void AdminGatekeepers_UseDatabaseAwareAdminCheck(string controllerFileName)
    {
        var source = ReadSourceFile("backend", "Controllers", controllerFileName);

        if (controllerFileName == "LegalEntitiesController.cs")
            Assert.Contains("if (!await _db.IsApprovedUserAdminAsync(context.HttpContext.User, context.HttpContext.RequestAborted))", source, StringComparison.Ordinal);
        else
            Assert.Contains("IsUserAdminOrLegacyFallbackAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("!userId.HasValue || !await _db.IsUserAdminAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("!userId.HasValue || !await _context.IsUserAdminAsync", source, StringComparison.Ordinal);
    }

    private static string ReadSourceFile(params string[] pathParts)
        => File.ReadAllText(Path.Combine(ResolveRepositoryRoot(), Path.Combine(pathParts)));

    private static string ResolveRepositoryRoot()
    {
        var directory = TestRepository.FindDirectory();
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "backend", "PayPlanner.Api.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}
