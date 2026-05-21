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
    DbSet<CustomRole> CustomRoles { get; }
    DbSet<CustomRolePermission> CustomRolePermissions { get; }
    DbSet<OTPVerification> OTPVerifications { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<Patient> Patients { get; }
    DbSet<Referrer> Referrers { get; }
    DbSet<Appointment> Appointments { get; }
    DbSet<ServiceCharge> ServiceCharges { get; }
    DbSet<Invoice> Invoices { get; }
    DbSet<Payment> Payments { get; }
    DbSet<Expense> Expenses { get; }
    DbSet<DiagnosticReport> DiagnosticReports { get; }
    DbSet<DiagnosticReportField> DiagnosticReportFields { get; }
    DbSet<ReportTemplate> ReportTemplates { get; }
    DbSet<ReportingKeyword> ReportingKeywords { get; }
    DbSet<StudyAsset> StudyAssets { get; }
    DbSet<PrescriptionProtocol> PrescriptionProtocols { get; }
    DbSet<ReferralCommission> ReferralCommissions { get; }
    DbSet<SubscriptionPlan> SubscriptionPlans { get; }
    DbSet<HospitalSubscription> HospitalSubscriptions { get; }
    DbSet<StaffMember> StaffMembers { get; }
    DbSet<StaffMemberRole> StaffMemberRoles { get; }
    DbSet<StaffDocument> StaffDocuments { get; }
    DbSet<SalaryRevision> SalaryRevisions { get; }
    DbSet<SalaryDisbursement> SalaryDisbursements { get; }
    DbSet<HospitalLeavePolicy> HospitalLeavePolicies { get; }
    IUserContext UserContext { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
