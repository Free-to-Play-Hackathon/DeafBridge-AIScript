using Accessibility.Application.Common.Interfaces;
using Accessibility.Domain.Entities;
using Accessibility.Domain.Enums;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Accessibility.Infrastructure.Background;

public class ReminderScheduler : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReminderScheduler> _logger;

    public ReminderScheduler(IServiceScopeFactory scopeFactory, ILogger<ReminderScheduler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
                var publishEndpoint = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

                var reminders = await ((DbContext)dbContext).Set<Reminder>()
                    .Where(x => x.Status == ReminderStatus.Pending && x.ScheduledAt <= DateTimeOffset.UtcNow)
                    .OrderBy(x => x.ScheduledAt)
                    .Take(20)
                    .ToListAsync(stoppingToken);

                foreach (var reminder in reminders)
                {
                    var rows = await ((DbContext)dbContext).Database.ExecuteSqlRawAsync(
                        "UPDATE \"Reminders\" SET \"Status\" = {0}, \"LastAttemptAt\" = {1}, \"RetryCount\" = \"RetryCount\" + 1 WHERE \"Id\" = {2} AND \"Status\" = {3}",
                        (int)ReminderStatus.Processing,
                        DateTimeOffset.UtcNow,
                        reminder.Id,
                        (int)ReminderStatus.Pending,
                        stoppingToken);

                    if (rows == 0)
                    {
                        continue;
                    }

                    _logger.LogInformation("Processing reminder {ReminderId}", reminder.Id);
                    await publishEndpoint.Publish(new Application.IntegrationEvents.ReminderDue(reminder.Id), stoppingToken);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Reminder scheduler waiting for database readiness");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
