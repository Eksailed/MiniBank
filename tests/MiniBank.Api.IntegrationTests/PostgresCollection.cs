using MiniBank.TestSupport;

namespace MiniBank.Api.IntegrationTests;

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
