using MediatR;

namespace _1Rad.Application.Features.Staff.Commands.SetStaffPhoto;

/// <summary>
/// Uploads a new profile photo for a staff member, replacing any existing one.
/// If FileStream is null, the existing photo is removed (used by the DELETE endpoint).
/// </summary>
public record SetStaffPhotoCommand(
    Guid StaffId,
    Guid HospitalId,
    string? FileName,
    string? ContentType,
    Stream? FileStream
) : IRequest<(string? PhotoUrl, string? Error)>;
