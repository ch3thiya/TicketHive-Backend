using System.Security.Claims;
using BuildingBlocks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BuildingBlocks.Tests;

public class InternalCancellationAuthorizationTests
{
    [Theory]
    [InlineData("APPLICATION", "openid payment:refund", true)]
    [InlineData("APPLICATION", "payment:read", false)]
    [InlineData("USER", "payment:refund", false)]
    public async Task Requires_machine_token_and_exact_scope(string aut, string scope, bool allowed)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCancellationInternalAuthorization("payment:refund");
        using var provider = services.BuildServiceProvider();
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("aut", aut), new Claim("scope", scope) }, "Test"));
        var result = await provider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, null, "CancellationInternal");
        Assert.Equal(allowed, result.Succeeded);
    }
}
