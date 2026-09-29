using Gateway.Proxy;
using Microsoft.AspNetCore.Http;
using Xunit;
using Yarp.ReverseProxy.Transforms;

namespace Gateway.Tests;

public class UpstreamCorsHeaderRemoverTests
{
    [Fact]
    public async Task Removes_every_upstream_access_control_header()
    {
        var httpContext = new DefaultHttpContext();
        var headers = httpContext.Response.Headers;
        headers["Access-Control-Allow-Origin"] = "http://localhost:5173";
        headers["Access-Control-Allow-Credentials"] = "true";
        headers["access-control-expose-headers"] = "Location";
        headers["Content-Type"] = "application/json";
        headers["Location"] = "/api/booking/orders/1";

        await UpstreamCorsHeaderRemover.RemoveAsync(new ResponseTransformContext { HttpContext = httpContext });

        Assert.DoesNotContain(headers.Keys, name => name.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("application/json", headers.ContentType.ToString());
        Assert.Equal("/api/booking/orders/1", headers.Location.ToString());
    }
}
