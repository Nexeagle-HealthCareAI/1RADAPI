using _1Rad.Domain.Constants;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace _1RadAPI.Authorization;

/// <summary>
/// On an integration endpoint: an integration key must carry this scope.
/// Staff sign-ins are not affected (their roles decide, via [Authorize(Roles=…)]).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class IntegrationScopeAttribute : Attribute, IAuthorizationFilter
{
    public string Scope { get; }

    public IntegrationScopeAttribute(string scope) => Scope = scope;

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (context.Result != null) return;
        var user = context.HttpContext.User;
        if (user.FindFirst("type")?.Value != IntegrationScopes.PrincipalType) return;
        if (user.HasClaim(IntegrationScopes.ScopeClaim, Scope)) return;

        context.Result = new ObjectResult(new
        {
            success = false,
            error = $"This integration key does not have the '{Scope}' scope.",
            errorCode = "INTEGRATION_SCOPE_MISSING",
        })
        { StatusCode = StatusCodes.Status403Forbidden };
    }
}
