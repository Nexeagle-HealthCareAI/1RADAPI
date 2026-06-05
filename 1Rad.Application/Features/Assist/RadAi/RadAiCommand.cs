using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Interfaces;
using MediatR;

namespace _1Rad.Application.Features.Assist.RadAi;

public record RadAiTurn(string Role, string Text); // role: "user" | "assistant"

/// <summary>
/// RadAI — the in-app help desk. Answers staff questions about how to use the
/// 1Rad app, in the user's language (Hindi or English). Accepts a typed question
/// OR spoken audio (Gemini transcribes + answers). Backend-only: the API key
/// never reaches the browser, and no patient data is sent.
/// </summary>
public record RadAiCommand : IRequest<RadAiResult>
{
    public string? Question { get; init; }
    public string? Page { get; init; }
    public string? AudioBase64 { get; init; }
    public string? AudioMimeType { get; init; }
    public List<RadAiTurn>? History { get; init; }
}

public record RadAiResult(bool Success, string? Answer, string? Error);

public class RadAiCommandHandler : IRequestHandler<RadAiCommand, RadAiResult>
{
    private readonly IReportAiService _ai;

    public RadAiCommandHandler(IReportAiService ai) => _ai = ai;

    public async Task<RadAiResult> Handle(RadAiCommand request, CancellationToken cancellationToken)
    {
        var hasAudio = !string.IsNullOrWhiteSpace(request.AudioBase64);
        if (!hasAudio && string.IsNullOrWhiteSpace(request.Question))
            return new RadAiResult(false, null, "Ask me something — type or use the mic.");

        if (!_ai.IsConfigured)
            return new RadAiResult(false, null, "RadAI isn't switched on yet (no AI key configured).");

        var system = BuildSystemPrompt(request.Page);

        var user = new StringBuilder();
        if (request.History is { Count: > 0 })
        {
            user.AppendLine("=== CONVERSATION SO FAR ===");
            foreach (var t in request.History.TakeLast(8))
                user.AppendLine($"{(t.Role == "assistant" ? "RadAI" : "User")}: {t.Text}");
            user.AppendLine();
        }
        user.AppendLine(hasAudio
            ? "The user's question is in the attached audio (Hindi or English). Understand it, then answer."
            : "User: " + request.Question!.Trim());

        try
        {
            string answer;
            if (hasAudio)
            {
                byte[] bytes;
                try { bytes = Convert.FromBase64String(StripDataUrl(request.AudioBase64!)); }
                catch { return new RadAiResult(false, null, "Couldn't read the audio. Please try again or type your question."); }
                answer = await _ai.GenerateWithAudioAsync(system, user.ToString(), bytes, request.AudioMimeType, cancellationToken);
            }
            else
            {
                answer = await _ai.GenerateAsync(system, user.ToString(), cancellationToken);
            }

            answer = (answer ?? string.Empty).Trim();
            return answer.Length == 0
                ? new RadAiResult(false, null, "I didn't catch that — could you rephrase?")
                : new RadAiResult(true, answer, null);
        }
        catch
        {
            // Never block the user on a third-party API — friendly fallback.
            return new RadAiResult(false, null, "I'm having trouble right now. Please try again in a moment.");
        }
    }

    private static string StripDataUrl(string b64)
    {
        var idx = b64.IndexOf(',');
        return (b64.StartsWith("data:") && idx >= 0) ? b64[(idx + 1)..] : b64;
    }

