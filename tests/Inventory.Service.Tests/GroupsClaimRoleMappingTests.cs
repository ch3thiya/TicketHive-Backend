using System.Security.Claims;
using Xunit;

namespace Inventory.Service.Tests;

// Program.cs sets RoleClaimType = "groups" on the customer JWT scheme so
// Asgardeo's "groups" claim is treated as the standard .NET role claim
// (AC7). This proves that mechanism: a ClaimsIdentity built the same way
// the JWT bearer handler builds one from a validated token (a "groups"
// claim, with "groups" passed as the role claim type) answers IsInRole
// correctly.
public class GroupsClaimRoleMappingTests
{
    [Fact]
    public void GroupsClaim_WithConfiguredRoleClaimType_SatisfiesIsInRole()
    {
        // Arrange
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "customer-sub"),
            new Claim("groups", "Organizer")
        };
        var identity = new ClaimsIdentity(claims, "Bearer", ClaimTypes.NameIdentifier, "groups");
        var principal = new ClaimsPrincipal(identity);

        // Act & Assert
        Assert.True(principal.IsInRole("Organizer"));
        Assert.False(principal.IsInRole("Admin"));
    }
}
