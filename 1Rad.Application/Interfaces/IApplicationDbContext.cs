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
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<Patient> Patients { get; }
    DbSet<Referrer> Referrers { get; }
    DbSet<Appointment> Appointments { get; }
    DbSet<ServiceCharge> ServiceCharges { get; }
    DbSet<Invoice> Invoices { get; }
    DbSet<Payment> Payments { get; }
    DbSet<Expense> Expenses { get; }
    IUserContext UserContext { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
