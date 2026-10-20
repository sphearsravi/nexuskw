namespace Nexus.IntegrationTests;

/// <summary>
/// One host and one database for the whole assembly.
/// <para>
/// xUnit runs test classes in parallel, so a per-class fixture meant two
/// <see cref="NexusApp"/> instances dropping and recreating the same database
/// at the same time — which SQL Server reports as "a severe error occurred on
/// the current command". A collection fixture serialises the classes onto one
/// host, which is also a good deal faster.
/// </para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class NexusCollection : ICollectionFixture<NexusApp>
{
    public const string Name = "nexus";
}
