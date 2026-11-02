using MiniBank.TestSupport;

namespace MiniBank.Notifications.Tests;

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
