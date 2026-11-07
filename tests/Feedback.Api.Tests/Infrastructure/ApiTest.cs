namespace Feedback.Api.Tests.Infrastructure;

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<FeedbackDatabase>
{
    public const string Name = "api";
}

/// <summary>
/// Base for HTTP API tests: every test starts from empty tables and gets a fresh client.
/// Tests in the collection run one at a time against the shared container.
/// </summary>
[Collection(ApiCollection.Name)]
public abstract class ApiTest(FeedbackDatabase database) : IAsyncLifetime
{
    protected FeedbackDatabase Database { get; } = database;
    protected FeedbackApp App { get; private set; } = null!;
    protected HttpClient Client { get; private set; } = null!;

    protected virtual IDictionary<string, string?>? Settings => null;

    public async Task InitializeAsync()
    {
        App = new FeedbackApp(Database, Settings);
        await App.MigrateAsync();
        await Database.ResetAsync();
        Client = App.CreateClient();
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await App.DisposeAsync();
    }
}
