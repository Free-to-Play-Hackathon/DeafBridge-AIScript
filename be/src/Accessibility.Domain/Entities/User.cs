using Accessibility.Domain.Common;

namespace Accessibility.Domain.Entities;

public class User : BaseEntity
{
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PreferredLanguage { get; set; } = "vi";
    public string TimeZone { get; set; } = "Asia/Ho_Chi_Minh";

    // Navigation
    public ICollection<Conversation> Conversations { get; set; } = [];
    public ICollection<Note> Notes { get; set; } = [];
    public ICollection<UserTask> Tasks { get; set; } = [];
    public ICollection<Appointment> Appointments { get; set; } = [];
    public ICollection<Reminder> Reminders { get; set; } = [];
}
