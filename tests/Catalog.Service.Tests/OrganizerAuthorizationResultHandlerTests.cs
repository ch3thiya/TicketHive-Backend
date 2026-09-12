using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using Catalog.Service.Authorization;

namespace Catalog.Service.Tests;

public class OrganizerAuthorizationResultHandlerTests
{
    private const string TestScheme = "Test";

    private readonly OrganizerAuthorizationResultHandler _resultHandler = new();

    // The default AuthorizationMiddlewareResultHandler resolves IAuthenticationService
    // to call ForbidAsync/ChallengeAsync, so the fake HttpContext needs a minimal
    // authentication scheme wired up to exercise that fallback path.
    private static DefaultHttpContext CreateHttpContext()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication(TestScheme)
            .AddScheme<AuthenticationSchemeOptions, NoOpAuthHandler>(TestScheme, options => { });

        var httpContext = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            Response = { Body = new MemoryStream() }
        };
        return httpContext;
    }

    [Fact]
    public async Task HandleAsync_IdentityUnavailableFlagSet_Writes503ProblemDetails()
    {
        // Arrange
        var httpContext = CreateHttpContext();
        httpContext.Items[ActiveOrganizerAuthorizationHandler.IdentityUnavailableItemKey] = true;
        var policy = new AuthorizationPolicyBuilder().RequireAssertion(_ => false).Build();
        var failure = PolicyAuthorizationResult.Forbid();

        // Act
        await _resultHandler.HandleAsync(_ => Task.CompletedTask, httpContext, policy, failure);

        // Assert
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, httpContext.Response.StatusCode);
        httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(httpContext.Response.Body).ReadToEndAsync();
        var problem = JsonSerializer.Deserialize<ProblemDetails>(body);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem?.Status);
    }

    [Fact]
    public async Task HandleAsync_NoIdentityUnavailableFlag_DelegatesToDefaultForbidHandling()
    {
        // Arrange
        var httpContext = CreateHttpContext();
        var policy = new AuthorizationPolicyBuilder().RequireAssertion(_ => false).Build();
        var failure = PolicyAuthorizationResult.Forbid();

        // Act
        await _resultHandler.HandleAsync(_ => Task.CompletedTask, httpContext, policy, failure);

        // Assert: the default handler's forbid path returns 403, not 503.
        Assert.Equal(StatusCodes.Status403Forbidden, httpContext.Response.StatusCode);
    }

    [Fact]
    public async Task HandleAsync_Succeeded_CallsNextInPipeline()
    {
        // Arrange
        var httpContext = CreateHttpContext();
        var policy = new AuthorizationPolicyBuilder().RequireAssertion(_ => true).Build();
        var success = PolicyAuthorizationResult.Success();
        var nextCalled = false;

        // Act
        await _resultHandler.HandleAsync(_ => { nextCalled = true; return Task.CompletedTask; }, httpContext, policy, success);

        // Assert
        Assert.True(nextCalled);
    }

    private sealed class NoOpAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public NoOpAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }
}
