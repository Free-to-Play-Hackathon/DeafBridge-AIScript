using Accessibility.Application.Common.Interfaces;
using Accessibility.Application.IntegrationEvents;
using Accessibility.Domain.Entities;
using Accessibility.Domain.Enums;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Accessibility.Infrastructure.Consumers;

public class ReminderDueConsumer : IConsumer<ReminderDue>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly IPublishEndpoint _publishEndpoint;

    public ReminderDueConsumer(IApplicationDbContext dbContext, IPublishEndpoint publishEndpoint)
    {
        _dbContext = dbContext;
        _publishEndpoint = publishEndpoint;
    }

    public async Task Consume(ConsumeContext<ReminderDue> context)
    {
        var reminder = await _dbContext.Reminders.FirstOrDefaultAsync(x => x.Id == context.Message.ReminderId, context.CancellationToken);
        if (reminder is null || reminder.Status is not (ReminderStatus.Pending or ReminderStatus.Processing))
        {
            return;
        }

        reminder.Status = ReminderStatus.Processing;
        reminder.LastAttemptAt = DateTimeOffset.UtcNow;
        reminder.RetryCount += 1;
        await _dbContext.SaveChangesAsync(context.CancellationToken);

        await _publishEndpoint.Publish(new EmailSendRequested(reminder.Id, reminder.RecipientEmail ?? string.Empty, reminder.Title ?? "Reminder", reminder.Description ?? string.Empty, reminder.ScheduledAt, string.Empty));
    }
}
