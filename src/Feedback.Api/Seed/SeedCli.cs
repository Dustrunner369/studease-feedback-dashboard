using Feedback.Api.Data;
using Feedback.Api.Intake;

namespace Feedback.Api.Seed;

/// <summary>
/// The <c>seed</c> CLI subcommands: <c>dotnet run --project src/Feedback.Api -- seed load [--file path]</c> and
/// <c>... -- seed remove</c>. Both use the configured <c>ConnectionStrings:Feedback</c>.
/// <c>load</c> needs the <see cref="IntakeWorker"/> running (Program.cs starts it; the test host already has it).
/// </summary>
public static class SeedCli
{
    public static async Task<int> RunAsync(IServiceProvider services, string[] args, TextWriter output, CancellationToken ct)
    {
        switch (args)
        {
            case ["load", .. var rest]:
                var path = rest is ["--file", var p] ? p : rest is [] ? SeedFile.FindDefault() : null;
                if (path is null)
                {
                    output.WriteLine(rest is [] ? $"Can't find {SeedFile.DefaultPath}; pass --file <path>." : Usage);
                    return 2;
                }
                SeedFile file;
                try { file = SeedFile.Read(path); }
                catch (SeedFileException e) { output.WriteLine(e.Message); return 2; }
                output.WriteLine($"Seed file: {Path.GetFullPath(path)}");
                return await new SeedLoader(services.GetRequiredService<IServiceScopeFactory>(),
                    services.GetRequiredService<IntakeQueue>()).LoadAsync(file, output, ct);

            case ["remove"]:
                await using (var scope = services.CreateAsyncScope())
                {
                    var counts = await FeedbackTables.EmptyAsync(scope.ServiceProvider.GetRequiredService<FeedbackDbContext>(), ct);
                    output.WriteLine($"Emptied {counts.Count} tables in the {FeedbackDbContext.Schema} schema (schema, extension and migration history kept):");
                    foreach (var (table, rows) in counts) output.WriteLine($"  {table}: {rows}");
                }
                return 0;

            default:
                output.WriteLine(Usage);
                return 2;
        }
    }

    private const string Usage = """
        Usage:
          seed load [--file <path>]   submit the seed backlog through the real AI pipeline (refuses if there's data)
          seed remove                 empty every table in the feedback schema
        """;
}
