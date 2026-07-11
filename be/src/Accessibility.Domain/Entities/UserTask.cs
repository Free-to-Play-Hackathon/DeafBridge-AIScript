using Accessibility.Domain.Common;
using Accessibility.Domain.Enums;

namespace Accessibility.Domain.Entities;

public class UserTask : BaseEntity
{
    public Guid UserId { get; set; }
    public Guid? ConversationId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public TaskPriority Priority { get; set; } = TaskPriority.Normal;
    public UserTaskStatus Status { get; set; } = UserTaskStatus.Pending;
    public DateTimeOffset? CompletedAt { get; set; }

    // Navigation
    public User User { get; set; } = null!;
    public Conversation? Conversation { get; set; }
}
