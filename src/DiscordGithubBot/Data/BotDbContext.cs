using Microsoft.EntityFrameworkCore;

namespace DiscordGithubBot.Data;

public class BotDbContext(DbContextOptions<BotDbContext> options) : DbContext(options)
{
    public DbSet<CachedIssue> CachedIssues => Set<CachedIssue>();
    public DbSet<PendingReport> PendingReports => Set<PendingReport>();
    public DbSet<PendingAttachment> PendingAttachments => Set<PendingAttachment>();
    public DbSet<RepoSyncState> RepoSyncStates => Set<RepoSyncState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CachedIssue>().HasIndex(e => new { e.RepoKey, e.IssueNumber }).IsUnique();

        modelBuilder.Entity<RepoSyncState>().HasKey(s => s.RepoKey);

        modelBuilder.Entity<PendingReport>()
            .HasMany(r => r.Attachments)
            .WithOne()
            .HasForeignKey(a => a.PendingReportId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
