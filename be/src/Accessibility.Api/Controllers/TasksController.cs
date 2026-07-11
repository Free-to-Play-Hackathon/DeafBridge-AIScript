using Accessibility.Application.Common.Interfaces;
using Accessibility.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accessibility.Api.Controllers;

[ApiController]
[Route("api/tasks")]
public class TasksController : ControllerBase
{
    private readonly IApplicationDbContext _dbContext;

    public TasksController(IApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<IActionResult> GetTasks(CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        return Ok(await _dbContext.UserTasks.Where(x => x.UserId == user.Id).OrderByDescending(x => x.CreatedAt).ToListAsync(cancellationToken));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetTask(Guid id, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        var task = await _dbContext.UserTasks.FirstOrDefaultAsync(x => x.Id == id && x.UserId == user.Id, cancellationToken);
        return task is null ? NotFound() : Ok(task);
    }

    [HttpPatch("{id:guid}/complete")]
    public async Task<IActionResult> CompleteTask(Guid id, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        var task = await _dbContext.UserTasks.FirstOrDefaultAsync(x => x.Id == id && x.UserId == user.Id, cancellationToken);
        if (task is null) return NotFound();
        task.Status = Accessibility.Domain.Enums.UserTaskStatus.Completed;
        task.CompletedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(task);
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
