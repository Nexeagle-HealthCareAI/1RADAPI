using System.Globalization;
using System.Text.Json;
using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Audit.Queries;

/// <summary>
/// "Who changed what" for one record, in plain words (Nov 2026). Reads the audit trail
/// the database already writes on every save (AuditTrail: who, when, old → new) and
/// gathers the rows that belong to the record:
///   • bill    — the bill, its lines, extra charges, payments and approval requests;
///   • visit   — the appointment and its tests;
///   • partner — the referring partner and every share and payout of theirs.
/// Only fields a person would recognise are shown (no ids, no technical columns).
/// </summary>
public record GetRecordHistoryQuery(string Kind, Guid Id) : IRequest<List<HistoryLine>>;

public record HistoryChange(string Field, string? From, string? To);
public record HistoryLine(DateTime At, string? Who, string What, string Action, List<HistoryChange> Changes);

public class GetRecordHistoryQueryHandler : IRequestHandler<GetRecordHistoryQuery, List<HistoryLine>>
{
    private const int MaxRows = 300;
    private readonly IApplicationDbContext _db;
    public GetRecordHistoryQueryHandler(IApplicationDbContext db) => _db = db;

    private static readonly Dictionary<string, string> Thing = new()
    {
        ["Invoice"] = "Bill", ["InvoiceItem"] = "Bill line", ["InvoiceExtraCharge"] = "Extra charge", ["Payment"] = "Payment",
        ["ApprovalRequest"] = "Request to the owner", ["Appointment"] = "Visit", ["AppointmentService"] = "Test",
        ["Referrer"] = "Partner", ["ReferralCommission"] = "Partner share", ["CreditTransaction"] = "Advance",
    };

    // Field → words. Anything not listed (ids, technical columns) is not shown.
    private static readonly Dictionary<string, string> Field = new()
    {
        ["Status"] = "Status", ["PatientName"] = "Patient", ["GrossAmount"] = "Price", ["DiscountAmount"] = "Discount (all)",
        ["CentreDiscount"] = "Discount", ["DiscountReason"] = "Discount reason", ["ReferrerDiscount"] = "Partner discount",
        ["InstitutionalDeduction"] = "Deduction", ["AdditionalCharges"] = "Extra charges", ["AdditionalChargesReason"] = "Extra charge reason",
        ["TotalAmount"] = "Total", ["PaidAmount"] = "Paid", ["IsFree"] = "Free", ["Description"] = "Item", ["Amount"] = "Amount",
        ["Quantity"] = "Quantity", ["PaymentMethod"] = "Paid by", ["Reference"] = "Reference", ["ReceiptNumber"] = "Receipt",
        ["Kind"] = "Type", ["Reason"] = "Reason", ["DateTime"] = "Time", ["Doctor"] = "Doctor", ["ReferredBy"] = "Referred by",
        ["Priority"] = "Priority", ["Service"] = "Test", ["ServiceName"] = "Test", ["Modality"] = "Modality", ["Notes"] = "Notes",
        ["Mobile"] = "Mobile", ["DelayReason"] = "Delay reason", ["ReportProgressStatus"] = "Report", ["Name"] = "Name",
        ["Contact"] = "Contact", ["Pan"] = "PAN", ["CommissionAmount"] = "Share", ["ReferrerName"] = "Partner",
        ["PaymentDate"] = "Paid on", ["PaidBy"] = "Paid by", ["ReferenceNumber"] = "Reference", ["Remarks"] = "Remarks",
        ["Title"] = "Request", ["ReviewNote"] = "Owner's note", ["DeletedAt"] = "Removed",
    };

