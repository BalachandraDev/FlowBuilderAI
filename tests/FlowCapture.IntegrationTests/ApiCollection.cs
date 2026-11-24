using Xunit;

namespace FlowCapture.IntegrationTests;

[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<TestApp>;
