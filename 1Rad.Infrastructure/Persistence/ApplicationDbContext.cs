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
                .WithMany()
                .HasForeignKey(e => e.UserId);
        });

        // Patient Configuration
        modelBuilder.Entity<Patient>(entity =>
        {
            entity.ToTable("Patients", "dbo");
            entity.HasKey(e => e.PatientId);
            entity.Property(e => e.PatientIdentifier).IsRequired().HasMaxLength(50);
            entity.HasOne(e => e.Hospital)
                .WithMany()
                .HasForeignKey(e => e.HospitalId);
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
