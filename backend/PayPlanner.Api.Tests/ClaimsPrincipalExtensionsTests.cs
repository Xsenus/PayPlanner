using System.Security.Claims;
using PayPlanner.Api.Extensions;
using Xunit;

namespace PayPlanner.Api.Tests;

public class ClaimsPrincipalExtensionsTests
{
    [Fact]
    public void HasAdminRole_ReturnsTrueForAdminRoleClaim()
    {
        var principal = CreatePrincipal("admin");

        Assert.True(principal.HasAdminRole());
    }

    [Fact]
    public void HasAdminRole_ReturnsFalseForDifferentRoleCasing()
    {
        var principal = CreatePrincipal("Admin");

        Assert.False(principal.HasAdminRole());
    }

    [Fact]
    public void HasAdminRole_ReturnsFalseForMissingPrincipal()
    {
        ClaimsPrincipal? principal = null;

        Assert.False(principal.HasAdminRole());
    }

    private static ClaimsPrincipal CreatePrincipal(string role)
        => new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, role)],
            authenticationType: "Test"));
}
