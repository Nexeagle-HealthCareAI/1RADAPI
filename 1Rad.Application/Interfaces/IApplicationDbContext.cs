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
    DbSet<AppointmentService> AppointmentServices { get; }
    DbSet<AppointmentComment> AppointmentComments { get; }
    DbSet<ServiceCharge> ServiceCharges { get; }
    DbSet<Invoice> Invoices { get; }
    DbSet<Payment> Payments { get; }
    DbSet<Expense> Expenses { get; }
    DbSet<DiagnosticReport> DiagnosticReports { get; }
    DbSet<DiagnosticReportField> DiagnosticReportFields { get; }
    DbSet<ReportTemplate> ReportTemplates { get; }
    DbSet<ReportingKeyword> ReportingKeywords { get; }
    DbSet<StudyAsset> StudyAssets { get; }
    DbSet<StudySliceIndex> StudySliceIndexes { get; }
    DbSet<PrescriptionProtocol> PrescriptionProtocols { get; }
    DbSet<ReferralCommission> ReferralCommissions { get; }
    DbSet<SubscriptionPlan> SubscriptionPlans { get; }
    DbSet<HospitalSubscription> HospitalSubscriptions { get; }
    DbSet<SubscriptionPaymentRequest> SubscriptionPaymentRequests { get; }
    DbSet<StaffMember> StaffMembers { get; }
    DbSet<StaffMemberRole> StaffMemberRoles { get; }
    DbSet<StaffDocument> StaffDocuments { get; }
    DbSet<SalaryRevision> SalaryRevisions { get; }
    DbSet<SalaryDisbursement> SalaryDisbursements { get; }
    DbSet<HospitalLeavePolicy> HospitalLeavePolicies { get; }
    DbSet<StaffAttendance> StaffAttendances { get; }
    DbSet<StaffLeaveRequest> StaffLeaveRequests { get; }
    IUserContext UserContext { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    // Atomically hand out the next value for a named counter.
    //
    // Used by the booking flow to fix the concurrent-booking race: the
    // daily token number and the APP-### display id were both computed with
    // a read-then-write (count + 1) that let simultaneous bookings collide.
    // This does the increment-and-return inside ONE locked statement, so 3-4
    // terminals booking at the same instant each receive a DISTINCT value.
    //
    // seedIfAbsent is the value handed out the first time a counter key is
    // seen (e.g. max existing token + 1, or the current display sequence), so
    // a brand-new hospital-day starts in the right place with no data backfill.
    Task<int> NextSequenceValueAsync(Guid hospitalId, string counterKey, int seedIfAbsent, CancellationToken cancellationToken);

    // EF Core ChangeTracker access — needed by OCC-aware command handlers
    // (Phase B2 Track 3) so they can set OriginalValues["RowVersion"] on
    // an existing tracked entity. Exposing the EntityEntry shape keeps
    // the interface narrower than full DbContext while still letting
    // handlers do this one operation.
    Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<TEntity> Entry<TEntity>(TEntity entity) where TEntity : class;
}
