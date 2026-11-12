using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

/// <summary>
/// Saved viewer measurements/annotations for one study (gap analysis P3-7).
/// Table dbo.StudyAnnotations (DB script 98). StudyKey = "appointment:{guid}" or "study:{guid}".
/// </summary>
public class StudyAnnotation : IHospitalContext
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HospitalId { get; set; }
    public string StudyKey { get; set; } = string.Empty;
    public string AnnotationsJson { get; set; } = "[]";
    public int AnnotationCount { get; set; }
    public Guid? UpdatedByUserId { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
