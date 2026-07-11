using Accessibility.Application.Common.Interfaces;
using Accessibility.Application.IntegrationEvents;
using Accessibility.Domain.Entities;
using Accessibility.Domain.Enums;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Accessibility.Infrastructure.Consumers;

public class SendEmailConsumer : IConsumer<EmailSendRequested>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly IEmailSender _emailSender;
    private readonly ILogger<SendEmailConsumer> _logger;
    private readonly IPublishEndpoint _publishEndpoint;

    public SendEmailConsumer(IApplicationDbContext dbContext, IEmailSender emailSender, ILogger<SendEmailConsumer> logger, IPublishEndpoint publishEndpoint)
    {
        _dbContext = dbContext;
        _emailSender = emailSender;
        _logger = logger;
        _publishEndpoint = publishEndpoint;
    }

    public async Task Consume(ConsumeContext<EmailSendRequested> context)
    {
        var reminder = await _dbContext.Reminders.FirstOrDefaultAsync(x => x.Id == context.Message.ReminderId, context.CancellationToken);
        if (reminder is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(reminder.RecipientEmail))
        {
            reminder.Status = ReminderStatus.Cancelled;
            await _dbContext.SaveChangesAsync(context.CancellationToken);
            return;
        }

        try
        {
            await _emailSender.SendReminderAsync(new ReminderEmail(
                reminder.RecipientEmail,
                reminder.Title ?? "Reminder",
                $"Reminder: {reminder.Title}\nScheduled: {reminder.ScheduledAt:O}\n{reminder.Description}",
                reminder.ScheduledAt,
                reminder.Title ?? "Reminder",
                reminder.Description ?? string.Empty,
                string.Empty,
                "http://localhost:8080"), context.CancellationToken);

            reminder.Status = ReminderStatus.Sent;
            reminder.SentAt = DateTimeOffset.UtcNow;
            await _dbContext.SaveChangesAsync(context.CancellationToken);
            await _publishEndpoint.Publish(new EmailSent(reminder.Id));
        }
        catch (Exception ex)
        {
            reminder.Status = ReminderStatus.Failed;
            reminder.RetryCount += 1;
            await _dbContext.SaveChangesAsync(context.CancellationToken);
            _logger.LogError(ex, "Failed to send reminder email for reminder {ReminderId}", reminder.Id);
            await _publishEndpoint.Publish(new EmailFailed(reminder.Id, ex.Message));
        }
    }
}
