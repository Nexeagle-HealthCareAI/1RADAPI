namespace _1Rad.Domain.Constants;

/// <summary>GST constants for bills (review B10, B33).</summary>
public static class GstDefaults
{
    /// <summary>SAC heading for human health services (diagnostic imaging and labs fall here).</summary>
    public const string DiagnosticSac = "9993";

    /// <summary>Rates GST allows on a line. 0 = exempt / nil-rated.</summary>
    public static readonly decimal[] AllowedRates = { 0m, 5m, 12m, 18m, 28m };
}
