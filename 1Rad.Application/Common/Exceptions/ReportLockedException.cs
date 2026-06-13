namespace _1Rad.Application.Common.Exceptions;

// Thrown when a write is attempted against a report whose Status is Final or
// Addended. A finalised report's clinical content is immutable (21 CFR Part 11)
// — changes after sign-off must go through a formal addendum, never an edit.
// The API layer maps this to 409 Conflict with code REPORT_LOCKED so the
// frontend can show "this report is signed; add an addendum instead".
public class ReportLockedException : Exception
{
    public ReportLockedException(string? message = null)
        : base(message ?? "This report is finalised and locked. Add an addendum to make a correction.")
    {
    }
}