    private static string BuildSystemPrompt(string? page)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are RadAI, the friendly in-app help desk for the 1Rad radiology clinic-management system (powered by NexEagle).");
        sb.AppendLine("Your job: help staff (front desk, billers, radiologists, admins) understand and use the app. Be warm, concise and practical.");
        sb.AppendLine("RULES:");
        sb.AppendLine("- Reply in the SAME language the user used. If they write/speak Hindi, answer in Hindi (Devanagari).");
        sb.AppendLine("- Give short, numbered step-by-step guidance ('Go to … → click … → …'). Keep it brief.");
        sb.AppendLine("- Only answer about THIS application's features. For anything unrelated (medical diagnosis, general knowledge), politely say you only help with using 1Rad.");
        sb.AppendLine("- Never ask for or reveal patient data, passwords or keys.");
        sb.AppendLine("- If unsure where a feature is, say so and suggest the closest area — never invent menus that may not exist.");
        if (!string.IsNullOrWhiteSpace(page))
            sb.AppendLine($"- The user is currently on the page '{page}'. Prefer answering in this page's context when relevant.");
        sb.AppendLine();
        sb.AppendLine("=== APP KNOWLEDGE BASE ===");
        sb.AppendLine(KnowledgeBase);
        return sb.ToString();
    }

    private const string KnowledgeBase = @"1Rad is a multi-page web + desktop app for a diagnostic / radiology centre. Main areas are in the left sidebar.

APPOINTMENT BOARD (Appointments): Front desk books and tracks patient visits.
- Book a visit: enter the patient (name, mobile, age, gender), choose service(s) and modality, the attending doctor, and 'Referred By' (the doctor or person who referred the patient — choose 'Self / walk-in' if there is no referrer).
- Filter by modality and status; search by patient, referrer, service or mobile.
- KPI cards: Total Volume (everyone incl. cancelled), Expected (not yet arrived), Arrived In Hall, Scanning/Scanned.
- A green 'PAID' badge appears on a card once that visit's bill is paid.
- 'Change referrer' fixes who gets the referral commission (needs admin approval once payment was taken). Cancelling a PAID appointment also needs admin approval.

BILLING: Money. Tabs at the top — Invoices (Revenue), Expenses, Referrals, Analytics.
- Revenue Hub: each invoice row has a '...' actions menu — Payment/View, print Slip (A4 / thermal / receipt, only after payment), Update payout, Delete. Record a payment in the Payment drawer. 'Mark free' makes a test free (needs approval). Discount and a 'FREE' badge show on the row.
- Referral Hub: commissions owed to referring doctors/agents. Mark a payout PAID; reverting a PAID one needs admin approval. Self / walk-ins are not partners and never appear here.
- Expense Ledger: 'Log expense' to record an outflow; filter and search; export CSV/Excel; bulk mark-paid or delete.

ADMIN APPROVAL: Admins sign off sensitive money changes — edit payment, cancel a paid appointment, change referrer, free test, revert a paid commission. Has Pending and History tabs (6 per page). Approve (optional note + quick suggestions) or Reject (with a reason).

REFERRALS: Manage referring partners and see referral analytics.
- Views: Source Analytics, Case Ledger, Partner Network, Master Index. Filter rows by Doctor / Other / Self.
- A 'Self / Walk-in' block shows direct (non-referred) patients separately from partners.
- Add Partner (one) or Bulk Add (type many at once, or upload an Excel using the downloadable template — duplicates merge automatically).
- 'Doctor links': give each referring doctor a private dashboard link via Copy / WhatsApp / Email (or 'Email all doctors'). The doctor opens it to see the patients they referred, each one's status, and the referral amount eligible / paid / outstanding.

REPORTING (Narrative Editor): Radiologists write the report.
- Type the report; use templates and keyword shortcuts (e.g. .norm), insert normal findings, or copy a prior study forward with a comparison line.
- AI co-pilot: select text then Improve / Proofread / Expand / Shorten. The whole-report '✨ AI' bar offers 'Restructure report' and 'Fix spelling & grammar' — you review the before/after and Apply. Patient identity is masked before the AI ever sees the text.
- Finalize, then print or export (DOCX / PDF).

OTHER: Technician and Doctor boards are scanning/reporting worklists. Operations board shows daily throughput. The Waiting board is a public waiting-room screen. Patients scan a QR to track their study at a public link. Staff covers attendance / leave / payroll; Configuration covers clinic settings, services and letterhead; Subscription covers the plan; DICOM bridge ingests images.

GENERAL: Names are stored in UPPERCASE. Most money changes after payment need admin approval. Look for the '...' menu on table rows for row actions, and the search box on each board.";
}
