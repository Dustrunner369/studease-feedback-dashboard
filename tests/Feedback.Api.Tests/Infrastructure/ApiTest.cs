using System.Net.Http.Json;
using System.Text.Json;

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
    protected FakeAiGateway Ai => App.Ai;

    protected virtual IDictionary<string, string?>? Settings => null;

    public virtual async Task InitializeAsync()
    {
        App = new FeedbackApp(Database, Settings);
        await App.MigrateAsync();
        await Database.ResetAsync();
        Client = App.CreateClient();
    }

    /// <summary>Signs <see cref="Client"/> in to /admin; its cookie then rides on every later call.</summary>
    protected async Task SignInAsync()
    {
        var response = await Client.PostAsJsonAsync("/api/admin/login", new { key = FeedbackApp.AdminKey });
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Signs in and sets the status of a Feature Request's Problem through PATCH /api/admin/problems/{id}
    /// (Won't do gets a reason).
    /// </summary>
    protected async Task SetProblemStatusAsync(long featureRequestId, string status)
    {
        await SignInAsync();
        var problems = (await Client.GetFromJsonAsync<JsonElement>("/api/admin/problems")).GetProperty("problems");
        foreach (var problem in problems.EnumerateArray())
        {
            var id = problem.GetProperty("id").GetInt64();
            var drawer = await Client.GetFromJsonAsync<JsonElement>($"/api/admin/problems/{id}");
            if (!drawer.GetProperty("featureRequests").EnumerateArray().Any(r => r.GetProperty("id").GetInt64() == featureRequestId))
                continue;
            var response = await Client.PatchAsJsonAsync($"/api/admin/problems/{id}",
                new { status, reason = status == "Won't do" ? "Not for now." : null });
            response.EnsureSuccessStatusCode();
            return;
        }
        throw new InvalidOperationException($"Feature Request {featureRequestId} has no open Problem.");
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await App.DisposeAsync();
    }
}
