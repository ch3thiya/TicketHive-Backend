using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using Xunit;
using Identity.Service.Clients;

namespace Identity.Service.Tests;

/// <summary>
/// Wso2ScimClient.GetM2mAccessTokenAsync used to open its own throwaway
/// HttpClient, which made this class impossible to unit test with a
/// stubbed handler. Now that it reuses the injected HttpClient for the
/// token call too, a single stubbed HttpMessageHandler can answer both the
/// token endpoint and the SCIM endpoints, following the Moq.Protected
/// pattern already used in tests/Catalog.Service.Tests/OrganizerStatusClientTests.cs.
/// </summary>
public class Wso2ScimClientTests
{
    private const string TokenJson = """{"access_token":"m2m-access-token"}""";

    private static Wso2ScimClient CreateClient(Mock<HttpMessageHandler> handler)
    {
        var httpClient = new HttpClient(handler.Object) { BaseAddress = new Uri("https://wso2.local/") };
        var configuration = new ConfigurationBuilder().Build();
        var adminOptions = Options.Create(new Wso2AdminOptions
        {
            AdminUsername = "admin-user",
            AdminPassword = "admin-password",
            M2mClientId = "m2m-client-id",
            M2mClientSecret = "m2m-client-secret"
        });

        return new Wso2ScimClient(httpClient, configuration, adminOptions, Mock.Of<ILogger<Wso2ScimClient>>());
    }

    private static Mock<HttpMessageHandler> CreateHandler(
        Func<HttpRequestMessage, HttpResponseMessage?> respond)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
                respond(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound));
        return handler;
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) =>
        new(statusCode) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task CreateUserAsync_ValidRequest_FetchesTokenThenCreatesUser()
    {
        // Arrange
        var requestedPaths = new List<string>();
        string? authorizationSentToTokenEndpoint = null;

        var handler = CreateHandler(request =>
        {
            requestedPaths.Add(request.RequestUri!.AbsolutePath);

            if (request.RequestUri.AbsolutePath == "/oauth2/token")
            {
                authorizationSentToTokenEndpoint = request.Headers.Authorization?.ToString();
                return JsonResponse(HttpStatusCode.OK, TokenJson);
            }

            if (request.RequestUri.AbsolutePath == "/scim2/Users")
            {
                return JsonResponse(HttpStatusCode.Created, """{"id":"new-wso2-user-id"}""");
            }

            return null;
        });

        var client = CreateClient(handler);

        // Act
        var wso2Id = await client.CreateUserAsync("jane@org.com", "Password123!", "jane@org.com", "Jane Doe", "pending");

        // Assert
        Assert.Equal("new-wso2-user-id", wso2Id);
        Assert.Equal(new[] { "/oauth2/token", "/scim2/Users" }, requestedPaths);
        Assert.NotNull(authorizationSentToTokenEndpoint);
        Assert.StartsWith("Basic ", authorizationSentToTokenEndpoint);
    }

    [Fact]
    public async Task AssignUserToGroupAsync_GroupNotFound_SkipsAssignmentWithoutPatching()
    {
        // Arrange
        var patchCalled = false;
        var handler = CreateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/oauth2/token")
            {
                return JsonResponse(HttpStatusCode.OK, TokenJson);
            }

            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/scim2/Groups")
            {
                return JsonResponse(HttpStatusCode.OK, """{"totalResults":0,"Resources":[]}""");
            }

            if (request.Method == HttpMethod.Patch)
            {
                patchCalled = true;
            }

            return null;
        });

        var client = CreateClient(handler);

        // Act
        await client.AssignUserToGroupAsync("wso2-user-id", "jane@org.com", "Organizer");

        // Assert
        Assert.False(patchCalled);
    }

    [Fact]
    public async Task AssignUserToGroupAsync_UserAlreadyMember_SkipsPatch()
    {
        // Arrange
        const string groupId = "group-123";
        const string wso2UserId = "wso2-user-id";
        var patchCalled = false;

        var handler = CreateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/oauth2/token")
            {
                return JsonResponse(HttpStatusCode.OK, TokenJson);
            }

            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/scim2/Groups")
            {
                return JsonResponse(HttpStatusCode.OK, $$"""{"totalResults":1,"Resources":[{"id":"{{groupId}}"}]}""");
            }

            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == $"/scim2/Groups/{groupId}")
            {
                return JsonResponse(HttpStatusCode.OK, $$"""{"members":[{"value":"{{wso2UserId}}"}]}""");
            }

            if (request.Method == HttpMethod.Patch)
            {
                patchCalled = true;
            }

            return null;
        });

        var client = CreateClient(handler);

        // Act
        await client.AssignUserToGroupAsync(wso2UserId, "jane@org.com", "Organizer");

        // Assert
        Assert.False(patchCalled);
    }

    [Fact]
    public async Task AssignUserToGroupAsync_UserNotYetMember_PatchesGroup()
    {
        // Arrange
        const string groupId = "group-123";
        const string wso2UserId = "wso2-user-id";
        var patchedGroupPaths = new List<string>();

        var handler = CreateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/oauth2/token")
            {
                return JsonResponse(HttpStatusCode.OK, TokenJson);
            }

            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/scim2/Groups")
            {
                return JsonResponse(HttpStatusCode.OK, $$"""{"totalResults":1,"Resources":[{"id":"{{groupId}}"}]}""");
            }

            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == $"/scim2/Groups/{groupId}")
            {
                return JsonResponse(HttpStatusCode.OK, """{"members":[]}""");
            }

            if (request.Method == HttpMethod.Patch && request.RequestUri.AbsolutePath == $"/scim2/Groups/{groupId}")
            {
                patchedGroupPaths.Add(request.RequestUri.AbsolutePath);
                return JsonResponse(HttpStatusCode.OK, "{}");
            }

            return null;
        });

        var client = CreateClient(handler);

        // Act
        await client.AssignUserToGroupAsync(wso2UserId, "jane@org.com", "Organizer");

        // Assert
        Assert.Equal(new[] { $"/scim2/Groups/{groupId}" }, patchedGroupPaths);
    }
}
