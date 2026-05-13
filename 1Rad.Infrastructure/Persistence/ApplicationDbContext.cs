using _1Rad.Application.Interfaces;
using _1Rad.Domain.Common;
using _1Rad.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

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
    public DbSet<OTPVerification> OTPVerifications => Set<OTPVerification>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<Referrer> Referrers => Set<Referrer>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<ServiceCharge> ServiceCharges => Set<ServiceCharge>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<DiagnosticReport> DiagnosticReports => Set<DiagnosticReport>();
    public DbSet<ReportTemplate> ReportTemplates => Set<ReportTemplate>();
    public DbSet<ReportingKeyword> ReportingKeywords => Set<ReportingKeyword>();
    public DbSet<DiagnosticReportField> DiagnosticReportFields => Set<DiagnosticReportField>();
    public DbSet<StudyAsset> StudyAssets => Set<StudyAsset>();
    public DbSet<PrescriptionProtocol> PrescriptionProtocols => Set<PrescriptionProtocol>();
    public DbSet<ReferralCommission> ReferralCommissions => Set<ReferralCommission>();
    public DbSet<SubscriptionPlan> SubscriptionPlans => Set<SubscriptionPlan>();
    public DbSet<HospitalSubscription> HospitalSubscriptions => Set<HospitalSubscription>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

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

            entity.HasMany(e => e.Fields)
                .WithOne(f => f.Report)
                .HasForeignKey(f => f.ReportId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Appointment)
                .WithMany()
                .HasForeignKey(e => e.AppointmentId)
                .OnDelete(DeleteBehavior.Restrict);

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

        // ReportTemplate Configuration
        modelBuilder.Entity<ReportTemplate>(entity =>
        {
            entity.ToTable("ReportTemplates", "dbo");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Modality).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Content).IsRequired();

            entity.HasOne(e => e.Doctor)
                .WithMany()
                .HasForeignKey(e => e.DoctorId)
                .OnDelete(DeleteBehavior.SetNull);

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

        // StudyAsset Configuration
        modelBuilder.Entity<StudyAsset>(entity =>
        {
            entity.ToTable("StudyAssets", "dbo");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.BlobUrl).IsRequired().HasMaxLength(500);
            entity.Property(e => e.FileName).IsRequired().HasMaxLength(500);
            entity.Property(e => e.FileType).IsRequired().HasMaxLength(50);

            entity.HasOne(e => e.Appointment)
                .WithMany(a => a.StudyAssets)
                .HasForeignKey(e => e.AppointmentId)
                .OnDelete(DeleteBehavior.Cascade);
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
        });

        // ReferralCommission Configuration
        modelBuilder.Entity<ReferralCommission>(entity =>
        {
            entity.ToTable("ReferralCommissions", "dbo");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ReferrerName).IsRequired().HasMaxLength(255); 
            entity.Property(e => e.PatientName).IsRequired().HasMaxLength(255); 
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
        });

        // SubscriptionPlan Configuration
        modelBuilder.Entity<SubscriptionPlan>(entity =>
        {
            entity.ToTable("SubscriptionPlans", "dbo");
            entity.HasKey(e => e.PlanId);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Price).HasPrecision(18, 2);
            entity.Property(e => e.DiscountPercentage).HasPrecision(18, 2);
        });

        // HospitalSubscription Configuration
        modelBuilder.Entity<HospitalSubscription>(entity =>
        {
            entity.ToTable("HospitalSubscriptions", "dbo");
            entity.HasKey(e => e.SubscriptionId);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50);

            entity.HasOne(e => e.Hospital)
                .WithMany(h => h.Subscriptions)
                .HasForeignKey(e => e.HospitalId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Plan)
                .WithMany()
                .HasForeignKey(e => e.PlanId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Seed Subscription Plans
        modelBuilder.Entity<SubscriptionPlan>().HasData(
            new SubscriptionPlan 
            { 
                PlanId = Guid.Parse("A1B2C3D4-E5F6-4A5B-8C9D-0E1F2A3B4C5D"), 
                Name = "Monthly", 
                Price = 4999, 
                DurationInDays = 30, 
                DiscountPercentage = 0,
                PerAdditionalDoctorPrice = 1000
            },
            new SubscriptionPlan 
            { 
                PlanId = Guid.Parse("B2C3D4E5-F6A7-4B6C-9D0E-1F2A3B4C5D6E"), 
                Name = "Yearly", 
                Price = 59988, 
                DurationInDays = 365, 
                DiscountPercentage = 10,
                PerAdditionalDoctorPrice = 10800 // 1000 * 12 * 0.9
            }
        );



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

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
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
