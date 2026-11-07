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
    public DbSet<PriorityScoring> PriorityScorings => Set<PriorityScoring>();
    public DbSet<DecisionLogEntry> DecisionLog => Set<DecisionLogEntry>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<DecisionBrief> DecisionBriefs => Set<DecisionBrief>();

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
                t.HasCheckConstraint("ck_problems_priority", $"priority IS NULL OR priority IN ({PriorityLevels.SqlList})");
            });
            e.Property(p => p.Priority).HasMaxLength(6);
            e.Property(p => p.CreatedAt).HasDefaultValueSql("now()");
            e.Property(p => p.RecommendedLevel).HasMaxLength(6);
            e.HasOne<PriorityScoring>().WithMany().HasForeignKey(p => p.FirstScoringId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(p => p.CurrentScoring).WithMany().HasForeignKey(p => p.CurrentScoringId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(p => p.RecommendedScore);
            // Spelled out: the snake_case convention would make these git_hub_*.
            e.Property(p => p.GitHubIssueNumber).HasColumnName("github_issue_number");
            e.Property(p => p.GitHubIssueUrl).HasColumnName("github_issue_url").HasMaxLength(300);
            e.Property(p => p.GitHubIssueState).HasColumnName("github_issue_state").HasMaxLength(6);
            e.Property(p => p.GitHubCheckedAt).HasColumnName("github_checked_at");
        });

        model.Entity<PriorityScoring>(e =>
        {
            e.ToTable("priority_scorings", t =>
            {
                t.HasCheckConstraint("ck_priority_scorings_strategic_fit", "strategic_fit BETWEEN 1 AND 5");
                t.HasCheckConstraint("ck_priority_scorings_pain", "pain BETWEEN 1 AND 5");
                t.HasCheckConstraint("ck_priority_scorings_retention_risk", "retention_risk BETWEEN 1 AND 5");
            });
            e.Property(s => s.CreatedAt).HasDefaultValueSql("now()");
            // Scorings go with their Problem when it is merged away or emptied.
            e.HasOne<Problem>().WithMany().HasForeignKey(s => s.ProblemId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => s.ProblemId);
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

        model.Entity<DecisionLogEntry>(e =>
        {
            e.ToTable("decision_log", t =>
                t.HasCheckConstraint("ck_decision_log_kind", $"kind IN ({DecisionKinds.SqlList})"));
            e.Property(d => d.Kind).HasMaxLength(16);
            e.Property(d => d.Details).HasColumnType("jsonb");
            e.Property(d => d.CreatedAt).HasDefaultValueSql("now()");
            e.HasOne<Problem>().WithMany().HasForeignKey(d => d.ProblemId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(d => d.ProblemId);
        });

        model.Entity<AiCall>(AiCall.Configure);
        model.Entity<OutboxMessage>(OutboxMessage.Configure);
        model.Entity<DecisionBrief>(DecisionBrief.Configure);
    }
}
