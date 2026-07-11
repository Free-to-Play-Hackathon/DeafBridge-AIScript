using Accessibility.Application.Common.Interfaces;
using Accessibility.Domain.Entities;
using Accessibility.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accessibility.Api.Controllers;

[ApiController]
[Route("api/reminders")]
public class RemindersController : ControllerBase
{
    private readonly IApplicationDbContext _dbContext;

    public RemindersController(IApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<IActionResult> GetReminders(CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        var reminders = await _dbContext.Reminders
            .Where(x => x.UserId == user.Id)
            .OrderBy(x => x.ScheduledAt)
            .Select(x => new
            {
                x.Id,
                x.UserId,
                x.RelatedEntityType,
                x.RelatedEntityId,
                Channel = x.Channel.ToString(),
                x.ScheduledAt,
                Status = x.Status.ToString(),
                x.RetryCount,
                x.LastAttemptAt,
                x.SentAt,
                x.Title,
                x.Description,
                x.RecipientEmail,
                x.CreatedAt
            })
            .ToListAsync(cancellationToken);

        return Ok(reminders);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetReminder(Guid id, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        var reminder = await _dbContext.Reminders.FirstOrDefaultAsync(x => x.Id == id && x.UserId == user.Id, cancellationToken);
        return reminder is null
            ? NotFound()
            : Ok(new
            {
                reminder.Id,
                reminder.UserId,
                reminder.RelatedEntityType,
                reminder.RelatedEntityId,
                Channel = reminder.Channel.ToString(),
                reminder.ScheduledAt,
                Status = reminder.Status.ToString(),
                reminder.RetryCount,
                reminder.LastAttemptAt,
                reminder.SentAt,
                reminder.Title,
                reminder.Description,
                reminder.RecipientEmail,
                reminder.CreatedAt
            });
    }

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> CancelReminder(Guid id, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        var reminder = await _dbContext.Reminders.FirstOrDefaultAsync(x => x.Id == id && x.UserId == user.Id, cancellationToken);
        if (reminder is null) return NotFound();
        reminder.Status = ReminderStatus.Cancelled;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(reminder);
    }

    private async Task<User> GetCurrentUserAsync(CancellationToken cancellationToken)
    {
        var userEmail = User?.Identity?.Name ?? "local@example.com";
        var user = await _dbContext.Users.FirstOrDefaultAsync(x => x.Email == userEmail, cancellationToken);
        if (user is null)
        {
            user = new User { Email = userEmail, DisplayName = userEmail, PreferredLanguage = "vi", TimeZone = "Asia/Ho_Chi_Minh"};
            await _dbContext.Users.AddAsync(user, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        return user;
    }
}
