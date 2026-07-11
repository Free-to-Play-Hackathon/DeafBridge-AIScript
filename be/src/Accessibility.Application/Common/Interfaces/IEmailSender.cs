namespace Accessibility.Application.Common.Interfaces;

public interface IEmailSender
{
    Task SendReminderAsync(ReminderEmail email, CancellationToken cancellationToken);
}

public record ReminderEmail(
    string To,
    string Subject,
    string Body,
    DateTimeOffset ScheduledAt,
    string Title,
    string Description,
    string? Location,
    string DashboardUrl);
