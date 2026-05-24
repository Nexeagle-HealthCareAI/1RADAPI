using _1Rad.Application.Features.Staff.Commands.AddSalaryDisbursement;
using _1Rad.Application.Features.Staff.Commands.AddStaffMember;
using _1Rad.Application.Features.Staff.Commands.DeleteSalaryRevision;
using _1Rad.Application.Features.Staff.Commands.DeleteStaffDocument;
using _1Rad.Application.Features.Staff.Commands.RemoveStaffMember;
using _1Rad.Application.Features.Staff.Commands.SaveSalaryRevision;
using _1Rad.Application.Features.Staff.Commands.SetDisbursementStatus;
using _1Rad.Application.Features.Staff.Commands.SetStaffPhoto;
using _1Rad.Application.Features.Staff.Commands.UpdateStaffMember;
using _1Rad.Application.Features.Staff.Commands.UploadStaffDocument;
using _1Rad.Application.Features.Staff.Queries.GetHospitalStaff;
using _1Rad.Application.Features.Staff.Queries.GetStaffDocuments;
using _1Rad.Application.Features.Staff.Queries.GetStaffSalary;
using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace _1RadAPI.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/[controller]")]
public class StaffController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IUserContext _userContext;

    public StaffController(IMediator mediator, IUserContext userContext)
    {
        _mediator = mediator;
        _userContext = userContext;
    }

    // GET /api/v1/staff
    [HttpGet]
    public async Task<IActionResult> GetStaff()
    {
        var hospitalId = _userContext.HospitalId;
        if (hospitalId == Guid.Empty) return BadRequest("Hospital context missing.");

        var result = await _mediator.Send(new GetHospitalStaffQuery(hospitalId));
        return Ok(result);
    }

    // POST /api/v1/staff
    [HttpPost]
    public async Task<IActionResult> AddStaff([FromBody] AddStaffMemberCommand command)
    {
        var cmd = command with { HospitalId = _userContext.HospitalId };
        var (staffId, error) = await _mediator.Send(cmd);
        return error == null
            ? Ok(new { staffId, message = "Staff member added successfully." })
            : BadRequest(new { message = error });
    }

    // PUT /api/v1/staff/{id}
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateStaff(Guid id, [FromBody] UpdateStaffMemberCommand command)
    {
        var cmd = command with { StaffId = id, HospitalId = _userContext.HospitalId };
        var (success, error) = await _mediator.Send(cmd);
        return success
            ? Ok(new { message = "Staff member updated successfully." })
            : BadRequest(new { message = error });
    }

    // DELETE /api/v1/staff/{id}
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> RemoveStaff(Guid id)
    {
        var (success, error) = await _mediator.Send(new RemoveStaffMemberCommand(id, _userContext.HospitalId));
        return success
            ? Ok(new { message = "Staff member removed." })
            : BadRequest(new { message = error });
    }

    // GET /api/v1/staff/{id}/documents
    [HttpGet("{id:guid}/documents")]
    public async Task<IActionResult> GetDocuments(Guid id)
    {
        var result = await _mediator.Send(new GetStaffDocumentsQuery(id, _userContext.HospitalId));
        return Ok(result);
    }

    // POST /api/v1/staff/{id}/documents  (multipart/form-data)
    [HttpPost("{id:guid}/documents")]
    [RequestSizeLimit(10 * 1024 * 1024)] // 10 MB
    public async Task<IActionResult> UploadDocument(Guid id, IFormFile file, [FromForm] string category = "Other")
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { message = "No file provided." });

        await using var stream = file.OpenReadStream();
        var cmd = new UploadStaffDocumentCommand(
            StaffId:          id,
            HospitalId:       _userContext.HospitalId,
            UploadedByUserId: _userContext.UserId,
            FileName:         file.FileName,
            ContentType:      file.ContentType,
            FileSizeBytes:    (int)file.Length,
            Category:         category,
            FileStream:       stream);

        var (documentId, error) = await _mediator.Send(cmd);
        return error == null
            ? Ok(new { documentId, message = "Document uploaded." })
            : BadRequest(new { message = error });
    }

    // DELETE /api/v1/staff/{id}/documents/{docId}
    [HttpDelete("{id:guid}/documents/{docId:guid}")]
    public async Task<IActionResult> DeleteDocument(Guid id, Guid docId)
    {
        var (success, error) = await _mediator.Send(
            new DeleteStaffDocumentCommand(docId, id, _userContext.HospitalId));
        return success
            ? Ok(new { message = "Document deleted." })
            : BadRequest(new { message = error });
    }

    // ── Photo ──────────────────────────────────────────────────────────

    // POST /api/v1/staff/{id}/photo  (multipart/form-data, field name: "photo")
    [HttpPost("{id:guid}/photo")]
    [RequestSizeLimit(5 * 1024 * 1024)] // 5 MB
    public async Task<IActionResult> UploadPhoto(Guid id, IFormFile photo)
    {
        if (photo == null || photo.Length == 0)
            return BadRequest(new { message = "No photo provided." });

        await using var stream = photo.OpenReadStream();
        var cmd = new SetStaffPhotoCommand(
            StaffId:     id,
            HospitalId:  _userContext.HospitalId,
            FileName:    photo.FileName,
            ContentType: photo.ContentType,
            FileStream:  stream);

        var (photoUrl, error) = await _mediator.Send(cmd);
        return error == null
            ? Ok(new { photoUrl, message = "Photo uploaded." })
            : BadRequest(new { message = error });
    }

    // DELETE /api/v1/staff/{id}/photo
    [HttpDelete("{id:guid}/photo")]
    public async Task<IActionResult> DeletePhoto(Guid id)
    {
        var cmd = new SetStaffPhotoCommand(
            StaffId:     id,
            HospitalId:  _userContext.HospitalId,
            FileName:    null,
            ContentType: null,
            FileStream:  null);
        var (_, error) = await _mediator.Send(cmd);
        return error == null
            ? Ok(new { message = "Photo removed." })
            : BadRequest(new { message = error });
    }

    // ── Payroll ────────────────────────────────────────────────────────

    // GET /api/v1/staff/{id}/salary
    [HttpGet("{id:guid}/salary")]
    public async Task<IActionResult> GetSalary(Guid id)
    {
        var result = await _mediator.Send(new GetStaffSalaryQuery(id, _userContext.HospitalId));
        return result == null ? NotFound(new { message = "Staff not found." }) : Ok(result);
    }

    // POST /api/v1/staff/{id}/salary/revisions  (also acts as upsert by EffectiveFrom)
    [HttpPost("{id:guid}/salary/revisions")]
    public async Task<IActionResult> SaveSalaryRevision(Guid id, [FromBody] SaveSalaryRevisionRequest body)
    {
        var cmd = new SaveSalaryRevisionCommand(
            StaffId:         id,
            HospitalId:      _userContext.HospitalId,
            CreatedByUserId: _userContext.UserId,
            EffectiveFrom:   body.EffectiveFrom,
            BasicPay:        body.BasicPay,
            Hra:             body.Hra,
            Travel:          body.Travel,
            OtherAllowances: body.OtherAllowances,
            PfDeduction:     body.PfDeduction,
            Tds:             body.Tds,
            OtherDeductions: body.OtherDeductions,
            Note:            body.Note);

        var (revisionId, error) = await _mediator.Send(cmd);
        return error == null
            ? Ok(new { revisionId, message = "Salary revision saved." })
            : BadRequest(new { message = error });
    }

    // DELETE /api/v1/staff/{id}/salary/revisions/{revId}
    [HttpDelete("{id:guid}/salary/revisions/{revId:guid}")]
    public async Task<IActionResult> DeleteSalaryRevision(Guid id, Guid revId)
    {
        var (success, error) = await _mediator.Send(
            new DeleteSalaryRevisionCommand(revId, id, _userContext.HospitalId));
        return success
            ? Ok(new { message = "Revision deleted." })
            : BadRequest(new { message = error });
    }

    // POST /api/v1/staff/{id}/salary/disbursements
    [HttpPost("{id:guid}/salary/disbursements")]
    public async Task<IActionResult> AddDisbursement(Guid id, [FromBody] AddDisbursementRequest body)
    {
        var cmd = new AddSalaryDisbursementCommand(
            StaffId:          id,
            HospitalId:       _userContext.HospitalId,
            CreatedByUserId:  _userContext.UserId,
            RevisionId:       body.RevisionId,
            Month:            body.Month,
            GrossPay:         body.GrossPay,
            NetPay:           body.NetPay,
            StructureGross:   body.StructureGross,
            StructureNet:     body.StructureNet,
            LwpDays:          body.LwpDays,
            LwpDeduction:     body.LwpDeduction,
            PerDayRate:       body.PerDayRate,
            PaidLeaveInMonth: body.PaidLeaveInMonth,
            LwpLeaveInMonth:  body.LwpLeaveInMonth,
            AttendanceJson:   body.AttendanceJson,
            EncashmentDays:   body.EncashmentDays,
            EncashmentBonus:  body.EncashmentBonus,
            ExtraPay:         body.ExtraPay,
            ExtraPayReason:   body.ExtraPayReason,
            PaymentMode:      body.PaymentMode,
            Reference:        body.Reference,
            PaidOnDate:       body.PaidOnDate,
            Notes:            body.Notes,
            Status:           body.Status ?? "Paid");

        var (disbursementId, error) = await _mediator.Send(cmd);
        return error == null
            ? Ok(new { disbursementId, message = "Salary recorded." })
            : BadRequest(new { message = error });
    }

    // PATCH /api/v1/staff/{id}/salary/disbursements/{disbId}/status
    [HttpPatch("{id:guid}/salary/disbursements/{disbId:guid}/status")]
    public async Task<IActionResult> SetDisbursementStatus(Guid id, Guid disbId, [FromBody] SetDisbursementStatusRequest body)
    {
        var cmd = new SetDisbursementStatusCommand(
            DisbursementId:  disbId,
            HospitalId:      _userContext.HospitalId,
            UpdatedByUserId: _userContext.UserId,
            Status:          body.Status,
            PaymentMode:     body.PaymentMode,
            Reference:       body.Reference,
            PaidOnDate:      body.PaidOnDate);

        var (success, error) = await _mediator.Send(cmd);
        return success
            ? Ok(new { message = $"Disbursement marked as {body.Status}." })
            : BadRequest(new { message = error });
    }
}

public record SaveSalaryRevisionRequest(
    string EffectiveFrom,
    decimal BasicPay,
    decimal Hra,
    decimal Travel,
    decimal OtherAllowances,
    decimal PfDeduction,
    decimal Tds,
    decimal OtherDeductions,
    string? Note);

public record AddDisbursementRequest(
    Guid? RevisionId,
    string Month,
    decimal GrossPay,
    decimal NetPay,
    decimal StructureGross,
    decimal StructureNet,
    decimal LwpDays,
    decimal LwpDeduction,
    decimal PerDayRate,
    int PaidLeaveInMonth,
    int LwpLeaveInMonth,
    string? AttendanceJson,
    decimal EncashmentDays,
    decimal EncashmentBonus,
    decimal ExtraPay,
    string? ExtraPayReason,
    string PaymentMode,
    string? Reference,
    string PaidOnDate,
    string? Notes,
    string? Status); // Draft | Paid — defaults to Paid when omitted

public record SetDisbursementStatusRequest(
    string Status,           // Draft | Paid
    string? PaymentMode,     // optional override
    string? Reference,       // optional UTR / cheque #
    string? PaidOnDate);     // optional "YYYY-MM-DD"
