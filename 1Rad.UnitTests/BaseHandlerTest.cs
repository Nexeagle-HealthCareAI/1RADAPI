using _1Rad.Application.Interfaces;
using _1Rad.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace _1Rad.UnitTests;

/// <summary>
/// Base class for handler tests using In-Memory Database.
/// Provides a real ApplicationDbContext with In-Memory provider for more reliable testing.
/// </summary>
public abstract class BaseHandlerTest : IDisposable
{
    protected readonly ApplicationDbContext Context;
    protected readonly Mock<IUserContext> MockUserContext;
    protected readonly Mock<IPublisher> MockPublisher;
    protected readonly Guid HospitalId;
    protected readonly Guid UserId;

    protected BaseHandlerTest()
    {
        // Generate unique IDs for this test
        HospitalId = Guid.NewGuid();
        UserId = Guid.NewGuid();

        // Create In-Memory database with unique name for test isolation
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        // Setup mocks
        MockUserContext = new Mock<IUserContext>();
        MockUserContext.Setup(x => x.HospitalId).Returns(HospitalId);
        MockUserContext.Setup(x => x.UserId).Returns(UserId);

        MockPublisher = new Mock<IPublisher>();

        // Create context
        Context = new ApplicationDbContext(options, MockPublisher.Object, MockUserContext.Object);
    }

    /// <summary>
    /// Seeds the database with test data. Override in derived classes to add specific test data.
    /// </summary>
    protected virtual async Task SeedDataAsync()
    {
        await Task.CompletedTask;
    }

    /// <summary>
    /// Helper method to save changes to the database
    /// </summary>
    protected async Task SaveChangesAsync()
    {
        await Context.SaveChangesAsync();
    }

    public void Dispose()
    {
        Context.Dispose();
        GC.SuppressFinalize(this);
    }
}
