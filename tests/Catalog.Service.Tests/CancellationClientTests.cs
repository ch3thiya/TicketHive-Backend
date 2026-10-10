using System.Net;
using Catalog.Service.Clients;
using Xunit;

namespace Catalog.Service.Tests;

public class CancellationClientTests
{
    [Fact]
    public async Task Acknowledgement_barrier_closes_inventory_then_booking()
    {
        var factory = new RecordingFactory();
        await new CancellationClient(factory).EstablishCancellationBarrierAsync(Guid.Parse("10000000-0000-0000-0000-000000000001"));
        Assert.Equal(new[] { "Inventory", "Booking" }, factory.Calls);
    }

    private sealed class RecordingFactory : IHttpClientFactory
    {
        public List<string> Calls { get; } = new();
        public HttpClient CreateClient(string name) => new(new Handler(() => Calls.Add(name.Replace("Cancellation", "")))) { BaseAddress = new Uri("http://test/") };
        private sealed class Handler(Action record) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                record();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
        }
    }
}
