using Microsoft.EntityFrameworkCore;

namespace DiscordGithubBot.Data;

public class BotDbContext(DbContextOptions<BotDbContext> options) : DbContext(options)
{
    public DbSet<CachedIssue> CachedIssues => Set<CachedIssue>();
    public DbSet<PendingReport> PendingReports => Set<PendingReport>();
    public DbSet<PendingAttachment> PendingAttachments => Set<PendingAttachment>();
    public DbSet<RepoSyncState> RepoSyncStates => Set<RepoSyncState>();
    public DbSet<RepoFile> RepoFiles => Set<RepoFile>();
    public DbSet<RepoMapState> RepoMapStates => Set<RepoMapState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CachedIssue>().HasIndex(e => new { e.RepoKey, e.IssueNumber }).IsUnique();

        modelBuilder.Entity<RepoSyncState>().HasKey(s => s.RepoKey);
        modelBuilder.Entity<RepoMapState>().HasKey(s => s.RepoKey);
        modelBuilder.Entity<RepoFile>().HasIndex(f => new { f.RepoKey, f.Path }).IsUnique();

        modelBuilder.Entity<PendingReport>()
            .HasMany(r => r.Attachments)
            .WithOne()
            .HasForeignKey(a => a.PendingReportId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
