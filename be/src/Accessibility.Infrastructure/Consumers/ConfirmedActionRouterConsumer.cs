using Accessibility.Application.Common.Interfaces;
using Accessibility.Application.IntegrationEvents;
using Accessibility.Domain.Entities;
using Accessibility.Domain.Enums;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Accessibility.Infrastructure.Consumers;

public class ConfirmedActionRouterConsumer : IConsumer<ProposedActionConfirmed>
{
    private readonly IApplicationDbContext _dbContext;
    private readonly IPublishEndpoint _publishEndpoint;

    public ConfirmedActionRouterConsumer(IApplicationDbContext dbContext, IPublishEndpoint publishEndpoint)
    {
        _dbContext = dbContext;
        _publishEndpoint = publishEndpoint;
    }

    public async Task Consume(ConsumeContext<ProposedActionConfirmed> context)
    {
        var action = await _dbContext.ProposedActions.FirstOrDefaultAsync(x => x.Id == context.Message.ProposedActionId, context.CancellationToken);
        if (action is null)
        {
            return;
        }

        action.Status = ProposedActionStatus.Processing;
        await _dbContext.SaveChangesAsync(context.CancellationToken);

        switch (action.ActionType)
        {
            case ActionType.SaveNote:
                await _publishEndpoint.Publish(new NoteCreationRequested(action.Id));
                break;
            case ActionType.CreateTask:
                await _publishEndpoint.Publish(new TaskCreationRequested(action.Id));
                break;
            case ActionType.CreateAppointment:
                await _publishEndpoint.Publish(new AppointmentCreationRequested(action.Id));
                break;
            case ActionType.ScheduleReminder:
                await _publishEndpoint.Publish(new ReminderSchedulingRequested(action.Id));
                break;
            case ActionType.SendEmail:
                await _publishEndpoint.Publish(new EmailSendRequested(action.Id, action.RecipientEmail ?? string.Empty, action.Title, action.Description ?? string.Empty, action.ScheduledAt ?? DateTimeOffset.UtcNow, action.Location ?? string.Empty));
                break;
        }
    }
}
