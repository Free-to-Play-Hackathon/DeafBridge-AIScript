namespace Accessibility.Domain.Enums;

public enum ConversationStatus
{
    Active,
    Completed,
    Archived
}

public enum Speaker
{
    DeafUser,
    HearingUser,
    System
}

public enum AnalysisCategory
{
    General,
    Medical,
    Appointment,
    Task,
    Reminder,
    Emergency,
    Education,
    Work,
    Shopping,
    Travel,
    Other
}

public enum Importance
{
    Low,
    Normal,
    High,
    Critical
}

public enum ActionType
{
    SaveNote,
    CreateTask,
    CreateAppointment,
    ScheduleReminder,
    SendEmail,
    None
}

public enum ProposedActionStatus
{
    Proposed,
    Confirmed,
    Rejected,
    Processing,
    Completed,
    Failed,
    Cancelled
}

public enum TaskPriority
{
    Low,
    Normal,
    High,
    Critical
}

public enum UserTaskStatus
{
    Pending,
    InProgress,
    Completed,
    Cancelled
}

public enum AppointmentStatus
{
    Scheduled,
    Completed,
    Cancelled
}

public enum ReminderChannel
{
    Email,
    InApp
}

public enum ReminderStatus
{
    Pending,
    Processing,
    Sent,
    Failed,
    Cancelled
}

public enum AnalysisStatus
{
    Pending,
    Processing,
    Completed,
    Failed
}
