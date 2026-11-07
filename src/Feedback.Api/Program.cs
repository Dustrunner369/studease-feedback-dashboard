using Feedback.Api.Data;
using Feedback.Api.Intake;
using Feedback.Api.Portal;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<FeedbackDbContext>(options => options
    .UseNpgsql(
        builder.Configuration.GetConnectionString("Feedback"),
        npgsql => npgsql
            .UseVector()
            .MigrationsHistoryTable("__EFMigrationsHistory", FeedbackDbContext.Schema))
    .UseSnakeCaseNamingConvention());

builder.Services.AddScoped<IntakePipeline>();
builder.Services.AddPortalRateLimit(builder.Configuration);

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();

app.MapPortalApi();
app.MapFallbackToFile("/requests/{id:long}", "request.html");

app.Run();

/// <summary>Exposed so the test host (WebApplicationFactory&lt;Program&gt;) can boot the app.</summary>
public partial class Program;
