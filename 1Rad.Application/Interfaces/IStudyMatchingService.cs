using System.Threading;
using System.Threading.Tasks;
using _1Rad.Domain.Entities;

namespace _1Rad.Application.Interfaces
{
    /// <summary>
    /// Server-side reconciliation of an appointment-free (Cloud PACS-only)
    /// <see cref="ImagingStudy"/> to a patient and/or appointment, using the
    /// DICOM identifiers carried on the study. High-confidence hits auto-link;
    /// anything ambiguous is left <c>Unmatched</c> for the manual inbox.
    ///
    /// Mutates the passed study in place; the CALLER owns SaveChanges.
    /// </summary>
    public interface IStudyMatchingService
    {
        /// <summary>
        /// Attempts to link <paramref name="study"/>. Returns true if anything
        /// was linked (and <c>MatchStatus</c> set to <c>AutoMatched</c>). A
        /// study a human already assigned is never overridden.
        /// </summary>
        Task<bool> TryMatchAsync(ImagingStudy study, CancellationToken cancellationToken);
    }
}
