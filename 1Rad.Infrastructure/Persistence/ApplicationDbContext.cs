using _1Rad.Application.Interfaces;
using _1Rad.Domain.Common;
using _1Rad.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Infrastructure.Persistence;

public class ApplicationDbContext : DbContext, IApplicationDbContext
{
    private readonly IPublisher _publisher;

    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options, IPublisher publisher) : base(options)
    {
        _publisher = publisher;
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<HospitalGroup> HospitalGroups => Set<HospitalGroup>();
    public DbSet<Hospital> Hospitals => Set<Hospital>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserHospitalMapping> UserHospitalMappings => Set<UserHospitalMapping>();
    public DbSet<OTPVerification> OTPVerifications => Set<OTPVerification>();

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

            entity.HasOne(e => e.Role)
                .WithMany(r => r.HospitalMappings)
                .HasForeignKey(e => e.RoleId);
        });

        // OTPVerification Configuration
        modelBuilder.Entity<OTPVerification>(entity =>
        {
            entity.ToTable("OTPVerifications", "dbo");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Identifier).IsRequired().HasMaxLength(100);
            entity.Property(e => e.CodeHash).IsRequired();
        });
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
