namespace _1Rad.Domain.Common;

/// <summary>
/// Tactical Interface for entities that belong to a specific Hospital context.
/// Enables automatic Global Query Filtering.
/// </summary>
public interface IHospitalContext
{
    Guid HospitalId { get; set; }
}
