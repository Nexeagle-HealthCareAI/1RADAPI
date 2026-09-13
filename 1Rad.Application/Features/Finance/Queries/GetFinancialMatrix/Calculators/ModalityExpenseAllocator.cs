using System.Text.RegularExpressions;

namespace _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix.Calculators;

public class ModalityExpenseAllocator : IModalityExpenseAllocator
{
    private sealed record ModalityRule(string Modality, string[] DescriptionKeywords, string[] CostCenterExactMatches);

    // Evaluated in order, first match wins (mirrors the original if/else-if
    // chain's priority: an expense described "MRI coil maintenance" is
    // attributed to MRI, not swept into general Maintenance). Add a new
    // modality here — no other code in this class changes.
    private static readonly ModalityRule[] KeywordMap =
    {
        new("MRI",   new[] { "MRI" },               new[] { "MRI" }),
        new("CT",    new[] { "CT" },                 new[] { "CT" }),
        new("X-RAY", new[] { "X-RAY", "XRAY" },       new[] { "X-RAY", "XRAY" }),
        new("USG",   new[] { "USG", "ULTRASOUND" },   new[] { "USG" }),
    };

    public ModalityExpenseAllocation Allocate(IReadOnlyList<ExpenseMatrixRow> expenseData)
    {
        var result = new ModalityExpenseAllocation();

        foreach (var exp in expenseData)
        {
            var desc = exp.Description ?? "";
            var cc = exp.CostCenter ?? "";
            var cat = exp.Category ?? "";
            var cost = exp.Amount + exp.TaxAmount;

            // Word-boundary match, not a raw substring search — "CT" as a plain
            // Contains() matches "Contract renewal" or "Electricity bill" (both
            // contain "ct"), silently misattributing unrelated overhead into the
            // CT modality's profitability numbers. \b anchors the keyword to a
            // real word so only an actual standalone mention of "CT" counts.
            var rule = KeywordMap.FirstOrDefault(r =>
                r.DescriptionKeywords.Any(k => Regex.IsMatch(desc, $@"\b{Regex.Escape(k)}\b", RegexOptions.IgnoreCase)) ||
                r.CostCenterExactMatches.Any(k => cc.Equals(k, StringComparison.OrdinalIgnoreCase)));

            if (rule != null)
            {
                result.DirectByModality[rule.Modality] = result.DirectByModality.GetValueOrDefault(rule.Modality) + cost;
            }
            else if (cc.Equals("Radiology", StringComparison.OrdinalIgnoreCase) || cat.Equals("Maintenance", StringComparison.OrdinalIgnoreCase))
            {
                result.GeneralRadiologyOverhead += cost;
            }
        }

        return result;
    }
}
