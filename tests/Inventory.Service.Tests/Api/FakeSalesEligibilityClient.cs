using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Inventory.Service.Clients;
using Inventory.Service.Models;

namespace Inventory.Service.Tests.Api;

/// <summary>
/// Stands in for Catalog in API tests: every show is eligible unless a test overrides it,
/// and the override can be flipped between requests to mimic a suspension or reinstatement.
/// </summary>
public sealed class FakeSalesEligibilityClient : ISalesEligibilityClient
{
    private readonly ConcurrentDictionary<Guid, SalesEligibilityStatus> _overrides = new();
    private int _calls;

    public SalesEligibilityStatus Default { get; set; } = SalesEligibilityStatus.Eligible;
    public int Calls => Volatile.Read(ref _calls);

    public void Set(Guid showId, SalesEligibilityStatus status) => _overrides[showId] = status;

    public Task<SalesEligibilityDecision> CheckAsync(Guid showId, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(new SalesEligibilityDecision(_overrides.TryGetValue(showId, out var status) ? status : Default));
    }
}