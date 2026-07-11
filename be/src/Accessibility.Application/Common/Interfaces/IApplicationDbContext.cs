using Accessibility.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accessibility.Application.Common.Interfaces;

public interface IApplicationDbContext
{
    DbSet<User> Users { get; }
    DbSet<Conversation> Conversations { get; }
    DbSet<TranscriptSegment> TranscriptSegments { get; }
    DbSet<AgentAnalysis> AgentAnalyses { get; }
    DbSet<ProposedAction> ProposedActions { get; }
    DbSet<Note> Notes { get; }
    DbSet<UserTask> UserTasks { get; }
    DbSet<Appointment> Appointments { get; }
    DbSet<Reminder> Reminders { get; }
    DbSet<OutboxMessage> OutboxMessages { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
