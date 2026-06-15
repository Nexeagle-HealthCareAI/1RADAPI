using System.Data;
using System.Data.Common;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Common;
using _1Rad.Domain.Constants;
using _1Rad.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace _1Rad.Infrastructure.Persistence;

public class ApplicationDbContext : DbContext, IApplicationDbContext
{
    private readonly IPublisher _publisher;
    public IUserContext UserContext { get; }

    public ApplicationDbContext(
        DbContextOptions<ApplicationDbContext> options, 
        IPublisher publisher,
        IUserContext userContext) : base(options)
    {
        _publisher = publisher;
        UserContext = userContext;
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<HospitalGroup> HospitalGroups => Set<HospitalGroup>();
    public DbSet<Hospital> Hospitals => Set<Hospital>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserHospitalMapping> UserHospitalMappings => Set<UserHospitalMapping>();
    public DbSet<CustomRole> CustomRoles => Set<CustomRole>();
    public DbSet<CustomRolePermission> CustomRolePermissions => Set<CustomRolePermission>();
    public DbSet<OTPVerification> OTPVerifications => Set<OTPVerification>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<Referrer> Referrers => Set<Referrer>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<AppointmentService> AppointmentServices => Set<AppointmentService>();
    public DbSet<AppointmentComment> AppointmentComments => Set<AppointmentComment>();
    public DbSet<ServiceCharge> ServiceCharges => Set<ServiceCharge>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<CreditTransaction> CreditTransactions => Set<CreditTransaction>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<DiagnosticReport> DiagnosticReports => Set<DiagnosticReport>();
    public DbSet<ReportTemplate> ReportTemplates => Set<ReportTemplate>();
    public DbSet<ReportingKeyword> ReportingKeywords => Set<ReportingKeyword>();
    public DbSet<DiagnosticReportField> DiagnosticReportFields => Set<DiagnosticReportField>();
    public DbSet<ReportAddendum> ReportAddenda => Set<ReportAddendum>();
    public DbSet<ReportAuditEvent> ReportAuditEvents => Set<ReportAuditEvent>();
    public DbSet<StudyAsset> StudyAssets => Set<StudyAsset>();
    public DbSet<ImagingStudy> ImagingStudies => Set<ImagingStudy>();
    public DbSet<StudySliceIndex> StudySliceIndexes => Set<StudySliceIndex>();
    public DbSet<PrescriptionProtocol> PrescriptionProtocols => Set<PrescriptionProtocol>();
    public DbSet<ReferralCommission> ReferralCommissions => Set<ReferralCommission>();
    public DbSet<SubscriptionPlan> SubscriptionPlans => Set<SubscriptionPlan>();
    public DbSet<HospitalSubscription> HospitalSubscriptions => Set<HospitalSubscription>();
    public DbSet<SubscriptionPaymentRequest> SubscriptionPaymentRequests => Set<SubscriptionPaymentRequest>();
    public DbSet<StaffMember> StaffMembers => Set<StaffMember>();
    public DbSet<StaffMemberRole> StaffMemberRoles => Set<StaffMemberRole>();
    public DbSet<StaffDocument> StaffDocuments => Set<StaffDocument>();
    public DbSet<SalaryRevision> SalaryRevisions => Set<SalaryRevision>();
    public DbSet<SalaryDisbursement> SalaryDisbursements => Set<SalaryDisbursement>();
    public DbSet<HospitalLeavePolicy> HospitalLeavePolicies => Set<HospitalLeavePolicy>();
    public DbSet<StaffAttendance> StaffAttendances => Set<StaffAttendance>();
    public DbSet<StaffLeaveRequest> StaffLeaveRequests => Set<StaffLeaveRequest>();
    public DbSet<ApprovalRequest> ApprovalRequests => Set<ApprovalRequest>();
    public DbSet<IdempotencyRecord> IdempotencyKeys => Set<IdempotencyRecord>();
    public DbSet<RadAiQuestionLog> RadAiQuestionLogs => Set<RadAiQuestionLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // RadAI question log — capture for the "retrain" loop. Scoped to a
        // hospital by the global query filter at the end of this method.
        modelBuilder.Entity<RadAiQuestionLog>(entity =>
        {
            entity.ToTable("RadAiQuestionLogs", "dbo");
            entity.HasKey(e => e.RadAiQuestionLogId);
            entity.Property(e => e.Question).HasMaxLength(2000);
            entity.Property(e => e.Page).HasMaxLength(200);
            entity.Property(e => e.ReplyLanguage).HasMaxLength(8);
            entity.Property(e => e.AnswerSnippet).HasMaxLength(500);
            entity.Property(e => e.Model).HasMaxLength(20);
            entity.HasIndex(e => new { e.HospitalId, e.CreatedAt });
            entity.HasIndex(e => new { e.HospitalId, e.Covered });
        });

        // User Configuration
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("Users", "dbo");
            entity.HasKey(e => e.UserId);
            entity.Property(e => e.FullName).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Email).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Mobile).IsRequired().HasMaxLength(20);
            entity.Property(e => e.PasswordHash).IsRequired();
            entity.Property(e => e.PreferredReportingMode).HasMaxLength(50).HasDefaultValue("Structured");
            entity.Property(e => e.Status).HasConversion<string>();
            entity.HasIndex(e => e.Email).IsUnique();
            entity.HasIndex(e => e.Mobile).IsUnique();
        });

        // HospitalGroup Configuration
        modelBuilder.Entity<HospitalGroup>(entity =>
        {
            entity.ToTable("HospitalGroups", "dbo");
            entity.HasKey(e => e.GroupId);
            entity.Property(e => e.GroupName).IsRequired().HasMaxLength(255);
        });

        // Hospital Configuration
        modelBuilder.Entity<Hospital>(entity =>
        {
            entity.ToTable("Hospitals", "dbo");
            entity.HasKey(e => e.HospitalId);
            entity.Property(e => e.HospitalName).IsRequired().HasMaxLength(255);
            entity.Property(e => e.HospitalAddress).IsRequired();
            
            // Metadata Fields
            entity.Property(e => e.GSTIN).HasMaxLength(15);
            entity.Property(e => e.RegistrationNumber).HasMaxLength(100);
            entity.Property(e => e.PAN).HasMaxLength(10);
            entity.Property(e => e.NABHNumber).HasMaxLength(100);
            entity.Property(e => e.IsAutoBillingEnabled).HasDefaultValue(false);

            entity.HasOne(e => e.Group)
                .WithMany(g => g.Hospitals)
                .HasForeignKey(e => e.GroupId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Role Configuration
        modelBuilder.Entity<Role>(entity =>
        {
            entity.ToTable("Roles", "dbo");
            entity.HasKey(e => e.RoleId);
            entity.Property(e => e.RoleName).IsRequired().HasMaxLength(50);
        });

        // UserHospitalMapping Configuration
        modelBuilder.Entity<UserHospitalMapping>(entity =>
        {
            entity.ToTable("UserHospitalMappings", "dbo");
            entity.HasKey(e => e.MappingId);
            entity.HasIndex(e => new { e.UserId, e.HospitalId }).IsUnique();

            entity.HasOne(e => e.User)
                .WithMany(u => u.HospitalMappings)
                .HasForeignKey(e => e.UserId);

            entity.HasOne(e => e.Hospital)
                .WithMany(h => h.UserMappings)
                .HasForeignKey(e => e.HospitalId);

            entity.HasMany(e => e.Roles)
                .WithMany(r => r.HospitalMappings)
                .UsingEntity<Dictionary<string, object>>(
                    "UserHospitalRole",
                    j => j.HasOne<Role>().WithMany().HasForeignKey("RoleId"),
                    j => j.HasOne<UserHospitalMapping>().WithMany().HasForeignKey("MappingId"),
                    j =>
                    {
                        j.ToTable("UserHospitalRoles", "dbo");
                        j.HasKey("MappingId", "RoleId");
                    });
        });

        // Seed Roles
        modelBuilder.Entity<Role>().HasData(
            new Role { RoleId = 1, RoleName = "AdminDoctor" },
            new Role { RoleId = 2, RoleName = "Admin" },
            new Role { RoleId = 3, RoleName = "Doctor" },
            new Role { RoleId = 4, RoleName = "Technician" },
            new Role { RoleId = 5, RoleName = "Receptionist" },
            new Role { RoleId = 6, RoleName = "Accountant" }
        );

        // OTPVerification Configuration
        modelBuilder.Entity<OTPVerification>(entity =>
        {
            entity.ToTable("OTPVerifications", "dbo");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Identifier).IsRequired().HasMaxLength(100);
            entity.Property(e => e.CodeHash).IsRequired();
        });

        // RefreshToken Configuration
        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.ToTable("RefreshTokens", "dbo");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Token).IsRequired().HasMaxLength(500);
            entity.HasOne(e => e.User)
                .WithMany(u => u.RefreshTokens)
                .HasForeignKey(e => e.UserId);
        });

        // Idempotency dedupe table — Phase B2 Track 2.
        modelBuilder.Entity<IdempotencyRecord>(entity =>
        {
            entity.ToTable("IdempotencyKeys", "dbo");
            // (Key, UserId) compound key — see migration 50.
            entity.HasKey(e => new { e.Key, e.UserId });
            entity.Property(e => e.Key).HasMaxLength(80).IsRequired();
            entity.Property(e => e.Method).HasMaxLength(10).IsRequired();
            entity.Property(e => e.Path).HasMaxLength(500).IsRequired();
            entity.Property(e => e.ResponseContentType).HasMaxLength(120);
            entity.HasIndex(e => e.ExpiresAt);
        });

        // Patient Configuration
        modelBuilder.Entity<Patient>(entity =>
        {
            entity.ToTable("Patients", "dbo");
            entity.HasKey(e => e.PatientId);
            entity.Property(e => e.FullName).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Mobile).HasMaxLength(20);
            entity.Property(e => e.PatientIdentifier).IsRequired().HasMaxLength(50);
            entity.HasIndex(e => e.Mobile);
            
            entity.HasOne(e => e.Hospital)
                .WithMany()
                .HasForeignKey(e => e.HospitalId);

            entity.HasOne(e => e.Referrer)
                .WithMany(r => r.Patients)
                .HasForeignKey(e => e.ReferrerId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Referrer Configuration
        modelBuilder.Entity<Referrer>(entity =>
        {
            entity.ToTable("Referrers", "dbo");
            entity.HasKey(e => e.ReferrerId);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Contact).HasMaxLength(20);
            
            entity.HasOne(e => e.Hospital)
                .WithMany()
                .HasForeignKey(e => e.HospitalId);
        });

        // Appointment Configuration
        modelBuilder.Entity<Appointment>(entity =>
        {
            entity.ToTable("Appointments", "dbo");
            entity.HasKey(e => e.AppointmentId);
            entity.Property(e => e.DisplayId).HasMaxLength(50);
            entity.Property(e => e.PatientName).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Service).HasMaxLength(255);
            entity.Property(e => e.Modality).HasMaxLength(50);
            entity.Property(e => e.Type).HasMaxLength(50);
            entity.Property(e => e.Status).HasMaxLength(50);

            entity.HasOne(e => e.Patient)
                .WithMany()
                .HasForeignKey(e => e.PatientId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Hospital)
                .WithMany()
                .HasForeignKey(e => e.HospitalId);

            // B2 Track 3 — OCC token. ROWVERSION is server-maintained; EF
            // ships the original value with every UPDATE's WHERE clause so
            // a concurrent change triggers DbUpdateConcurrencyException.
            entity.Property(e => e.RowVersion).IsRowVersion();
        });

        // AppointmentService Configuration — per-line-item child of an
        // Appointment. A single visit may carry many services (X-ray + CT
        // + USG); each gets one row here with its own status, TAT
        // milestones, amount, and downstream report/study/commission
        // attachments. Cascade-delete from the parent Appointment so
        // disposing a booking doesn't leave orphan service lines behind.
        modelBuilder.Entity<AppointmentService>(entity =>
        {
            entity.ToTable("AppointmentServices", "dbo");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ServiceName).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Modality).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(30);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.Property(e => e.ReferralCutValue).HasPrecision(18, 2);
            entity.Property(e => e.TechnicianComments).HasMaxLength(1000);

            entity.HasOne(e => e.Appointment)
                .WithMany()
                .HasForeignKey(e => e.AppointmentId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Hospital)
                .WithMany()
                .HasForeignKey(e => e.HospitalId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.ServiceCharge)
                .WithMany()
                .HasForeignKey(e => e.ServiceChargeId)
                .OnDelete(DeleteBehavior.SetNull);

            // Sync delta probe — same shape as Appointments / Invoices /
            // ReferralCommissions so the SyncEngine's per-domain
            // ?updatedAfter= queries land on a covering index.
            entity.HasIndex(e => new { e.HospitalId, e.UpdatedAt })
                .HasDatabaseName("IX_AppointmentServices_Hospital_UpdatedAt");

            entity.HasIndex(e => e.AppointmentId)
                .HasDatabaseName("IX_AppointmentServices_AppointmentId");

            // OCC token — one report per service can be written
            // independently from the others on the same visit.
            entity.Property(e => e.RowVersion).IsRowVersion();
        });

        // AppointmentComment Configuration — append-only audit trail for an
        // appointment. Cascade delete from Appointment so disposed records
        // don't leave orphan comments behind.
        modelBuilder.Entity<AppointmentComment>(entity =>
        {
            entity.ToTable("AppointmentComments", "dbo");
            entity.HasKey(e => e.AppointmentCommentId);
            entity.Property(e => e.Body).IsRequired().HasMaxLength(2000);

            entity.HasOne(e => e.Appointment)
                .WithMany()
                .HasForeignKey(e => e.AppointmentId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.AppointmentId, e.CreatedAt })
                .HasDatabaseName("IX_AppointmentComments_AppointmentId_CreatedAt");
        });

        // ServiceCharge Configuration
        modelBuilder.Entity<ServiceCharge>(entity =>
        {
            entity.ToTable("ServiceCharges", "dbo");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Modality).IsRequired().HasMaxLength(50);
            entity.Property(e => e.ServiceName).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Amount).HasPrecision(18, 2);

            entity.HasOne(e => e.Hospital)
                .WithMany()
                .HasForeignKey(e => e.HospitalId);
        });

        // Invoice Configuration
        modelBuilder.Entity<Invoice>(entity =>
        {
            entity.ToTable("Invoices", "dbo");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.InvoiceId).IsRequired().HasMaxLength(50);
            entity.Property(e => e.PatientName).IsRequired().HasMaxLength(255); 
            entity.Property(e => e.GrossAmount).HasPrecision(18, 2);
            entity.Property(e => e.DiscountAmount).HasPrecision(18, 2);
            entity.Property(e => e.TotalAmount).HasPrecision(18, 2);
            entity.Property(e => e.PaidAmount).HasPrecision(18, 2);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50);

            entity.HasOne(e => e.Patient)
                .WithMany()
                .HasForeignKey(e => e.PatientId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Appointment)
                .WithMany()
                .HasForeignKey(e => e.AppointmentId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.Hospital)
                .WithMany()
                .HasForeignKey(e => e.HospitalId);
        });

        // InvoiceItem Configuration
        modelBuilder.Entity<InvoiceItem>(entity =>
        {
            entity.ToTable("InvoiceItems", "dbo");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Description).IsRequired().HasMaxLength(500);
            entity.Property(e => e.Amount).HasPrecision(18, 2);

            entity.HasOne(e => e.Invoice)
                .WithMany(i => i.Items)
                .HasForeignKey(e => e.InvoiceId)
                .OnDelete(DeleteBehavior.Cascade);

            // Multi-service link (migration 57). Optional — legacy single-
            // service invoice items have NULL; new multi-service items
            // point at their AppointmentService. SetNull on delete keeps
            // billing history intact even if the service line is removed.
            entity.HasOne<AppointmentService>()
                .WithMany()
                .HasForeignKey(e => e.AppointmentServiceId)
                // NO ACTION (not SET NULL) — SQL Server's multi-cascade-
                // path detector blocks SET NULL because of the parallel
                // Appointment → child + Appointment → AppointmentService
                // → child paths. We soft-delete service rows via
                // DeletedAt, so the difference never matters in practice.
                .OnDelete(DeleteBehavior.NoAction);
        });

        // Payment Configuration
        modelBuilder.Entity<Payment>(entity =>
        {
            entity.ToTable("Payments", "dbo");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.Property(e => e.PaymentMethod).IsRequired().HasMaxLength(50);

            entity.HasOne(e => e.Invoice)
                .WithMany(i => i.Payments)
                .HasForeignKey(e => e.InvoiceId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Hospital)
                .WithMany()
                .HasForeignKey(e => e.HospitalId);
        });

        // CreditTransaction Configuration — patient credit-wallet ledger. PatientId
        // / InvoiceId are kept as plain Guid columns (no FK navs) so this ledger is
        // independent of cascade paths; hospital isolation comes from the global
        // IHospitalContext query filter applied below.
        modelBuilder.Entity<CreditTransaction>(entity =>
        {
            entity.ToTable("CreditTransactions", "dbo");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PatientName).HasMaxLength(255);
            entity.Property(e => e.Type).IsRequired().HasMaxLength(20);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.Property(e => e.InvoiceDisplayId).HasMaxLength(50);
            entity.Property(e => e.PaymentMethod).HasMaxLength(50);
            entity.Property(e => e.Remarks).HasMaxLength(500);
            entity.HasIndex(e => new { e.HospitalId, e.PatientId });
            entity.HasIndex(e => new { e.HospitalId, e.UpdatedAt });
        });

        // DiagnosticReport Configuration
        modelBuilder.Entity<DiagnosticReport>(entity =>
        {
            entity.ToTable("DiagnosticReports", "dbo");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Findings).IsRequired();
            entity.Property(e => e.Impression).IsRequired();
            entity.Property(e => e.ReportingMode).HasMaxLength(50).HasDefaultValue("Structured");
            entity.Property(e => e.FieldCount).HasDefaultValue(0);
            entity.Property(e => e.ReportPdfUrl).HasMaxLength(500);

            // Sign-off state machine (21 CFR Part 11). Status defaults to Draft;
            // signer fields are populated only when the report is signed.
            entity.Property(e => e.Status).HasMaxLength(20).HasDefaultValue("Draft");
            entity.Property(e => e.SignerName).HasMaxLength(200);
            entity.Property(e => e.SignerCredentials).HasMaxLength(200);
            entity.Property(e => e.SignedContentHash).HasMaxLength(64);

            entity.HasMany(e => e.Addenda)
                .WithOne(a => a.Report)
                .HasForeignKey(a => a.ReportId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Fields)
                .WithOne(f => f.Report)
                .HasForeignKey(f => f.ReportId)
                .OnDelete(DeleteBehavior.Cascade);

            // B2 Track 3 — OCC token (see Appointment for rationale).
            entity.Property(e => e.RowVersion).IsRowVersion();

            entity.HasOne(e => e.Appointment)
                .WithMany()
                .HasForeignKey(e => e.AppointmentId)
                .OnDelete(DeleteBehavior.Restrict);

            // Multi-service link (migration 57). One DiagnosticReport per
            // AppointmentService — different modalities of the same visit
            // get independent reports with independent RowVersions so
            // doctors can write them concurrently. Backfilled from the
            // 1:1 single-service assumption on existing rows.
            entity.HasOne<AppointmentService>()
                .WithMany()
                .HasForeignKey(e => e.AppointmentServiceId)
                // NO ACTION (not SET NULL) — SQL Server's multi-cascade-
                // path detector blocks SET NULL because of the parallel
                // Appointment → child + Appointment → AppointmentService
                // → child paths. We soft-delete service rows via
                // DeletedAt, so the difference never matters in practice.
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasOne(e => e.Doctor)
                .WithMany()
                .HasForeignKey(e => e.DoctorId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Hospital)
                .WithMany()
                .HasForeignKey(e => e.HospitalId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Template)
                .WithMany()
                .HasForeignKey(e => e.TemplateId)
                .OnDelete(DeleteBehavior.SetNull);

            // PACS-only reports key off an ImagingStudy instead of an
            // appointment (a report belongs to exactly one of the two). NO
            // ACTION — deleting a study is an explicit, blob-aware operation,
            // never a silent cascade through reports.
            entity.HasOne(e => e.ImagingStudy)
                .WithMany()
                .HasForeignKey(e => e.ImagingStudyId)
                .OnDelete(DeleteBehavior.NoAction);

            // One report per study — the study-based analogue of the
            // appointment upsert key. Filtered so the many appointment-based
            // rows (NULL ImagingStudyId) don't collide.
            entity.HasIndex(e => e.ImagingStudyId)
                .IsUnique()
                .HasFilter("[ImagingStudyId] IS NOT NULL");
        });

        // DiagnosticReportField Configuration
        modelBuilder.Entity<DiagnosticReportField>(entity =>
        {
            entity.ToTable("DiagnosticReportFields", "dbo");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FieldName).IsRequired().HasMaxLength(255);
            entity.Property(e => e.SectionName).HasMaxLength(255);
            entity.Property(e => e.FieldValue).IsRequired();
        });

        // ReportAddendum Configuration — append-only amendment records.
        modelBuilder.Entity<ReportAddendum>(entity =>
        {
            entity.ToTable("ReportAddenda", "dbo");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.AuthorName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.AuthorCredentials).HasMaxLength(200);
            entity.Property(e => e.Text).IsRequired();
            entity.Property(e => e.ContentHash).HasMaxLength(64);
            // The report → addenda relationship (FK, cascade) is declared on the
            // DiagnosticReport side above; don't re-declare it here.
            entity.HasIndex(e => new { e.ReportId, e.SortOrder });
        });

        // ReportAuditEvent Configuration — append-only, hash-chained audit trail.
        modelBuilder.Entity<ReportAuditEvent>(entity =>
        {
            entity.ToTable("ReportAuditEvents", "dbo");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EventType).IsRequired().HasMaxLength(40);
            entity.Property(e => e.ActorName).HasMaxLength(200);
            entity.Property(e => e.ContentHash).HasMaxLength(64);
            entity.Property(e => e.PreviousHash).HasMaxLength(64);
            // No FK constraint to DiagnosticReports: the audit trail must survive
            // even if a report row is ever hard-deleted (tamper evidence). Indexed
            // for "show this report's history, oldest first".
            entity.HasIndex(e => new { e.ReportId, e.Timestamp });
        });

        // ReportTemplate Configuration
        modelBuilder.Entity<ReportTemplate>(entity =>
        {
            entity.ToTable("ReportTemplates", "dbo");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Modality).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Content).IsRequired();



            entity.HasOne(e => e.Hospital)
                .WithMany()
                .HasForeignKey(e => e.HospitalId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ReportingKeyword Configuration
        modelBuilder.Entity<ReportingKeyword>(entity =>
        {
            entity.ToTable("ReportingKeywords", "dbo");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Trigger).IsRequired().HasMaxLength(100);
            entity.Property(e => e.ReplacementText).IsRequired();

            entity.HasOne(e => e.Doctor)
                .WithMany()
                .HasForeignKey(e => e.DoctorId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Hospital)
                .WithMany()
                .HasForeignKey(e => e.HospitalId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ImagingStudy Configuration — the PACS-side aggregate (Phase 1 of the
        // RIS/PACS SKU split). One row per DICOM study; appointment optional.
        modelBuilder.Entity<ImagingStudy>(entity =>
        {
            entity.ToTable("ImagingStudies", "dbo");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.StudyInstanceUID).HasMaxLength(128);
            entity.Property(e => e.PatientName).HasMaxLength(255);
            entity.Property(e => e.DicomPatientId).HasMaxLength(128);
            entity.Property(e => e.Modality).HasMaxLength(32);
            entity.Property(e => e.StudyDescription).HasMaxLength(255);
            entity.Property(e => e.AccessionNumber).HasMaxLength(64);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20)
                .HasDefaultValue(ImagingStudyStatus.Received);
            entity.Property(e => e.MatchStatus).IsRequired().HasMaxLength(20)
                .HasDefaultValue(ImagingStudyMatchStatus.Unmatched);
            entity.Property(e => e.Source).HasMaxLength(32);

            // DICOM identity: unique per tenant when known. Filtered so the
            // many legacy/unparsed rows with NULL UID don't collide.
            entity.HasIndex(e => new { e.HospitalId, e.StudyInstanceUID })
                .IsUnique()
                .HasFilter("[StudyInstanceUID] IS NOT NULL");

            entity.HasOne(e => e.Appointment)
                .WithMany()
                .HasForeignKey(e => e.AppointmentId)
                // Keep the study when a visit is deleted — the archive is the
                // PACS product; orphaning the link is correct, deleting PHI
                // imaging as a side effect of RIS cleanup is not.
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.Patient)
                .WithMany()
                .HasForeignKey(e => e.PatientId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasIndex(e => e.AppointmentId).HasFilter("[AppointmentId] IS NOT NULL");
            // Study-browser worklist: newest studies for a hospital.
            entity.HasIndex(e => new { e.HospitalId, e.CreatedAt });
            // PACS-only inbox slice: unassigned studies for a hospital.
            entity.HasIndex(e => new { e.HospitalId, e.MatchStatus });
            // Accession lookup for server-side matching / reconciliation.
            entity.HasIndex(e => new { e.HospitalId, e.AccessionNumber })
                .HasFilter("[AccessionNumber] IS NOT NULL");
        });

        // StudyAsset Configuration
        modelBuilder.Entity<StudyAsset>(entity =>
        {
            entity.ToTable("StudyAssets", "dbo");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.BlobUrl).IsRequired().HasMaxLength(500);
            entity.Property(e => e.FileName).IsRequired().HasMaxLength(500);
            entity.Property(e => e.FileType).IsRequired().HasMaxLength(50);
            entity.Property(e => e.ExtractionStatus).HasMaxLength(20);
            entity.Property(e => e.ExtractionError).HasMaxLength(2000);

            // Optional since the RIS/PACS SKU split (Phase 1) — PACS-only
            // assets carry only the ImagingStudy link.
            entity.HasOne(e => e.Appointment)
                .WithMany(a => a.StudyAssets)
                .HasForeignKey(e => e.AppointmentId)
                .OnDelete(DeleteBehavior.Cascade);

            // The imaging aggregate. NO ACTION: deleting a study must be an
            // explicit operation that handles blobs + slices, never a silent
            // cascade (and SQL Server would reject another cascade path here
            // anyway, alongside Appointment → StudyAssets).
            entity.HasOne(e => e.ImagingStudy)
                .WithMany(s => s.Assets)
                .HasForeignKey(e => e.ImagingStudyId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasIndex(e => e.ImagingStudyId).HasFilter("[ImagingStudyId] IS NOT NULL");

            // Multi-service link (migration 57). For a multi-modality visit
            // (X-ray + CT) each acquisition's assets route to the right
            // service line so the right report opens against the right
            // images. NULL on legacy single-service rows.
            entity.HasOne<AppointmentService>()
                .WithMany()
                .HasForeignKey(e => e.AppointmentServiceId)
                // NO ACTION (not SET NULL) — SQL Server's multi-cascade-
                // path detector blocks SET NULL because of the parallel
                // Appointment → child + Appointment → AppointmentService
                // → child paths. We soft-delete service rows via
                // DeletedAt, so the difference never matters in practice.
                .OnDelete(DeleteBehavior.NoAction);

            // Filtered index — fast lookup for the extraction worker.
            entity.HasIndex(e => e.ExtractionStatus).HasFilter("[ExtractionStatus] IS NOT NULL");
        });

        // StudySliceIndex Configuration (Option C — per-slice viewer manifest)
        modelBuilder.Entity<StudySliceIndex>(entity =>
        {
            entity.ToTable("StudySliceIndexes", "dbo");
            entity.HasKey(e => e.SliceId);
            entity.Property(e => e.SeriesUID).IsRequired().HasMaxLength(128);
            entity.Property(e => e.SopInstanceUID).IsRequired().HasMaxLength(128);
            entity.Property(e => e.BlobUrl).IsRequired().HasMaxLength(700);
            entity.Property(e => e.BlobPath).IsRequired().HasMaxLength(700);
            entity.Property(e => e.SeriesDescription).HasMaxLength(200);
            entity.Property(e => e.Modality).HasMaxLength(16);
            entity.Property(e => e.ThumbnailUrl).HasMaxLength(700);

            entity.HasOne(e => e.Asset)
                .WithMany(a => a.Slices)
                .HasForeignKey(e => e.AssetId)
                .OnDelete(DeleteBehavior.Cascade);

            // Primary read path: manifest endpoint groups by AssetId/SeriesUID/InstanceNumber.
            entity.HasIndex(e => new { e.AssetId, e.SeriesUID, e.InstanceNumber });
            entity.HasIndex(e => e.AppointmentId);
            entity.HasIndex(e => e.HospitalId);
        });

        // PrescriptionProtocol Configuration
        modelBuilder.Entity<PrescriptionProtocol>(entity =>
        {
            entity.ToTable("PrescriptionProtocols", "dbo");
            entity.HasKey(e => e.Id);
            
            entity.Property(e => e.HeaderMargin).HasPrecision(18, 2);
            entity.Property(e => e.LeftMargin).HasPrecision(18, 2);
            entity.Property(e => e.RightMargin).HasPrecision(18, 2);
            entity.Property(e => e.BottomMargin).HasPrecision(18, 2);
            
            entity.Property(e => e.FontColor).HasMaxLength(50);
            entity.Property(e => e.FontFamily).HasMaxLength(100);
            entity.Property(e => e.LetterheadBlobUrl).HasMaxLength(500);
            entity.Property(e => e.OverflowBackgroundMode).HasMaxLength(50).HasDefaultValue("REUSE");

            entity.HasOne(e => e.Doctor)
                .WithMany()
                .HasForeignKey(e => e.DoctorId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Hospital)
                .WithMany()
                .HasForeignKey(e => e.HospitalId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Expense Configuration
        modelBuilder.Entity<Expense>(entity =>
        {
            entity.ToTable("Expenses", "dbo");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Description).IsRequired();
            entity.Property(e => e.Category).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.Property(e => e.TaxAmount).HasPrecision(18, 2);
            entity.Property(e => e.PaymentMode).HasMaxLength(50);
            entity.Property(e => e.ReferenceNumber).HasMaxLength(100);
            entity.Property(e => e.VendorName).HasMaxLength(200);
            entity.Property(e => e.CostCenter).HasMaxLength(100);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50).HasDefaultValue("Paid");
            entity.Property(e => e.TransactionDate).IsRequired();

            entity.HasOne(e => e.Hospital)
                .WithMany()
                .HasForeignKey(e => e.HospitalId);

            entity.HasOne(e => e.LinkedDisbursement)
                .WithMany()
                .HasForeignKey(e => e.LinkedDisbursementId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.LinkedDisbursementId);
        });

        // ReferralCommission Configuration
        modelBuilder.Entity<ReferralCommission>(entity =>
        {
            entity.ToTable("ReferralCommissions", "dbo");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ReferrerName).IsRequired().HasMaxLength(255); 
            // entity.Property(e => e.PatientName).IsRequired().HasMaxLength(255); 
            entity.Property(e => e.CommissionAmount).HasPrecision(18, 2);
            entity.Property(e => e.AccumulatedTotal).HasPrecision(18, 2);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50).HasDefaultValue("Pending");
            entity.Property(e => e.ReferenceNumber).HasMaxLength(100);
            entity.Property(e => e.Modality).HasMaxLength(50);

            entity.HasOne(e => e.Referrer)
                .WithMany(r => r.Commissions)
                .HasForeignKey(e => e.ReferrerId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Hospital)
                .WithMany()
                .HasForeignKey(e => e.HospitalId);

            // Multi-service link (migration 57). One commission row per
            // AppointmentService line so per-modality referral cuts work
            // (USG and CT may have different ReferralCutValues). NULL on
            // legacy single-service appointments — those still resolve
            // their commission via AppointmentId.
            entity.HasOne<AppointmentService>()
                .WithMany()
                .HasForeignKey(e => e.AppointmentServiceId)
                // NO ACTION (not SET NULL) — SQL Server's multi-cascade-
                // path detector blocks SET NULL because of the parallel
                // Appointment → child + Appointment → AppointmentService
                // → child paths. We soft-delete service rows via
                // DeletedAt, so the difference never matters in practice.
                .OnDelete(DeleteBehavior.NoAction);
        });

        // SubscriptionPlan Configuration
        modelBuilder.Entity<SubscriptionPlan>(entity =>
        {
            entity.ToTable("SubscriptionPlans", "dbo");
            entity.HasKey(e => e.PlanId);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Price).HasPrecision(18, 2);
            entity.Property(e => e.DiscountPercentage).HasPrecision(18, 2);
            entity.Property(e => e.Edition).IsRequired().HasMaxLength(20).HasDefaultValue("RIS+PACS");
            entity.Property(e => e.Modules).IsRequired().HasMaxLength(50).HasDefaultValue(ModuleConstants.DefaultModules);
            entity.Property(e => e.PerGbOveragePrice).HasPrecision(18, 2);
            entity.Property(e => e.Tier).IsRequired().HasMaxLength(20).HasDefaultValue("Starter");
            entity.Property(e => e.BillingMode).IsRequired().HasMaxLength(20).HasDefaultValue("Subscription");
            entity.Property(e => e.PerStudyPrice).HasPrecision(18, 2);
        });

        // HospitalSubscription Configuration
        modelBuilder.Entity<HospitalSubscription>(entity =>
        {
            entity.ToTable("HospitalSubscriptions", "dbo");
            entity.HasKey(e => e.SubscriptionId);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50);
            entity.Property(e => e.BillingCycle).IsRequired().HasMaxLength(20).HasDefaultValue("Trial");
            entity.Property(e => e.BillingMode).IsRequired().HasMaxLength(20).HasDefaultValue("Subscription");
            entity.Property(e => e.PerStudyPrice).HasPrecision(18, 2);
            entity.Property(e => e.LockReason).HasMaxLength(100);
            // SQL default covers legacy rows (backfilled by the migration) and
            // any insert path that doesn't set Modules explicitly.
            entity.Property(e => e.Modules).IsRequired().HasMaxLength(200)
                .HasDefaultValue(Domain.Constants.ModuleConstants.DefaultModules);

            entity.HasOne(e => e.Hospital)
                .WithMany(h => h.Subscriptions)
                .HasForeignKey(e => e.HospitalId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Plan)
                .WithMany()
                .HasForeignKey(e => e.PlanId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => new { e.HospitalId, e.Status });
        });

        // SubscriptionPaymentRequest Configuration
        modelBuilder.Entity<SubscriptionPaymentRequest>(entity =>
        {
            entity.ToTable("SubscriptionPaymentRequests", "dbo");
            entity.HasKey(e => e.RequestId);
            entity.Property(e => e.PlanName).IsRequired().HasMaxLength(50);
            entity.Property(e => e.BillingCycle).IsRequired().HasMaxLength(20);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.Property(e => e.PayerName).IsRequired().HasMaxLength(255);
            entity.Property(e => e.PayerContact).IsRequired().HasMaxLength(100);
            entity.Property(e => e.TransactionReference).IsRequired().HasMaxLength(255);
            entity.Property(e => e.PaymentMode).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20).HasDefaultValue("Pending");
            entity.Property(e => e.ReviewNote).HasMaxLength(500);
            entity.Property(e => e.PaymentGatewayOrderId).HasMaxLength(255);
            entity.Property(e => e.PaymentGatewayResponse).HasMaxLength(2000);

            entity.HasOne(e => e.Hospital)
                .WithMany()
                .HasForeignKey(e => e.HospitalId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.HospitalId, e.Status });
        });

        // StaffMember Configuration
        modelBuilder.Entity<StaffMember>(entity =>
        {
            entity.ToTable("StaffMembers", "dbo");
            entity.HasKey(e => e.StaffId);
            entity.Property(e => e.EmployeeCode).HasMaxLength(20);
            entity.Property(e => e.PhotoUrl).HasMaxLength(2000);
            entity.Property(e => e.PhotoPath).HasMaxLength(500);
            entity.HasIndex(e => new { e.HospitalId, e.EmployeeCode }).IsUnique().HasFilter("[EmployeeCode] IS NOT NULL");
            entity.Property(e => e.FullName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Email).HasMaxLength(200);
            entity.Property(e => e.Mobile).HasMaxLength(30);
            entity.Property(e => e.Designation).HasMaxLength(200);
            entity.Property(e => e.Department).HasMaxLength(200);
            entity.Property(e => e.EmploymentType).IsRequired().HasMaxLength(50).HasDefaultValue("Full-Time");
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50).HasDefaultValue("Active");

            entity.HasOne(e => e.Hospital)
                .WithMany()
                .HasForeignKey(e => e.HospitalId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Roles)
                .WithOne(r => r.StaffMember)
                .HasForeignKey(r => r.StaffId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Documents)
                .WithOne(d => d.StaffMember)
                .HasForeignKey(d => d.StaffId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // StaffMemberRole Configuration
        modelBuilder.Entity<StaffMemberRole>(entity =>
        {
            entity.ToTable("StaffMemberRoles", "dbo");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.RoleName).IsRequired().HasMaxLength(100);
            entity.HasIndex(e => new { e.StaffId, e.RoleName }).IsUnique();
        });

        // StaffDocument Configuration
        modelBuilder.Entity<StaffDocument>(entity =>
        {
            entity.ToTable("StaffDocuments", "dbo");
            entity.HasKey(e => e.DocumentId);
            entity.Property(e => e.Category).HasMaxLength(100).HasDefaultValue("Other");
            entity.Property(e => e.FileName).IsRequired().HasMaxLength(500);
            entity.Property(e => e.ContentType).HasMaxLength(200);
            entity.Property(e => e.VerificationStatus).HasMaxLength(50).HasDefaultValue("Pending");
            entity.Property(e => e.BlobUrl).HasMaxLength(2000);
            entity.Property(e => e.BlobPath).HasMaxLength(500);
            entity.Property(e => e.BlobContainer).HasMaxLength(100);
        });

        // SalaryRevision Configuration
        modelBuilder.Entity<SalaryRevision>(entity =>
        {
            entity.ToTable("SalaryRevisions", "dbo");
            entity.HasKey(e => e.RevisionId);
            entity.Property(e => e.BasicPay).HasColumnType("decimal(12,2)");
            entity.Property(e => e.Hra).HasColumnType("decimal(12,2)");
            entity.Property(e => e.Travel).HasColumnType("decimal(12,2)");
            entity.Property(e => e.OtherAllowances).HasColumnType("decimal(12,2)");
            entity.Property(e => e.PfDeduction).HasColumnType("decimal(12,2)");
            entity.Property(e => e.Tds).HasColumnType("decimal(12,2)");
            entity.Property(e => e.OtherDeductions).HasColumnType("decimal(12,2)");
            entity.Property(e => e.Note).HasMaxLength(500);

            entity.HasOne(e => e.StaffMember)
                .WithMany(s => s.SalaryRevisions)
                .HasForeignKey(e => e.StaffId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.StaffId, e.EffectiveFrom });
            entity.HasIndex(e => e.HospitalId);
        });

        // SalaryDisbursement Configuration
        modelBuilder.Entity<SalaryDisbursement>(entity =>
        {
            entity.ToTable("SalaryDisbursements", "dbo");
            entity.HasKey(e => e.DisbursementId);
            entity.Property(e => e.Month).IsRequired().HasMaxLength(7); // "YYYY-MM"
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20).HasDefaultValue("Draft");
            entity.Property(e => e.GrossPay).HasColumnType("decimal(12,2)");
            entity.Property(e => e.NetPay).HasColumnType("decimal(12,2)");
            entity.Property(e => e.StructureGross).HasColumnType("decimal(12,2)");
            entity.Property(e => e.StructureNet).HasColumnType("decimal(12,2)");
            entity.Property(e => e.LwpDays).HasColumnType("decimal(5,2)");
            entity.Property(e => e.LwpDeduction).HasColumnType("decimal(12,2)");
            entity.Property(e => e.PerDayRate).HasColumnType("decimal(12,2)");
            entity.Property(e => e.PaymentMode).IsRequired().HasMaxLength(20).HasDefaultValue("bank");
            entity.Property(e => e.Reference).HasMaxLength(200);
            entity.Property(e => e.Notes).HasMaxLength(1000);
            entity.Property(e => e.AttendanceJson).HasMaxLength(2000);

            entity.HasOne(e => e.StaffMember)
                .WithMany(s => s.SalaryDisbursements)
                .HasForeignKey(e => e.StaffId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Revision)
                .WithMany()
                .HasForeignKey(e => e.RevisionId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => new { e.StaffId, e.Month }).IsUnique();
            entity.HasIndex(e => e.HospitalId);
        });

        // HospitalLeavePolicy Configuration
        modelBuilder.Entity<HospitalLeavePolicy>(entity =>
        {
            entity.ToTable("HospitalLeavePolicies", "dbo");
            entity.HasKey(e => e.PolicyId);
            entity.Property(e => e.LeaveTypesJson).IsRequired().HasMaxLength(4000).HasDefaultValue("[]");

            entity.HasOne(e => e.Hospital)
                .WithMany()
                .HasForeignKey(e => e.HospitalId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.HospitalId).IsUnique();
        });

        // StaffAttendance Configuration
        modelBuilder.Entity<StaffAttendance>(entity =>
        {
            entity.ToTable("StaffAttendance", "dbo");
            entity.HasKey(e => e.AttendanceId);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20).HasDefaultValue("present");
            entity.Property(e => e.Note).HasMaxLength(500);

            entity.HasOne(e => e.StaffMember)
                .WithMany(s => s.Attendances)
                .HasForeignKey(e => e.StaffId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.StaffId, e.AttendanceDate }).IsUnique();
            entity.HasIndex(e => new { e.HospitalId, e.AttendanceDate });
        });

        // StaffLeaveRequest Configuration
        modelBuilder.Entity<StaffLeaveRequest>(entity =>
        {
            entity.ToTable("StaffLeaveRequests", "dbo");
            entity.HasKey(e => e.LeaveRequestId);
            entity.Property(e => e.LeaveType).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Reason).HasMaxLength(1000);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20).HasDefaultValue("pending");

            entity.HasOne(e => e.StaffMember)
                .WithMany(s => s.LeaveRequests)
                .HasForeignKey(e => e.StaffId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.StaffId);
            entity.HasIndex(e => e.HospitalId);
            entity.HasIndex(e => e.SourceDisbursementId);
        });

        // Seed Subscription Plans — the full catalog: each edition × tier ×
        // {Monthly,Yearly} subscription plan, plus a PAYG (per-study) and a Chain
        // (custom) plan per edition. PRICES / allowances / caps are PLACEHOLDERS
        // the business adjusts. PlanIds are deterministic (stable) from the
        // natural key, so the seed is repeatable. (Prod keeps any legacy plan
        // GUIDs via the hand-written SQL migration's natural-key merge.)
        static Guid PlanGuid(string key)
        {
            using var md5 = System.Security.Cryptography.MD5.Create();
            return new Guid(md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes("plan|" + key)));
        }

        var editions = new[]
        {
            (Edition: "RIS", Modules: "RIS", PerGb: 0m, Payg: 8m, Tiers: new[]
            {
                (Tier: "Starter", M: 1999m,  Gb: (int?)null, U: (int?)2,  S: (int?)1),
                (Tier: "Growth",  M: 4999m,  Gb: (int?)null, U: (int?)5,  S: (int?)1),
                (Tier: "Clinic",  M: 9999m,  Gb: (int?)null, U: (int?)10, S: (int?)3),
            }),
            (Edition: "PACS", Modules: "PACS", PerGb: 50m, Payg: 15m, Tiers: new[]
            {
                (Tier: "Starter", M: 2999m,  Gb: (int?)100,  U: (int?)5,  S: (int?)1),
                (Tier: "Growth",  M: 6999m,  Gb: (int?)500,  U: (int?)10, S: (int?)1),
                (Tier: "Clinic",  M: 14999m, Gb: (int?)1024, U: (int?)20, S: (int?)3),
            }),
            (Edition: "RIS+PACS", Modules: "RIS,PACS", PerGb: 50m, Payg: 25m, Tiers: new[]
            {
                (Tier: "Starter", M: 3999m,  Gb: (int?)100,  U: (int?)2,  S: (int?)1),
                (Tier: "Growth",  M: 9999m,  Gb: (int?)500,  U: (int?)5,  S: (int?)1),
                (Tier: "Clinic",  M: 19999m, Gb: (int?)1024, U: (int?)10, S: (int?)3),
            }),
        };

        var seedPlans = new List<SubscriptionPlan>();
        foreach (var e in editions)
        {
            foreach (var t in e.Tiers)
            {
                seedPlans.Add(new SubscriptionPlan
                {
                    PlanId = PlanGuid($"{e.Edition}|{t.Tier}|Monthly"),
                    Name = "Monthly", Edition = e.Edition, Modules = e.Modules, Tier = t.Tier,
                    Price = t.M, DurationInDays = 30, DiscountPercentage = 0, PerAdditionalDoctorPrice = 1000,
                    IncludedStorageGb = t.Gb, PerGbOveragePrice = e.PerGb,
                    BillingMode = "Subscription", PerStudyPrice = 0, MaxUsers = t.U, MaxSites = t.S,
                });
                seedPlans.Add(new SubscriptionPlan
                {
                    PlanId = PlanGuid($"{e.Edition}|{t.Tier}|Yearly"),
                    Name = "Yearly", Edition = e.Edition, Modules = e.Modules, Tier = t.Tier,
                    Price = Math.Round(t.M * 12 * 0.9m, 0), DurationInDays = 365, DiscountPercentage = 10, PerAdditionalDoctorPrice = 10800,
                    IncludedStorageGb = t.Gb, PerGbOveragePrice = e.PerGb,
                    BillingMode = "Subscription", PerStudyPrice = 0, MaxUsers = t.U, MaxSites = t.S,
                });
            }
            // Pay-as-you-go (monthly arrears, no base, no caps).
            seedPlans.Add(new SubscriptionPlan
            {
                PlanId = PlanGuid($"{e.Edition}|PAYG"),
                Name = "PAYG", Edition = e.Edition, Modules = e.Modules, Tier = "PAYG",
                Price = 0, DurationInDays = 30, DiscountPercentage = 0, PerAdditionalDoctorPrice = 0,
                IncludedStorageGb = null, PerGbOveragePrice = 0,
                BillingMode = "PerStudy", PerStudyPrice = e.Payg, MaxUsers = null, MaxSites = null,
            });
            // Chain / enterprise — bespoke, not self-serve.
            seedPlans.Add(new SubscriptionPlan
            {
                PlanId = PlanGuid($"{e.Edition}|Chain"),
                Name = "Custom", Edition = e.Edition, Modules = e.Modules, Tier = "Chain",
                Price = 0, DurationInDays = 30, DiscountPercentage = 0, PerAdditionalDoctorPrice = 0,
                IncludedStorageGb = null, PerGbOveragePrice = e.PerGb,
                BillingMode = "Subscription", PerStudyPrice = 0, MaxUsers = null, MaxSites = null, IsCustom = true,
            });
        }
        modelBuilder.Entity<SubscriptionPlan>().HasData(seedPlans);



        modelBuilder.Entity<CustomRole>(entity =>
        {
            entity.ToTable("CustomRoles", "dbo");
            entity.HasKey(e => e.CustomRoleId);
            entity.Property(e => e.RoleName).IsRequired().HasMaxLength(100);
            entity.HasIndex(e => new { e.HospitalId, e.RoleName }).IsUnique();

            entity.HasOne(e => e.Hospital)
                .WithMany()
                .HasForeignKey(e => e.HospitalId);
        });

        modelBuilder.Entity<CustomRolePermission>(entity =>
        {
            entity.ToTable("CustomRolePermissions", "dbo");
            entity.HasKey(e => new { e.CustomRoleId, e.RoutePath });
            entity.Property(e => e.RoutePath).HasMaxLength(255);

            entity.HasOne(e => e.CustomRole)
                .WithMany(cr => cr.Permissions)
                .HasForeignKey(e => e.CustomRoleId);
        });

        modelBuilder.Entity<UserHospitalMapping>()
            .HasMany(e => e.CustomRoles)
            .WithMany(cr => cr.UserHospitalMappings)
            .UsingEntity<Dictionary<string, object>>(
                "UserHospitalCustomRole",
                j => j.HasOne<CustomRole>().WithMany().HasForeignKey("CustomRoleId"),
                j => j.HasOne<UserHospitalMapping>().WithMany().HasForeignKey("MappingId"),
                j =>
                {
                    j.ToTable("UserHospitalCustomRoles", "dbo");
                    j.HasKey("MappingId", "CustomRoleId");
                });

        // Tactical Global Query Filters for Multi-Facility Isolation
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (typeof(IHospitalContext).IsAssignableFrom(entityType.ClrType))
            {
                // Dynamic equivalent of: HasQueryFilter(e => e.HospitalId == UserContext.HospitalId)
                var parameter = System.Linq.Expressions.Expression.Parameter(entityType.ClrType, "e");
                
                var left = System.Linq.Expressions.Expression.Property(parameter, nameof(IHospitalContext.HospitalId));
                
                var userContextProperty = System.Linq.Expressions.Expression.Property(System.Linq.Expressions.Expression.Constant(this), nameof(UserContext));
                var right = System.Linq.Expressions.Expression.Property(userContextProperty, nameof(IUserContext.HospitalId));
                
                var body = System.Linq.Expressions.Expression.Equal(left, right);

                var lambda = System.Linq.Expressions.Expression.Lambda(body, parameter);
                modelBuilder.Entity(entityType.ClrType).HasQueryFilter(lambda);
            }
        }
    }

    // See IApplicationDbContext.NextSequenceValueAsync. Atomicity comes from
    // running the whole "increment, or create from seed" as one server-side
    // batch in an explicit transaction with UPDLOCK + HOLDLOCK on the counter
    // key. HOLDLOCK takes a key-range lock, so a concurrent caller for the
    // SAME key blocks on the UPDATE until we commit — it can neither read a
    // stale value nor insert a duplicate row. Net effect: every caller walks
    // away with a distinct, gap-free number.
    public async Task<int> NextSequenceValueAsync(Guid hospitalId, string counterKey, int seedIfAbsent, CancellationToken cancellationToken)
    {
        const string sql = @"
SET NOCOUNT ON;
DECLARE @v INT;
BEGIN TRANSACTION;
    UPDATE dbo.SequenceCounters WITH (UPDLOCK, HOLDLOCK)
       SET @v = CounterValue = CounterValue + 1
     WHERE HospitalId = @h AND CounterKey = @k;
    IF @@ROWCOUNT = 0
    BEGIN
        SET @v = @seed;
        INSERT INTO dbo.SequenceCounters (HospitalId, CounterKey, CounterValue)
        VALUES (@h, @k, @seed);
    END
COMMIT TRANSACTION;
SELECT @v;";

        var conn = Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        // Attach an ambient EF transaction if one happens to be open (none is
        // during the booking handler's read phase, but stay correct).
        var ambient = Database.CurrentTransaction?.GetDbTransaction();
        if (ambient != null) cmd.Transaction = ambient;

        DbParameter Param(string name, object value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            return p;
        }
        cmd.Parameters.Add(Param("@h", hospitalId));
        cmd.Parameters.Add(Param("@k", counterKey));
        cmd.Parameters.Add(Param("@seed", seedIfAbsent));

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    // See IApplicationDbContext.ClaimNextExtractionJobAsync. The CTE selects the
    // OLDEST ready job (FIFO) under READPAST/UPDLOCK/ROWLOCK — READPAST makes a
    // concurrent claimer on another instance SKIP a row this one is locking, so
    // N instances pull DISTINCT jobs with zero contention and nothing is
    // processed twice. "Ready" = a Queued row past its backoff gate, OR a Running
    // row whose lease expired (its owner crashed). The single UPDATE flips it to
    // Running + stamps a fresh lease and returns the id.
    public async Task<Guid?> ClaimNextExtractionJobAsync(string owner, int leaseSeconds, CancellationToken cancellationToken)
    {
        const string sql = @"
SET NOCOUNT ON;
DECLARE @claimed TABLE (Id UNIQUEIDENTIFIER);
WITH nxt AS (
    SELECT TOP (1) Id, ExtractionStatus, ExtractionLeaseOwner, ExtractionLeaseUntil, ExtractionStartedAt
      FROM dbo.StudyAssets WITH (READPAST, UPDLOCK, ROWLOCK)
     WHERE FileType IN ('zip','instances','dcm','dicom')
       AND (
             (ExtractionStatus = 'Queued'
                AND (ExtractionNextAttemptAt IS NULL OR ExtractionNextAttemptAt <= SYSUTCDATETIME()))
             OR
             -- Running with an expired (or legacy NULL) lease = its owner died → reclaim.
             (ExtractionStatus = 'Running'
                AND (ExtractionLeaseUntil IS NULL OR ExtractionLeaseUntil < SYSUTCDATETIME()))
           )
     ORDER BY UploadedAt ASC
)
UPDATE nxt
   SET ExtractionStatus     = 'Running',
       ExtractionLeaseOwner = @owner,
       ExtractionLeaseUntil = DATEADD(SECOND, @lease, SYSUTCDATETIME()),
       ExtractionStartedAt  = COALESCE(ExtractionStartedAt, SYSUTCDATETIME())
OUTPUT inserted.Id INTO @claimed;
SELECT Id FROM @claimed;";

        var conn = Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var ambient = Database.CurrentTransaction?.GetDbTransaction();
        if (ambient != null) cmd.Transaction = ambient;

        DbParameter P(string n, object v) { var p = cmd.CreateParameter(); p.ParameterName = n; p.Value = v; return p; }
        cmd.Parameters.Add(P("@owner", owner));
        cmd.Parameters.Add(P("@lease", leaseSeconds));

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is Guid g ? g : (Guid?)null;
    }

    // See IApplicationDbContext.RenewExtractionLeaseAsync. Only extends the lease
    // if THIS instance still owns it and the job is still Running — a no-op if it
    // was already reclaimed/finished, which is the safe outcome.
    public async Task RenewExtractionLeaseAsync(Guid assetId, string owner, int leaseSeconds, CancellationToken cancellationToken)
    {
        const string sql = @"
UPDATE dbo.StudyAssets
   SET ExtractionLeaseUntil = DATEADD(SECOND, @lease, SYSUTCDATETIME())
 WHERE Id = @id AND ExtractionLeaseOwner = @owner AND ExtractionStatus = 'Running';";

        var conn = Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var ambient = Database.CurrentTransaction?.GetDbTransaction();
        if (ambient != null) cmd.Transaction = ambient;

        DbParameter P(string n, object v) { var p = cmd.CreateParameter(); p.ParameterName = n; p.Value = v; return p; }
        cmd.Parameters.Add(P("@id", assetId));
        cmd.Parameters.Add(P("@owner", owner));
        cmd.Parameters.Add(P("@lease", leaseSeconds));

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        // Sync foundations: keep Appointment.UpdatedAt fresh on every write
        // automatically. Without this hook each handler would have to set
        // it manually and one missed handler would silently break the
        // delta-fetch sync engine (rows would never appear "changed").
        // Done before base.SaveChangesAsync so the new value persists in
        // the same transaction.
        var nowUtc = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<Appointment>())
        {
            if (entry.State == EntityState.Added || entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = nowUtc;
            }
        }
        foreach (var entry in ChangeTracker.Entries<AppointmentService>())
        {
            if (entry.State == EntityState.Added || entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = nowUtc;
            }
        }
        foreach (var entry in ChangeTracker.Entries<Patient>())
        {
            if (entry.State == EntityState.Added || entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = nowUtc;
            }
        }
        foreach (var entry in ChangeTracker.Entries<DiagnosticReport>())
        {
            if (entry.State == EntityState.Added || entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = nowUtc;
            }
        }
        foreach (var entry in ChangeTracker.Entries<Invoice>())
        {
            if (entry.State == EntityState.Added || entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = nowUtc;
            }
        }
        foreach (var entry in ChangeTracker.Entries<Expense>())
        {
            if (entry.State == EntityState.Added || entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = nowUtc;
            }
        }
        foreach (var entry in ChangeTracker.Entries<Referrer>())
        {
            if (entry.State == EntityState.Added || entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = nowUtc;
            }
        }
        foreach (var entry in ChangeTracker.Entries<ReferralCommission>())
        {
            if (entry.State == EntityState.Added || entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = nowUtc;
            }
        }
        foreach (var entry in ChangeTracker.Entries<CreditTransaction>())
        {
            if (entry.State == EntityState.Added || entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = nowUtc;
            }
        }

        var result = await base.SaveChangesAsync(cancellationToken);

        await DispatchDomainEvents(cancellationToken);

        return result;
    }

    private async Task DispatchDomainEvents(CancellationToken cancellationToken)
    {
        var domainEntities = ChangeTracker
            .Entries<BaseEntity>()
            .Where(x => x.Entity.DomainEvents.Any());

        var domainEvents = domainEntities
            .SelectMany(x => x.Entity.DomainEvents)
            .ToList();

        domainEntities.ToList()
            .ForEach(entity => entity.Entity.ClearDomainEvents());

        foreach (var domainEvent in domainEvents)
        {
            await _publisher.Publish(domainEvent, cancellationToken);
        }
    }
}
