using System.Net;
using Microsoft.Extensions.Options;
using Notification.Service.Models;
using Notification.Service.Services;
using Xunit;

namespace Notification.Service.Tests;

public class CancellationEmailSenderTests
{
    private static CancellationEmail Email => new("key", "test@example.invalid", "Test", Guid.CreateVersion7(), new[] { Guid.CreateVersion7() }, 100, "LKR", true, "Show");

    [Fact]
    public async Task Default_simulation_never_sends_http()
    {
        var handler = new FakeHandler();
        var sender = new CancellationEmailSender(new HttpClient(handler), Options.Create(new CancellationEmailOptions()));
        Assert.Equal("Simulated", await sender.SendAsync(Email));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Provider_failure_is_reconciled_instead_of_retried()
    {
        var handler = new FakeHandler();
        var sender = new CancellationEmailSender(new HttpClient(handler), Options.Create(new CancellationEmailOptions
        { Simulate = false, ServiceId = "test", TemplateId = "test", PublicKey = "test" }));
        Assert.Equal("NeedsReconciliation", await sender.SendAsync(Email));
        Assert.Equal(1, handler.Calls);
    }

    private class FakeHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
}
