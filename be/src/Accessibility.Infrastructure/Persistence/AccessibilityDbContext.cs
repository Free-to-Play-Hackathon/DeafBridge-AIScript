using Accessibility.Application.Common.Interfaces;
using Accessibility.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accessibility.Infrastructure.Persistence;

public class AccessibilityDbContext : DbContext, IApplicationDbContext
{
    public AccessibilityDbContext(DbContextOptions<AccessibilityDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<TranscriptSegment> TranscriptSegments => Set<TranscriptSegment>();
    public DbSet<AgentAnalysis> AgentAnalyses => Set<AgentAnalysis>();
    public DbSet<ProposedAction> ProposedActions => Set<ProposedAction>();
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<UserTask> UserTasks => Set<UserTask>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<Reminder> Reminders => Set<Reminder>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        NormalizeDateTimeOffsets();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        NormalizeDateTimeOffsets();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasIndex(e => e.Email).IsUnique();
        });

        modelBuilder.Entity<Conversation>(entity =>
        {
            entity.HasOne(e => e.User)
                .WithMany(e => e.Conversations)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TranscriptSegment>(entity =>
        {
            entity.HasOne(e => e.Conversation)
                .WithMany(e => e.Transcripts)
                .HasForeignKey(e => e.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Analysis)
                .WithOne(e => e.TranscriptSegment)
                .HasForeignKey<AgentAnalysis>(e => e.TranscriptSegmentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentAnalysis>(entity =>
        {
            entity.HasOne(e => e.Conversation)
                .WithMany(e => e.Analyses)
                .HasForeignKey(e => e.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProposedAction>(entity =>
        {
            entity.HasIndex(e => e.IdempotencyKey);
            entity.HasOne(e => e.Conversation)
                .WithMany(e => e.ProposedActions)
                .HasForeignKey(e => e.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.AgentAnalysis)
                .WithMany(e => e.ProposedActions)
                .HasForeignKey(e => e.AgentAnalysisId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Note>(entity =>
        {
            entity.HasOne(e => e.User)
                .WithMany(e => e.Notes)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserTask>(entity =>
        {
            entity.HasOne(e => e.User)
                .WithMany(e => e.Tasks)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Appointment>(entity =>
        {
            entity.HasOne(e => e.User)
                .WithMany(e => e.Appointments)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Reminder>(entity =>
        {
            entity.HasOne(e => e.User)
                .WithMany(e => e.Reminders)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        base.OnModelCreating(modelBuilder);
    }

    private void NormalizeDateTimeOffsets()
    {
        foreach (var entry in ChangeTracker.Entries()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            foreach (var property in entry.Properties)
            {
                if (property.CurrentValue is DateTimeOffset dateTimeOffset)
                {
                    property.CurrentValue = dateTimeOffset.ToUniversalTime();
                }
            }
        }
    }
}
