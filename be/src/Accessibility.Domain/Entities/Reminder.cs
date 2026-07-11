using Accessibility.Domain.Common;
using Accessibility.Domain.Enums;

namespace Accessibility.Domain.Entities;

public class Reminder : BaseEntity
{
    public Guid UserId { get; set; }
    public string? RelatedEntityType { get; set; }
    public Guid? RelatedEntityId { get; set; }
    public ReminderChannel Channel { get; set; } = ReminderChannel.Email;
    public DateTimeOffset ScheduledAt { get; set; }
    public ReminderStatus Status { get; set; } = ReminderStatus.Pending;
    public int RetryCount { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? RecipientEmail { get; set; }

    // Navigation
    public User User { get; set; } = null!;
}
