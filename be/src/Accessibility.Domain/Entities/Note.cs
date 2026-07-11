using Accessibility.Domain.Common;
using Accessibility.Domain.Enums;

namespace Accessibility.Domain.Entities;

public class Note : BaseEntity
{
    public Guid UserId { get; set; }
    public Guid? ConversationId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? Category { get; set; }
    public bool IsImportant { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    // Navigation
    public User User { get; set; } = null!;
    public Conversation? Conversation { get; set; }
}
