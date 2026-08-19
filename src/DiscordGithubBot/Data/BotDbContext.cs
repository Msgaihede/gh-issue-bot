using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace DiscordGithubBot.Data;

public class BotDbContext(DbContextOptions<BotDbContext> options) : DbContext(options)
{
    public DbSet<IssueEmbedding> IssueEmbeddings => Set<IssueEmbedding>();
    public DbSet<PendingReport> PendingReports => Set<PendingReport>();
    public DbSet<PendingAttachment> PendingAttachments => Set<PendingAttachment>();
    public DbSet<RepoSyncState> RepoSyncStates => Set<RepoSyncState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var issue = modelBuilder.Entity<IssueEmbedding>();
        issue.HasIndex(e => new { e.RepoKey, e.IssueNumber }).IsUnique();
        issue.Property(e => e.Vector)
            .HasConversion(v => VectorConversion.ToBytes(v), b => VectorConversion.FromBytes(b))
            .Metadata.SetValueComparer(new ValueComparer<float[]>(
                (a, b) => (a ?? Array.Empty<float>()).SequenceEqual(b ?? Array.Empty<float>()),
                v => v.Aggregate(17, (h, f) => HashCode.Combine(h, f)),
                v => v.ToArray()));

        modelBuilder.Entity<RepoSyncState>().HasKey(s => s.RepoKey);

        modelBuilder.Entity<PendingReport>()
            .HasMany(r => r.Attachments)
            .WithOne()
            .HasForeignKey(a => a.PendingReportId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
