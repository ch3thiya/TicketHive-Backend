using System.Threading.Tasks;
using BuildingBlocks;
using Inventory.Service.Db;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit;

namespace Inventory.Service.Tests.Integration;

// Separate from PostgresFixture/PostgresCollection: those back repository
// unit-of-work tests, while this owns a full HTTP pipeline (HoldsApiFactory)
// against its own container so the holds concurrency tests exercise real
// parallel requests through real routing and auth, not just repository
// calls.
public sealed class HoldsApiFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder("postgres:15-alpine").Build();

    public HoldsApiFactory Factory { get; private set; } = null!;
    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        DatabaseMigrator.Migrate(ConnectionString, typeof(DbConnectionFactory).Assembly, NullLogger.Instance);
        Factory = new HoldsApiFactory(ConnectionString);
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _container.DisposeAsync();
    }
}

[CollectionDefinition("HoldsApi")]
public sealed class HoldsApiCollection : ICollectionFixture<HoldsApiFixture> { }
