using Accessibility.Application.Common.Interfaces;
using Accessibility.Application.IntegrationEvents;
using Accessibility.Domain.Entities;
using Accessibility.Domain.Enums;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Accessibility.Infrastructure.Consumers;

public class ScheduleReminderConsumer : IConsumer<ReminderSchedulingRequested>
{
    private readonly IApplicationDbContext _dbContext;

    public ScheduleReminderConsumer(IApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task Consume(ConsumeContext<ReminderSchedulingRequested> context)
    {
        var action = await _dbContext.ProposedActions.FirstOrDefaultAsync(x => x.Id == context.Message.ProposedActionId, context.CancellationToken);
        if (action is null || action.Status == ProposedActionStatus.Completed)
        {
            return;
        }

        var conversation = await _dbContext.Conversations.FirstAsync(x => x.Id == action.ConversationId, context.CancellationToken);
        var reminder = new Reminder
        {
            UserId = conversation.UserId,
            RelatedEntityType = nameof(Appointment),
            RelatedEntityId = action.Id,
            Channel = ReminderChannel.Email,
            ScheduledAt = action.ScheduledAt ?? DateTimeOffset.UtcNow,
            Status = ReminderStatus.Pending,
            Title = action.Title,
            Description = action.Description,
            RecipientEmail = action.RecipientEmail
        };

        await _dbContext.Reminders.AddAsync(reminder, context.CancellationToken);
        action.Status = ProposedActionStatus.Completed;
        action.ExecutedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(context.CancellationToken);
    }
}
