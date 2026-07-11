using Accessibility.Application.Common.Interfaces;
using Accessibility.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accessibility.Api.Controllers;

[ApiController]
[Route("api/appointments")]
public class AppointmentsController : ControllerBase
{
    private readonly IApplicationDbContext _dbContext;

    public AppointmentsController(IApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<IActionResult> GetAppointments(CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        return Ok(await _dbContext.Appointments.Where(x => x.UserId == user.Id).OrderBy(x => x.StartAt).ToListAsync(cancellationToken));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetAppointment(Guid id, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        var appointment = await _dbContext.Appointments.FirstOrDefaultAsync(x => x.Id == id && x.UserId == user.Id, cancellationToken);
        return appointment is null ? NotFound() : Ok(appointment);
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
