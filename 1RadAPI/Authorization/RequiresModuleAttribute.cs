using _1Rad.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace _1RadAPI.Authorization;

/// <summary>
/// Gates a controller or action behind a product module (see
/// <c>ModuleConstants</c>): the active center's subscription must include the
/// module or the request is rejected with 403 / MODULE_NOT_ENABLED.
///
/// Usage: <c>[RequiresModule(ModuleConstants.Pacs)]</c> on PACS-only surfaces
/// (DICOM upload, manifest, viewer), <c>[RequiresModule(ModuleConstants.Ris)]</c>
/// on RIS-only surfaces (appointments, visit billing, referrals). Reporting
/// endpoints carry NO module attribute — every SKU includes reporting.
///
/// Runs after authentication (the hospital comes from the JWT's "cid" claim
/// via IUserContext), resolves entitlements through the cached
/// IModuleEntitlementService, so the per-request cost is a dictionary lookup.
/// An action-level attribute ADDS to a controller-level one (both must pass);
/// for an action needing a different module than its controller, attribute
/// the actions individually instead of the class.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequiresModuleAttribute : Attribute, IAsyncAuthorizationFilter
{
    private readonly string _module;

    public RequiresModuleAttribute(string module) => _module = module;

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        // Another filter (authentication, an earlier module check) already
        // short-circuited — don't overwrite its result.
        if (context.Result != null) return;

        var services = context.HttpContext.RequestServices;
        var userContext = services.GetRequiredService<IUserContext>();
        var entitlements = services.GetRequiredService<IModuleEntitlementService>();

        var hospitalId = userContext.HospitalId;
        if (hospitalId == Guid.Empty)
        {
            // Authenticated but with no center context (e.g. an initiation
            // token) — module-gated endpoints are center-scoped by definition.
            context.Result = new ObjectResult(new
            {
                success = false,
                error = "No active center in the session — module-gated endpoints require a center-scoped token.",
                errorCode = "MODULE_NO_CENTER_CONTEXT",
            })
            { StatusCode = StatusCodes.Status403Forbidden };
            return;
        }

        var access = await entitlements.GetModuleAccessAsync(hospitalId, _module, context.HttpContext.RequestAborted);
        if (access == ModuleAccess.Full) return; // fully entitled — allow.

        if (access == ModuleAccess.GraceRead)
        {
            // PACS removed but inside the read-only grace window. Allow reads
            // (view/browse/export = GET/HEAD) and cleanup (DELETE = export-or-
            // delete); block ingestion/changes (POST/PUT/PATCH).
            var method = context.HttpContext.Request.Method;
            if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsDelete(method))
                return;

            context.Result = new ObjectResult(new
            {
                success = false,
                error = $"The {_module} module was removed; your studies are in a read-only grace period. New uploads and changes are disabled — you can still view, export, or delete existing studies.",
                errorCode = "MODULE_GRACE_READONLY",
                module = _module,
            })
            { StatusCode = StatusCodes.Status403Forbidden };
            return;
        }

        // ModuleAccess.None — not entitled, no active grace.
        context.Result = new ObjectResult(new
        {
            success = false,
            error = $"This feature requires the {_module} module, which is not part of this center's subscription.",
            errorCode = "MODULE_NOT_ENABLED",
            module = _module,
        })
        { StatusCode = StatusCodes.Status403Forbidden };
    }
}