    public async Task<List<HistoryLine>> Handle(GetRecordHistoryQuery q, CancellationToken ct)
    {
        var id = q.Id.ToString();
        var idUpper = id.ToUpperInvariant();
        var logs = _db.AuditLogs.AsNoTracking();
        IQueryable<_1Rad.Domain.Entities.AuditLog> rows;

        switch ((q.Kind ?? string.Empty).ToLowerInvariant())
        {
            case "bill":
            {
                var children = await _db.InvoiceItemIdsAsync(q.Id, ct);
                var pays = await _db.Payments.AsNoTracking().Where(p => p.InvoiceId == q.Id).Select(p => p.Id.ToString()).ToListAsync(ct);
                var kids = children.Concat(pays).ToList();
                rows = logs.Where(a =>
                    (a.EntityType == "Invoice" && a.EntityId == id)
                    || ((a.EntityType == "InvoiceItem" || a.EntityType == "Payment" || a.EntityType == "InvoiceExtraCharge" || a.EntityType == "ApprovalRequest")
                        && (kids.Contains(a.EntityId) || (a.ChangesJson != null && (a.ChangesJson.Contains(id) || a.ChangesJson.Contains(idUpper))))));
                break;
            }
            case "visit":
            {
                var tests = await _db.AppointmentServices.AsNoTracking().Where(s => s.AppointmentId == q.Id).Select(s => s.Id.ToString()).ToListAsync(ct);
                rows = logs.Where(a =>
                    (a.EntityType == "Appointment" && a.EntityId == id)
                    || (a.EntityType == "AppointmentService" && (tests.Contains(a.EntityId) || (a.ChangesJson != null && (a.ChangesJson.Contains(id) || a.ChangesJson.Contains(idUpper))))));
                break;
            }
            case "partner":
            {
                rows = logs.Where(a =>
                    (a.EntityType == "Referrer" && a.EntityId == id)
                    || (a.EntityType == "ReferralCommission" && a.ChangesJson != null && (a.ChangesJson.Contains(id) || a.ChangesJson.Contains(idUpper)))
                    || (a.EntityType == "ReferralCommission" && _db.ReferralCommissions.Any(c => c.ReferrerId == q.Id && c.Id.ToString() == a.EntityId)));
                break;
            }
            default:
                throw new _1Rad.Domain.Exceptions.ValidationException("Kind must be bill, visit or partner.");
        }

        var raw = await rows.OrderByDescending(a => a.Id).Take(MaxRows).ToListAsync(ct);
        var userIds = raw.Where(r => r.UserId.HasValue).Select(r => r.UserId!.Value).Distinct().ToList();
        var names = await _db.Users.AsNoTracking().Where(u => userIds.Contains(u.UserId))
            .Select(u => new { u.UserId, u.FullName }).ToDictionaryAsync(u => u.UserId, u => u.FullName, ct);

        var lines = new List<HistoryLine>();
        foreach (var r in raw)
        {
            var thing = Thing.TryGetValue(r.EntityType, out var t) ? t : r.EntityType;
            var changes = Parse(r.Action, r.ChangesJson);
            if (r.Action == "Modified" && changes.Count == 0) continue; // only technical columns changed
            var what = r.Action switch
            {
                "Added" => $"{thing} added",
                "Deleted" or "SoftDeleted" => $"{thing} removed",
                _ => $"{thing} changed",
            };
            lines.Add(new HistoryLine(r.CreatedAt, r.UserId.HasValue && names.TryGetValue(r.UserId.Value, out var n) ? n : null,
                what, r.Action, changes));
        }
        return lines;
    }

    private static List<HistoryChange> Parse(string action, string? json)
    {
        var list = new List<HistoryChange>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        Dictionary<string, JsonElement>? map;
        try { map = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json); }
        catch (JsonException) { return list; }
        if (map == null) return list;
        foreach (var (key, value) in map)
        {
            if (!Field.TryGetValue(key, out var label)) continue;
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("from", out var from) && value.TryGetProperty("to", out var to))
            {
                if (key == "DeletedAt") continue; // shown as "removed"
                list.Add(new HistoryChange(label, Show(from), Show(to)));
            }
            else if (action == "Added" || action == "Deleted")
            {
                var v = Show(value);
                if (!string.IsNullOrEmpty(v) && v != "0" && v != "false") list.Add(new HistoryChange(label, null, v));
            }
        }
        return list;
    }

    private static string? Show(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.True => "yes",
        JsonValueKind.False => "no",
        JsonValueKind.Number => v.TryGetDecimal(out var d) ? d.ToString("0.##", CultureInfo.InvariantCulture) : v.GetRawText(),
        JsonValueKind.String => v.GetString(),
        _ => v.GetRawText(),
    };
}

internal static class RecordHistoryDb
{
    // InvoiceItems has no DbSet of its own: reach the lines through the bill.
    public static Task<List<string>> InvoiceItemIdsAsync(this IApplicationDbContext db, Guid invoiceId, CancellationToken ct) =>
        db.Invoices.AsNoTracking().Where(i => i.Id == invoiceId).SelectMany(i => i.Items.Select(it => it.Id.ToString())).ToListAsync(ct);
}
