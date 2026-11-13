using _1Rad.Application.Features.Staff.WorkDays;
using _1Rad.Domain.Constants;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace _1RadAPI.Controllers;

/// <summary>
/// The centre's working days (Nov 2026): weekly off, Saturdays off, holidays and
/// each person's own weekly off. Leave, attendance and salary all count days
/// from this calendar. Admins only, like the rest of Staff &amp; salary.
/// </summary>
[Authorize(Roles = $"{RoleConstants.AdminDoctor},{RoleConstants.AdminOperator}")]
[ApiController]
[Route("api/v1/work-calendar")]
public class WorkCalendarController : ControllerBase
{
    private readonly IMediator _mediator;
    public WorkCalendarController(IMediator mediator) => _mediator = mediator;

    // GET /api/v1/work-calendar?year=2026 → week, holidays and working days per month
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int? year) =>
        Ok(await _mediator.Send(new GetWorkCalendarQuery(year ?? DateTime.UtcNow.Year)));

    // PUT /api/v1/work-calendar { weeklyOff: ["SUN"], saturdaysOff: "2_4" | null }
    [HttpPut]
    public async Task<IActionResult> SaveWeek([FromBody] SaveWorkWeekCommand body)
    {
        await _mediator.Send(body);
        return Ok(new { success = true });
    }

    // POST /api/v1/work-calendar/holidays { holidays: [{ date: "2026-01-26", name: "Republic Day" }] }
    [HttpPost("holidays")]
    public async Task<IActionResult> AddHolidays([FromBody] AddHolidaysCommand body) =>
        Ok(new { success = true, saved = await _mediator.Send(body) });

    // DELETE /api/v1/work-calendar/holidays/{id}
    [HttpDelete("holidays/{id:guid}")]
    public async Task<IActionResult> DeleteHoliday(Guid id)
    {
        await _mediator.Send(new DeleteHolidayCommand(id));
        return Ok(new { success = true });
    }

    // PUT /api/v1/work-calendar/staff/{staffId}/weekly-off { weeklyOff: ["TUE"] }  (empty = centre's)
    [HttpPut("staff/{staffId:guid}/weekly-off")]
    public async Task<IActionResult> SetStaffWeeklyOff(Guid staffId, [FromBody] StaffWeeklyOffRequest body)
    {
        await _mediator.Send(new SetStaffWeeklyOffCommand(staffId, body.WeeklyOff));
        return Ok(new { success = true });
    }

    // GET /api/v1/work-calendar/leave-days?staffId=&from=&to= → how many leave days a range uses
    [HttpGet("leave-days")]
    public async Task<IActionResult> LeaveDays([FromQuery] Guid staffId, [FromQuery] DateOnly from, [FromQuery] DateOnly to) =>
        Ok(await _mediator.Send(new CountLeaveDaysQuery(staffId, from, to)));
}

public record StaffWeeklyOffRequest(List<string>? WeeklyOff);
