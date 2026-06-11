using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

// One row per answered RadAI question. Feeds the "retrain" loop: the most-asked
// questions — and especially those the knowledge base did NOT cover
// (Covered == false) — are the entries to add or refresh in app_knowledge.json.
//
// Implements IHospitalContext so the global query filter scopes every read/write
// to the current centre automatically (see ApplicationDbContext.OnModelCreating).
public class RadAiQuestionLog : BaseEntity, IHospitalContext
{
    public Guid RadAiQuestionLogId { get; set; } = Guid.NewGuid();
    public Guid HospitalId { get; set; }

    public Guid? AskedByUserId { get; set; }
    public Guid? SessionId { get; set; }

    // The typed question. Null for voice questions — the spoken transcript is not
    // currently surfaced by the model, so WasVoice flags those rows.
    public string? Question { get; set; }
    public bool WasVoice { get; set; }

    // Where in the app it was asked, and the reply language the model chose.
    public string? Page { get; set; }
    public string? ReplyLanguage { get; set; }

    // The honesty flag from RadAiResult: false => the knowledge base did not
    // cover this question. These are the highest-value gaps to fix.
    public bool Covered { get; set; }

    // A short, PHI-free snippet of the answer for quick review (RadAI answers are
    // how-to guidance, never patient data). Length capped in the model config.
    public string? AnswerSnippet { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
