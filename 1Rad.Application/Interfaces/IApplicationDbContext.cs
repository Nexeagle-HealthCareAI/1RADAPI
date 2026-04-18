using Microsoft.EntityFrameworkCore;
using _1Rad.Domain.Entities;

namespace _1Rad.Application.Interfaces;

public interface IApplicationDbContext
{
    DbSet<User> Users { get; }
    DbSet<HospitalGroup> HospitalGroups { get; }
    DbSet<Hospital> Hospitals { get; }
    DbSet<Role> Roles { get; }
    DbSet<UserHospitalMapping> UserHospitalMappings { get; }
    DbSet<OTPVerification> OTPVerifications { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
