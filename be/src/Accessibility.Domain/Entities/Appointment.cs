using Accessibility.Domain.Common;
using Accessibility.Domain.Enums;

namespace Accessibility.Domain.Entities;

public class Appointment : BaseEntity
{
    public Guid UserId { get; set; }
    public Guid? ConversationId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTimeOffset StartAt { get; set; }
    public DateTimeOffset? EndAt { get; set; }
    public string? Location { get; set; }
    public AppointmentStatus Status { get; set; } = AppointmentStatus.Scheduled;

    // Navigation
    public User User { get; set; } = null!;
    public Conversation? Conversation { get; set; }
}
