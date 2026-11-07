using Feedback.Api.Admin;
using Feedback.Api.Ai;
using Feedback.Api.Data;
using Feedback.Api.GitHub;
using Feedback.Api.Intake;
using Feedback.Api.Outbox;
using Feedback.Api.Portal;
using Feedback.Api.Scoring;
using Feedback.Api.Solutions;
using Feedback.Api.Triage;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<FeedbackDbContext>(options => options
    .UseNpgsql(
        builder.Configuration.GetConnectionString("Feedback"),
        npgsql => npgsql
            // Both are needed: EF's type mapping, and the ADO data source plugin that writes vector parameters.
            .ConfigureDataSource(dataSource => dataSource.UseVector())
            .UseVector()
            .MigrationsHistoryTable("__EFMigrationsHistory", FeedbackDbContext.Schema))
    .UseSnakeCaseNamingConvention());

builder.Services.AddScoped<IntakePipeline>();
builder.Services.AddSingleton<IntakeQueue>();
builder.Services.AddScoped<MatchingStep>();
builder.Services.AddScoped<PriorityScorer>();
builder.Services.AddScoped<ProblemDecisions>();
builder.Services.AddScoped<OutboxDrafter>();
builder.Services.AddScoped<IShippedHook>(services => services.GetRequiredService<OutboxDrafter>());
builder.Services.AddHostedService<IntakeWorker>();

builder.Services.Configure<AiOptions>(builder.Configuration.GetSection(AiOptions.Section));
builder.Services.AddSingleton<IAiGateway, OpenAiGateway>();
builder.Services.AddScoped<AiCallLog>();
builder.Services.AddPortalRateLimit(builder.Configuration);
builder.Services.Configure<SuggestOptions>(builder.Configuration.GetSection(SuggestOptions.Section));
builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection(AdminOptions.Section));
builder.Services.AddSingleton<AdminAuth>();
builder.Services.Configure<PriorityCriteria>(builder.Configuration.GetSection(PriorityCriteria.Section));
builder.Services.Configure<GitHubOptions>(builder.Configuration.GetSection(GitHubOptions.Section));
GitHubApiGateway.AddHttpClient(builder.Services);
builder.Services.AddSingleton<IGitHubGateway, GitHubApiGateway>();
builder.Services.AddSingleton<GitHubConnection>();
builder.Services.Configure<SolutionsOptions>(builder.Configuration.GetSection(SolutionsOptions.Section));
builder.Services.AddScoped<SolutionSuggester>();
builder.Services.AddHostedService<GitHubStartupCheck>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();

app.MapPortalApi();
app.MapAdminApi();
app.MapFallbackToFile("/requests/{id:long}", "request.html");

app.Run();

/// <summary>Exposed so the test host (WebApplicationFactory&lt;Program&gt;) can boot the app.</summary>
public partial class Program;
