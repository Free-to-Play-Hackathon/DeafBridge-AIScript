using Accessibility.Application.Common.Interfaces;
using Accessibility.Application.Common.Models;
using Accessibility.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accessibility.Api.Controllers;

[ApiController]
[Route("api/dashboard")]
public class DashboardController : ControllerBase
{
    private readonly IApplicationDbContext _dbContext;

    public DashboardController(IApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet("today")]
    public async Task<IActionResult> GetToday(CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        var today = DateTimeOffset.UtcNow.Date;

        var importantNotes = await _dbContext.Notes.Where(x => x.UserId == user.Id && x.IsImportant).Take(10).ToListAsync(cancellationToken);
        var pendingTasks = await _dbContext.UserTasks.Where(x => x.UserId == user.Id && x.Status != Accessibility.Domain.Enums.UserTaskStatus.Completed && x.Status != Accessibility.Domain.Enums.UserTaskStatus.Cancelled).OrderBy(x => x.DueAt).Take(10).ToListAsync(cancellationToken);
        var todayAppointments = await _dbContext.Appointments.Where(x => x.UserId == user.Id && x.StartAt.Date == today).OrderBy(x => x.StartAt).Take(10).ToListAsync(cancellationToken);
        var upcomingReminders = await _dbContext.Reminders.Where(x => x.UserId == user.Id && x.Status == Accessibility.Domain.Enums.ReminderStatus.Pending).OrderBy(x => x.ScheduledAt).Take(10).ToListAsync(cancellationToken);

        return Ok(new DashboardDto(importantNotes, pendingTasks, todayAppointments, upcomingReminders));
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
