using Npgsql;
using Testcontainers.PostgreSql;

namespace Feedback.Api.Tests.Infrastructure;

/// <summary>
/// One throwaway Postgres + pgvector container for the whole test run.
/// Mirrors production: the vector extension is installed into the
/// <c>feedback</c> schema by hand before the app's migrations run,
/// because the migrations never create it.
/// </summary>
public sealed class FeedbackDatabase : IAsyncLifetime
{
    // Production is Postgres 18 with pgvector 0.8.6 (Neon).
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:0.8.6-pg18")
        .Build();

    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            SearchPath = "feedback",
        }.ConnectionString;

        await ExecuteAsync("""
            CREATE SCHEMA IF NOT EXISTS feedback;
            CREATE EXTENSION IF NOT EXISTS vector SCHEMA feedback;
            """);
    }

    /// <summary>Empties every table in the feedback schema (except the migration history).</summary>
    public Task ResetAsync() => ExecuteAsync("""
        DO $$
        DECLARE t text;
        BEGIN
          SELECT string_agg(format('feedback.%I', tablename), ', ') INTO t
          FROM pg_tables
          WHERE schemaname = 'feedback' AND tablename <> '__EFMigrationsHistory';
          IF t IS NOT NULL THEN
            EXECUTE 'TRUNCATE ' || t || ' RESTART IDENTITY CASCADE';
          END IF;
        END $$;
        """);

    /// <summary>
    /// Arrange-only: sets the status of a Feature Request's Problem, until a test can do it through /admin.
    /// Never use it to assert.
    /// </summary>
    public async Task SetProblemStatusAsync(long featureRequestId, string status)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            UPDATE problems SET status = @status
            WHERE id = (SELECT problem_id FROM feature_requests WHERE id = @id)
            """, connection);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("id", featureRequestId);
        if (await command.ExecuteNonQueryAsync() != 1)
            throw new InvalidOperationException($"Feature Request {featureRequestId} has no Problem.");
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}
