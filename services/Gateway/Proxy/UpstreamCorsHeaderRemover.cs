using Yarp.ReverseProxy.Transforms;

namespace Gateway.Proxy;

// CORS is answered at the edge only. Services still run their own CORS
// policy, so any Access-Control-* header they return is dropped here to
// avoid duplicate headers, which browsers reject.
public static class UpstreamCorsHeaderRemover
{
    private const string CorsHeaderPrefix = "Access-Control-";

    public static ValueTask RemoveAsync(ResponseTransformContext context)
    {
        var headers = context.HttpContext.Response.Headers;
        var corsHeaders = headers.Keys
            .Where(name => name.StartsWith(CorsHeaderPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var name in corsHeaders)
        {
            headers.Remove(name);
        }

        return ValueTask.CompletedTask;
    }
}
