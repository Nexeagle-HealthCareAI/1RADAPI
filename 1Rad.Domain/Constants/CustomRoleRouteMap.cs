namespace _1Rad.Domain.Constants;

/// <summary>
/// Custom roles are saved as the web app's page routes ("/billing", "/staff", …), but
/// ContextualSentinelMiddleware authorises API calls ("api/v1/finance/payments"). Before
/// Oct 2026 it compared the two directly, so a custom role never passed a role-gated
/// endpoint — and any string was accepted, so a permission of "api" passed all of them
/// (gap analysis P0-5 / P1-2).
///
/// This map is the single translation from a page to the API areas that page needs.
/// Only routes listed here may be saved on a custom role; anything else is rejected.
/// Keep the keys in step with NAV_ITEMS in 1RADWeb/src/data/roles.js.
/// Prefixes are lower-case and relative to "api/v1/".
/// </summary>
public static class CustomRoleRouteMap
{
    public static readonly IReadOnlyDictionary<string, string[]> PageToApiPrefixes =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["/admin-board"]       = new[] { "personnel", "hospitals", "customroles", "finance/registry", "intelligence", "prescription", "referrers/intelligence", "audit-logs" },
            ["/appointment-board"] = new[] { "appointments", "patients", "referrers", "finance/invoices", "finance/payments", "finance/registry/quick-add", "finance/credit", "approvals" },
            ["/operations-board"]  = new[] { "appointments" },
            ["/billing"]           = new[] { "finance", "referrers" },
            ["/approvals"]         = new[] { "approvals" },
            ["/technician"]        = new[] { "appointments", "study", "study-annotations" },
            ["/doctor-board"]      = new[] { "reporting", "prescription", "study", "study-annotations" },
            ["/referrals"]         = new[] { "referrers", "intelligence" },
            ["/staff"]             = new[] { "staff", "attendance", "leave", "leave-policy" },
            ["/staff/dashboard"]   = new[] { "staff", "attendance", "leave", "leave-policy" },
            ["/configuration"]     = new[] { "reporting/templates", "reporting/keywords" },
            ["/subscription"]      = new[] { "subscriptions" },
            ["/studies"]           = new[] { "study", "study-annotations" },
            ["/dicom-bridge"]      = new[] { "study", "study-annotations" },
        };

    public static bool IsKnownPage(string? route) =>
        !string.IsNullOrWhiteSpace(route) && PageToApiPrefixes.ContainsKey(Normalise(route));

    public static string Normalise(string route)
    {
        var r = route.Trim().ToLowerInvariant().TrimEnd('/');
        return r.StartsWith('/') ? r : "/" + r;
    }

    /// <summary>True when any of the saved page routes grants the given request path.</summary>
    public static bool Grants(IEnumerable<string> savedPageRoutes, string requestPath)
    {
        var path = requestPath.Trim('/').ToLowerInvariant();
        const string apiRoot = "api/v1/";
        if (!path.StartsWith(apiRoot)) return false;
        var rest = path[apiRoot.Length..];

        foreach (var saved in savedPageRoutes)
        {
            if (string.IsNullOrWhiteSpace(saved)) continue;
            if (!PageToApiPrefixes.TryGetValue(Normalise(saved), out var prefixes)) continue;
            foreach (var prefix in prefixes)
            {
                if (rest == prefix || rest.StartsWith(prefix + "/") || rest.StartsWith(prefix + "?"))
                    return true;
            }
        }
        return false;
    }
}
