using MiniBank.TestSupport;

namespace MiniBank.Infrastructure.Tests;

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
