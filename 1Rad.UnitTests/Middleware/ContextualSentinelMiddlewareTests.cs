using System.Net;
using System.Security.Claims;
using _1Rad.Application.Interfaces;
using _1Rad.Infrastructure.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text.Json;

namespace _1Rad.UnitTests.Middleware;

public class ContextualSentinelMiddlewareTests
{
    private readonly Mock<RequestDelegate> _nextMock;
    private readonly Mock<ILogger<ContextualSentinelMiddleware>> _loggerMock;
    private readonly Mock<IUserContext> _userContextMock;
    private readonly Mock<IApplicationDbContext> _dbMock;

    public ContextualSentinelMiddlewareTests()
    {
        _nextMock = new Mock<RequestDelegate>();
        _loggerMock = new Mock<ILogger<ContextualSentinelMiddleware>>();
        _userContextMock = new Mock<IUserContext>();
        _dbMock = new Mock<IApplicationDbContext>();
    }

    [Fact]
    public async Task InvokeAsync_WithAuthorizedContext_ShouldCallNext()
    {
        // Arrange
        var context = new DefaultHttpContext();
        var hospitalId = Guid.NewGuid();
        
        // Setup authenticated user
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        context.User = new ClaimsPrincipal(identity);

        _userContextMock.Setup(x => x.HospitalId).Returns(hospitalId);
        _userContextMock.Setup(x => x.AuthorizedHospitalIds).Returns(new List<Guid> { hospitalId });

        var middleware = new ContextualSentinelMiddleware(_nextMock.Object, _loggerMock.Object);

        // Act
        await middleware.InvokeAsync(context, _userContextMock.Object, _dbMock.Object);

        // Assert
        _nextMock.Verify(x => x(context), Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_WithMissingCid_ShouldReturnForbidden()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        
        var identity = new ClaimsIdentity(new[] { new Claim("sub", "user1") }, "TestAuth");
        context.User = new ClaimsPrincipal(identity);

        _userContextMock.Setup(x => x.HospitalId).Returns(Guid.Empty);

        var middleware = new ContextualSentinelMiddleware(_nextMock.Object, _loggerMock.Object);

        // Act
        await middleware.InvokeAsync(context, _userContextMock.Object, _dbMock.Object);

        // Assert
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.Forbidden);
        _nextMock.Verify(x => x(context), Times.Never);
    }

    [Fact]
    public async Task InvokeAsync_WithUnauthorizedCid_ShouldReturnUnauthorized()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var identity = new ClaimsIdentity(new[] { new Claim("sub", "user1") }, "TestAuth");
        context.User = new ClaimsPrincipal(identity);

        var requestedCid = Guid.NewGuid();
        _userContextMock.Setup(x => x.HospitalId).Returns(requestedCid);
        _userContextMock.Setup(x => x.AuthorizedHospitalIds).Returns(new List<Guid> { Guid.NewGuid() }); // Different ID

        var middleware = new ContextualSentinelMiddleware(_nextMock.Object, _loggerMock.Object);

        // Act
        await middleware.InvokeAsync(context, _userContextMock.Object, _dbMock.Object);

        // Assert
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);
        _nextMock.Verify(x => x(context), Times.Never);
    }
}
