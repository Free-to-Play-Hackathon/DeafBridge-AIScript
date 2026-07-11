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
        return Ok(await _dbContext.Reminders.Where(x => x.UserId == user.Id).OrderBy(x => x.ScheduledAt).ToListAsync(cancellationToken));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetReminder(Guid id, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        var reminder = await _dbContext.Reminders.FirstOrDefaultAsync(x => x.Id == id && x.UserId == user.Id, cancellationToken);
        return reminder is null ? NotFound() : Ok(reminder);
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
