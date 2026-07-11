using Accessibility.Application.Common.Interfaces;
using Accessibility.Application.IntegrationEvents;
using Accessibility.Domain.Entities;
using Accessibility.Domain.Enums;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Accessibility.Infrastructure.Consumers;

public class CreateAppointmentConsumer : IConsumer<AppointmentCreationRequested>
{
    private readonly IApplicationDbContext _dbContext;

    public CreateAppointmentConsumer(IApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task Consume(ConsumeContext<AppointmentCreationRequested> context)
    {
        var action = await _dbContext.ProposedActions.FirstOrDefaultAsync(x => x.Id == context.Message.ProposedActionId, context.CancellationToken);
        if (action is null || action.Status == ProposedActionStatus.Completed)
        {
            return;
        }

        var conversation = await _dbContext.Conversations.FirstAsync(x => x.Id == action.ConversationId, context.CancellationToken);
        var appointment = new Appointment
        {
            UserId = conversation.UserId,
            ConversationId = action.ConversationId,
            Title = action.Title,
            Description = action.Description,
            StartAt = action.ScheduledAt ?? DateTimeOffset.UtcNow,
            EndAt = action.ScheduledAt?.AddMinutes(30),
            Location = action.Location,
            Status = AppointmentStatus.Scheduled
        };

        await _dbContext.Appointments.AddAsync(appointment, context.CancellationToken);
        action.Status = ProposedActionStatus.Completed;
        action.ExecutedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(context.CancellationToken);
    }
}
