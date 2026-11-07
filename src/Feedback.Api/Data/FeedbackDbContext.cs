using Feedback.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Feedback.Api.Data;

public sealed class FeedbackDbContext(DbContextOptions<FeedbackDbContext> options) : DbContext(options)
{
    /// <summary>Every table, and the migration history, lives in this schema. Studease's own tables are never touched.</summary>
    public const string Schema = "feedback";

    public DbSet<Problem> Problems => Set<Problem>();
    public DbSet<FeatureRequest> FeatureRequests => Set<FeatureRequest>();
    public DbSet<Upvote> Upvotes => Set<Upvote>();
    public DbSet<AiCall> AiCalls => Set<AiCall>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);
        // Deliberately no HasPostgresExtension("vector"): the extension is installed
        // into the feedback schema outside the migrations (prod: by hand; tests: the fixture).

        model.Entity<Problem>(e =>
        {
            e.ToTable("problems", t =>
            {
                t.HasCheckConstraint("ck_problems_category", $"category IN ({Categories.SqlList})");
                t.HasCheckConstraint("ck_problems_status", $"status IN ({ProblemStatuses.SqlList})");
            });
            e.Property(p => p.CreatedAt).HasDefaultValueSql("now()");
        });

        model.Entity<FeatureRequest>(e =>
        {
            e.ToTable("feature_requests", t =>
                t.HasCheckConstraint("ck_feature_requests_category", $"category IN ({Categories.SqlList})"));
            e.Property(r => r.Embedding).HasColumnType("vector(1536)");
            e.Property(r => r.CreatedAt).HasDefaultValueSql("now()");
            e.HasOne(r => r.Problem).WithMany(p => p.FeatureRequests)
                .HasForeignKey(r => r.ProblemId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(r => r.CreatedAt);
        });

        model.Entity<Upvote>(e =>
        {
            e.ToTable("upvotes");
            e.HasKey(u => new { u.FeatureRequestId, u.BrowserToken });
            e.Property(u => u.BrowserToken).HasMaxLength(64);
            e.Property(u => u.CreatedAt).HasDefaultValueSql("now()");
            e.HasOne<FeatureRequest>().WithMany()
                .HasForeignKey(u => u.FeatureRequestId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<AiCall>(AiCall.Configure);
    }
}
