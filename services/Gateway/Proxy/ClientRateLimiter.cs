using System.Threading.RateLimiting;

namespace Gateway.Proxy;

// Fixed window per client IP. The IP is the X-Forwarded-For value applied by
// the forwarded-headers middleware, since Container Apps ingress sits in front.
public static class ClientRateLimiter
{
    private const string ExemptPartition = "exempt";
    private const string UnknownClient = "unknown";

    public static PartitionedRateLimiter<HttpContext> Create(ClientRateLimitOptions options) =>
        PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            if (IsExempt(context.Request))
            {
                return RateLimitPartition.GetNoLimiter(ExemptPartition);
            }

            var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? UnknownClient;
            return RateLimitPartition.GetFixedWindowLimiter(clientIp, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = options.PermitLimit,
                Window = TimeSpan.FromSeconds(options.WindowSeconds),
                QueueLimit = options.QueueLimit,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            });
        });

    // Health probes and the PayHere server-to-server callback must never be
    // throttled: a rejected payment notification would be retried late or lost.
    public static bool IsExempt(HttpRequest request) =>
        request.Path.StartsWithSegments("/health")
        || (HttpMethods.IsPost(request.Method) && request.Path.Equals("/api/payment/notify", StringComparison.OrdinalIgnoreCase));
}
