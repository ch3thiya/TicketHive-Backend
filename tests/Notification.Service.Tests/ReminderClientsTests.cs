using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Notification.Service.Clients;
using Xunit;

namespace Notification.Service.Tests;

public class ReminderClientsTests
{
    [Fact]
    public async Task Catalog_client_maps_live_show_details()
    {
        var showId = Guid.CreateVersion7();
        using var http = Client(request =>
        {
            Assert.Equal("/internal/catalog/shows/reminder-info", request.RequestUri!.AbsolutePath);
            Assert.Contains(showId.ToString(), request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return Json($$"""{"shows":[{"showId":"{{showId}}","startsAtUtc":"2026-10-24T12:00:00Z","eventName":"Concert","venueName":"Hall","cancelled":false}]}""");
        });

        var shows = await new ReminderCatalogClient(http, NullLogger<ReminderCatalogClient>.Instance)
            .GetShowsAsync([showId]);

        Assert.NotNull(shows);
        Assert.Equal("Concert", shows[showId].EventName);
        Assert.Equal("Hall", shows[showId].VenueName);
    }

    [Fact]
    public async Task Booking_client_maps_eligible_order_ticket_counts()
    {
        var orderId = Guid.CreateVersion7();
        using var http = Client(request =>
        {
            Assert.Equal("/internal/booking/orders/reminder-eligibility", request.RequestUri!.AbsolutePath);
            Assert.Contains(orderId.ToString(), request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return Json($$"""{"orders":[{"orderId":"{{orderId}}","ticketCount":2}]}""");
        });

        var orders = await new ReminderBookingClient(http, NullLogger<ReminderBookingClient>.Instance)
            .GetEligibleOrdersAsync([orderId]);

        Assert.NotNull(orders);
        Assert.Equal(2, orders[orderId]);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task Reminder_clients_return_null_when_upstream_rejects_request(HttpStatusCode status)
    {
        using var catalogHttp = Client(_ => new HttpResponseMessage(status));
        using var bookingHttp = Client(_ => new HttpResponseMessage(status));

        Assert.Null(await new ReminderCatalogClient(catalogHttp, NullLogger<ReminderCatalogClient>.Instance)
            .GetShowsAsync([Guid.CreateVersion7()]));
        Assert.Null(await new ReminderBookingClient(bookingHttp, NullLogger<ReminderBookingClient>.Instance)
            .GetEligibleOrdersAsync([Guid.CreateVersion7()]));
    }

    [Fact]
    public async Task Reminder_clients_return_null_for_unreadable_responses()
    {
        using var catalogHttp = Client(_ => Json("not-json"));
        using var bookingHttp = Client(_ => Json("not-json"));

        Assert.Null(await new ReminderCatalogClient(catalogHttp, NullLogger<ReminderCatalogClient>.Instance)
            .GetShowsAsync([Guid.CreateVersion7()]));
        Assert.Null(await new ReminderBookingClient(bookingHttp, NullLogger<ReminderBookingClient>.Instance)
            .GetEligibleOrdersAsync([Guid.CreateVersion7()]));
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> send) =>
        new(new StubHandler(send)) { BaseAddress = new Uri("http://localhost/") };

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
}
