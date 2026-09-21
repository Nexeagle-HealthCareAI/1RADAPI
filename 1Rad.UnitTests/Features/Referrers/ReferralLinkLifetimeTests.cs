using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Common;
using _1Rad.Application.Features.Referrers.Commands.RenewReferralLinks;
using _1Rad.Application.Features.Referrers.Commands.RevokeReferralLinks;
using _1Rad.Application.Features.Referrers.Commands.SendReferralLinks;
using _1Rad.Application.Features.Referrers.Commands.SendReferralLinksWhatsApp;
using _1Rad.Application.Features.Referrers.Queries.GetReferralLinkStatus;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Exceptions;
using _1Rad.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace _1Rad.UnitTests.Features.Referrers;

/// <summary>
/// Doctor-portal link LIFETIME: links expire (90 days by default), a centre's sends are
/// recorded, a daily job renews them before they die, and a doctor with an expired link can
/// ask for a fresh one (delivered only to the contact on file).
/// </summary>
public class ReferralLinkLifetimeTests : BaseHandlerTest
{
    private const string Secret = "unit-test-secret-0123456789-abcdefghij";
    private const string Portal = "https://app.example.test";

    private readonly Mock<IEmailService> _email = new();
    private readonly Mock<ISmsService> _sms = new();
    private readonly List<string> _smsLinks = new();
    private readonly List<string> _emailBodies = new();

    private static IConfiguration Config(Dictionary<string, string?>? extra = null)
    {
        var d = new Dictionary<string, string?> { ["Jwt:Secret"] = Secret };
        if (extra != null) foreach (var kv in extra) d[kv.Key] = kv.Value;
        return new ConfigurationBuilder().AddInMemoryCollection(d).Build();
    }

    private ReferralLinkTokenService Tokens(Dictionary<string, string?>? extra = null) => new(Config(extra));

    private ReferralLinkSender Sender(ReferralLinkTokenService tokens)
    {
        _sms.Setup(s => s.SendReferralLinkAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string, string, string>((_, _, _, link) => _smsLinks.Add(link))
            .Returns(Task.CompletedTask);
        _email.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string, string>((_, _, body) => _emailBodies.Add(body))
            .Returns(Task.CompletedTask);
        return new ReferralLinkSender(Context, tokens, _email.Object, _sms.Object);
    }

    private Referrer AddPartner(string name, string? contact = "9876543210", string? email = "dr@example.test", Guid? hospital = null)
    {
        var r = new Referrer { ReferrerId = Guid.NewGuid(), HospitalId = hospital ?? HospitalId, Name = name, Contact = contact, Email = email };
        Context.Referrers.Add(r);
        return r;
    }

    private void AddHospital(string status = "Active", Guid? id = null) =>
        Context.Hospitals.Add(new Hospital { HospitalId = id ?? HospitalId, HospitalName = "Test Centre", Status = status });

    private ReferrerLinkVersion AddRow(Referrer p, TimeSpan expiresIn, string channel = "whatsapp", string? baseUrl = Portal,
        bool autoRenew = true, TimeSpan? sentAgo = null, int version = 0)
    {
        var row = new ReferrerLinkVersion
        {
            ReferrerId = p.ReferrerId, HospitalId = p.HospitalId, Version = version,
            LastSentAt = DateTime.UtcNow - (sentAgo ?? TimeSpan.FromDays(70)), LastSentChannel = channel,
            LastSentBaseUrl = baseUrl, LastSentExpiresAt = DateTime.UtcNow + expiresIn, AutoRenew = autoRenew,
        };
        Context.ReferrerLinkVersions.Add(row);
        return row;
    }

    private ReferrerLinkVersion Row(Guid referrerId) => Context.ReferrerLinkVersions.Single(v => v.ReferrerId == referrerId);

    private static string TokenExpiringIn(ReferralLinkTokenService tokens, Guid id, int version, TimeSpan ttl) => tokens.Issue(id, version, ttl);

    // A token exactly as minted BEFORE versions existed (24-byte payload) - 365-day-era links.
    private static string LegacyToken(Guid referrerId, TimeSpan ttl)
    {
        var payload = new byte[24];
        referrerId.TryWriteBytes(payload.AsSpan(0, 16));
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(16, 8), DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeSeconds());
        var buf = Encoding.ASCII.GetBytes("REFERRAL_LINK_V1").Concat(payload).ToArray();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
        string B64(byte[] d) => Convert.ToBase64String(d).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64(payload)}.{B64(hmac.ComputeHash(buf))}";
    }

    // ── policy + token lifetime ─────────────────────────────────────────────────

    [Fact]
    public void Options_DefaultToNinetyDays_AndClampNonsense()
    {
        var d = ReferralLinkOptions.From(Config());
        Assert.Equal(90, d.TtlDays);
        Assert.Equal(14, d.RenewBeforeDays);
        Assert.True(d.AutoRenewEnabled);

        var clamped = ReferralLinkOptions.From(Config(new() { ["ReferralLinks:TtlDays"] = "99999", ["ReferralLinks:RenewBeforeDays"] = "9999", ["ReferralLinks:SelfServeCooldownHours"] = "0" }));
        Assert.Equal(365, clamped.TtlDays);                    // never immortal
        Assert.True(clamped.RenewBeforeDays <= clamped.TtlDays / 2);
        Assert.Equal(1, clamped.SelfServeCooldownHours);

        var tiny = ReferralLinkOptions.From(Config(new() { ["ReferralLinks:TtlDays"] = "1" }));
        Assert.Equal(7, tiny.TtlDays);                         // never instantly dead

        Assert.False(ReferralLinkOptions.From(Config(new() { ["ReferralLinks:AutoRenewEnabled"] = "false" })).AutoRenewEnabled);
        Assert.Equal("https://x.test", ReferralLinkOptions.From(Config(new() { ["ReferralLinks:PortalBaseUrl"] = " https://x.test/ " })).PortalBaseUrl);
    }

    [Fact]
    public void NewLinks_ExpireAfterTheConfiguredLifetime_NotAYear()
    {
        var id = Guid.NewGuid();
        var tokens = Tokens();
        Assert.True(tokens.TryReadClaims(tokens.Issue(id), out var claims));
        Assert.InRange((claims.ExpiresAtUtc - DateTime.UtcNow).TotalDays, 89.9, 90.1);

        var short30 = Tokens(new() { ["ReferralLinks:TtlDays"] = "30" });
        Assert.True(short30.TryReadClaims(short30.Issue(id), out var c30));
        Assert.InRange((c30.ExpiresAtUtc - DateTime.UtcNow).TotalDays, 29.9, 30.1);
    }

    [Fact]
    public void LinksIssuedUnderTheOldYearLongPolicy_KeepWorkingUntilTheirOwnExpiry()
    {
        var id = Guid.NewGuid();
        var tokens = Tokens();
        var legacyYear = LegacyToken(id, TimeSpan.FromDays(300));      // signed with 300 days left
        Assert.True(tokens.Validate(legacyYear, id, 0));
    }

    [Fact]
    public void TryReadClaims_ReadsAnExpiredTokenButRejectsForgeries()
    {
        var id = Guid.NewGuid();
        var tokens = Tokens();
        var expired = TokenExpiringIn(tokens, id, 3, TimeSpan.FromDays(-2));

        Assert.False(tokens.Validate(expired, id, 3));
        Assert.True(tokens.TryReadClaims(expired, out var claims));
        Assert.Equal(id, claims.ReferrerId);
        Assert.Equal(3, claims.Version);
        Assert.True(claims.IsExpired(DateTime.UtcNow));

        var parts = expired.Split('.');
        Assert.False(tokens.TryReadClaims(parts[0] + "." + parts[1][..^2] + "AA", out _));
        Assert.False(tokens.TryReadClaims("garbage", out _));
        Assert.False(tokens.TryReadClaims("", out _));
    }

    // ── sending records what was sent ───────────────────────────────────────────

    [Fact]
    public async Task Send_OverWhatsApp_RecordsChannelPortalAndExpiry_AndTurnsAutoRenewOn()
    {
        var tokens = Tokens();
        var p = AddPartner("DR A");
        AddHospital();
        await Context.SaveChangesAsync();

        var outcome = await Sender(tokens).SendAsync(HospitalId, p.ReferrerId, "whatsapp", Portal + "/", autoRenew: true, CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal("••••3210", outcome.MaskedTo);
        var link = Assert.Single(_smsLinks);
        Assert.StartsWith($"{Portal}/r/{p.ReferrerId}?t=", link);
        var row = Row(p.ReferrerId);
        Assert.Equal("whatsapp", row.LastSentChannel);
        Assert.Equal(Portal, row.LastSentBaseUrl);                       // trailing slash normalised away
        Assert.True(row.AutoRenew);
        Assert.InRange((row.LastSentExpiresAt!.Value - DateTime.UtcNow).TotalDays, 89.9, 90.1);
        Assert.NotNull(row.LastSentAt);
    }

    [Fact]
    public async Task Send_OverEmail_UsesTheEmailChannel()
    {
        var tokens = Tokens();
        var p = AddPartner("DR A");
        await Context.SaveChangesAsync();

        var outcome = await Sender(tokens).SendAsync(HospitalId, p.ReferrerId, "email", Portal, true, CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal("d•••@example.test", outcome.MaskedTo);
        Assert.Contains($"{Portal}/r/{p.ReferrerId}?t=", Assert.Single(_emailBodies));
        Assert.Equal("email", Row(p.ReferrerId).LastSentChannel);
    }

    [Fact]
    public async Task Send_RefusesWithoutContact_BadPortalAddress_OrAnotherCentresPartner_AndRecordsNothing()
    {
        var tokens = Tokens();
        var noMobile = AddPartner("DR NOMOBILE", contact: null);
        var ok = AddPartner("DR OK");
        var foreign = AddPartner("DR FOREIGN", hospital: Guid.NewGuid());
        await Context.SaveChangesAsync();
        var sender = Sender(tokens);

        var a = await sender.SendAsync(HospitalId, noMobile.ReferrerId, "whatsapp", Portal, true, CancellationToken.None);
        Assert.False(a.Success); Assert.True(a.NoContact);

        foreach (var bad in new[] { "javascript:alert(1)", "not a url", "", "ftp://x.test" })
        {
            var b = await sender.SendAsync(HospitalId, ok.ReferrerId, "whatsapp", bad, true, CancellationToken.None);
            Assert.False(b.Success);
        }

        var c = await sender.SendAsync(HospitalId, foreign.ReferrerId, "whatsapp", Portal, true, CancellationToken.None);
        Assert.False(c.Success); Assert.True(c.PartnerMissing);

        Assert.Empty(_smsLinks);
        Assert.Empty(Context.ReferrerLinkVersions);
    }

    [Fact]
    public async Task ManualSendCommands_KeepTheirResultShape_AndSwitchAutoRenewOn()
    {
        var tokens = Tokens();
        var withMobile = AddPartner("DR WITH");
        var without = AddPartner("DR WITHOUT", contact: "");
        await Context.SaveChangesAsync();
        var sender = Sender(tokens);

        var wa = await new SendReferralLinksWhatsAppCommandHandler(Context, sender)
            .Handle(new SendReferralLinksWhatsAppCommand(new() { withMobile.ReferrerId, without.ReferrerId, Guid.NewGuid() }, Portal), CancellationToken.None);
        Assert.Equal(1, wa.Sent);
        Assert.Equal(new[] { "DR WITHOUT" }, wa.NoContact);
        Assert.Empty(wa.Failed);
        Assert.True(Row(withMobile.ReferrerId).AutoRenew);

        var em = await new SendReferralLinksCommandHandler(Context, sender)
            .Handle(new SendReferralLinksCommand(new() { withMobile.ReferrerId }, Portal), CancellationToken.None);
        Assert.Equal(1, em.Sent);
        Assert.Equal(0, em.Skipped);
    }

    // ── daily renewal ───────────────────────────────────────────────────────────

    private RenewExpiringReferralLinksCommandHandler RenewHandler(ReferralLinkTokenService tokens, Dictionary<string, string?>? cfg = null)
        => new(Context, Sender(tokens), ReferralLinkOptions.From(Config(cfg)));

    [Fact]
    public async Task Renewal_SendsANewLinkBeforeExpiry_OverTheSameChannelAndPortal_AndIsIdempotent()
    {
        var tokens = Tokens();
        var p = AddPartner("DR A");
        AddHospital();
        AddRow(p, expiresIn: TimeSpan.FromDays(10));                       // inside the 14-day window
        await Context.SaveChangesAsync();
        var handler = RenewHandler(tokens);

        var first = await handler.Handle(new RenewExpiringReferralLinksCommand(), CancellationToken.None);

        Assert.Equal(1, first.Renewed);
        Assert.StartsWith($"{Portal}/r/{p.ReferrerId}?t=", Assert.Single(_smsLinks));
        Assert.InRange((Row(p.ReferrerId).LastSentExpiresAt!.Value - DateTime.UtcNow).TotalDays, 89.9, 90.1);   // pushed out

        var second = await handler.Handle(new RenewExpiringReferralLinksCommand(), CancellationToken.None);
        Assert.Equal(0, second.Renewed);
        Assert.Single(_smsLinks);                                           // nothing more was sent
    }

    [Fact]
    public async Task Renewal_LeavesAlone_LinksNotDue_SwitchedOff_LongDead_OrJustSent()
    {
        var tokens = Tokens();
        AddHospital();
        var notDue = AddPartner("NOT DUE");        AddRow(notDue, TimeSpan.FromDays(60));
        var off = AddPartner("SWITCHED OFF");      AddRow(off, TimeSpan.FromDays(5), autoRenew: false);
        var dead = AddPartner("LONG DEAD");        AddRow(dead, TimeSpan.FromDays(-40));
        var justSent = AddPartner("JUST SENT");    AddRow(justSent, TimeSpan.FromDays(5), sentAgo: TimeSpan.FromHours(2));
        await Context.SaveChangesAsync();

        var result = await RenewHandler(tokens).Handle(new RenewExpiringReferralLinksCommand(), CancellationToken.None);

        Assert.Equal(0, result.Renewed);
        Assert.Empty(_smsLinks);
    }

    [Fact]
    public async Task Renewal_RetriesAnOverdueLinkWithinTheGraceWindow()
    {
        var tokens = Tokens();
        AddHospital();
        var p = AddPartner("MISSED BY A FEW DAYS");
        AddRow(p, TimeSpan.FromDays(-3));                                   // expired 3 days ago (job was down)
        await Context.SaveChangesAsync();

        var result = await RenewHandler(tokens).Handle(new RenewExpiringReferralLinksCommand(), CancellationToken.None);

        Assert.Equal(1, result.Renewed);
    }

    [Fact]
    public async Task Renewal_StopsMessagingAPartnerWithNoContact_AndRetriesAGatewayFailure()
    {
        var tokens = Tokens();
        AddHospital();
        var noContact = AddPartner("NO CONTACT", contact: null); AddRow(noContact, TimeSpan.FromDays(5));
        var flaky = AddPartner("FLAKY GATEWAY");                 AddRow(flaky, TimeSpan.FromDays(5));
        await Context.SaveChangesAsync();
        var sender = Sender(tokens);
        _sms.Setup(s => s.SendReferralLinkAsync(It.Is<string>(m => m.EndsWith("3210")), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("gateway down"));

        var result = await new RenewExpiringReferralLinksCommandHandler(Context, sender, ReferralLinkOptions.From(Config()))
            .Handle(new RenewExpiringReferralLinksCommand(), CancellationToken.None);

        Assert.Equal(1, result.Disabled);
        Assert.False(Row(noContact.ReferrerId).AutoRenew);                  // stop trying until a centre re-sends
        Assert.Equal(1, result.Failed);
        Assert.True(Row(flaky.ReferrerId).AutoRenew);                       // will be retried on the next run
    }

    [Fact]
    public async Task Renewal_HonoursTheMasterSwitchAndSkipsInactiveCentres()
    {
        var tokens = Tokens();
        var p = AddPartner("DR A");
        AddHospital();
        AddRow(p, TimeSpan.FromDays(5));
        var otherHospital = Guid.NewGuid();
        var q = AddPartner("DR B", hospital: otherHospital);
        AddHospital(status: "Suspended", id: otherHospital);
        AddRow(q, TimeSpan.FromDays(5));
        await Context.SaveChangesAsync();

        var off = await RenewHandler(tokens, new() { ["ReferralLinks:AutoRenewEnabled"] = "false" })
            .Handle(new RenewExpiringReferralLinksCommand(), CancellationToken.None);
        Assert.Equal(0, off.Checked);
        Assert.Empty(_smsLinks);

        var on = await RenewHandler(tokens).Handle(new RenewExpiringReferralLinksCommand(), CancellationToken.None);
        Assert.Equal(1, on.Renewed);                                        // the suspended centre's doctor is not messaged
        Assert.Single(_smsLinks);
    }

    [Fact]
    public async Task Revoking_StopsAutomaticRenewal_UntilACentreSendsAgain()
    {
        var tokens = Tokens();
        AddHospital();
        var p = AddPartner("DR A");
        AddRow(p, TimeSpan.FromDays(5));
        await Context.SaveChangesAsync();

        await new RevokeReferralLinksCommandHandler(Context).Handle(new RevokeReferralLinksCommand(p.ReferrerId), CancellationToken.None);

        Assert.False(Row(p.ReferrerId).AutoRenew);
        Assert.Null(Row(p.ReferrerId).LastSentExpiresAt);
        var result = await RenewHandler(tokens).Handle(new RenewExpiringReferralLinksCommand(), CancellationToken.None);
        Assert.Equal(0, result.Renewed);
        Assert.Empty(_smsLinks);

        // A deliberate send afterwards switches it back on, under the NEW version.
        var send = await Sender(tokens).SendAsync(HospitalId, p.ReferrerId, "whatsapp", Portal, true, CancellationToken.None);
        Assert.True(send.Success);
        Assert.True(Row(p.ReferrerId).AutoRenew);
        Assert.Equal(1, Row(p.ReferrerId).Version);
    }

    // ── the doctor asks for a fresh link ────────────────────────────────────────

    private RenewReferralLinkSelfServeCommandHandler SelfServe(ReferralLinkTokenService tokens, Dictionary<string, string?>? cfg = null)
        => new(Context, tokens, Sender(tokens), ReferralLinkOptions.From(Config(cfg)));

    [Fact]
    public async Task SelfServe_ExpiredLink_SendsAFreshOneOnlyToTheContactOnFile_UsingTheCentresPortal()
    {
        var tokens = Tokens();
        AddHospital();
        var p = AddPartner("DR A");
        AddRow(p, TimeSpan.FromDays(-5), sentAgo: TimeSpan.FromDays(95));
        await Context.SaveChangesAsync();
        var expired = TokenExpiringIn(tokens, p.ReferrerId, 0, TimeSpan.FromDays(-5));

        var result = await SelfServe(tokens).Handle(new RenewReferralLinkSelfServeCommand(p.ReferrerId, expired), CancellationToken.None);

        Assert.Equal("whatsapp", result.Channel);
        Assert.Equal("••••3210", result.MaskedTo);                          // the doctor learns WHERE it went, never the link
        var link = Assert.Single(_smsLinks);
        Assert.StartsWith($"{Portal}/r/{p.ReferrerId}?t=", link);            // the centre's portal - nothing came from the request
        var fresh = link[(link.IndexOf("?t=", StringComparison.Ordinal) + 3)..];
        Assert.True(tokens.Validate(fresh, p.ReferrerId, 0));
    }

    [Fact]
    public async Task SelfServe_FallsBackToEmail_WhenThereIsNoMobile_AndRefusesWhenThereIsNothing()
    {
        var tokens = Tokens();
        AddHospital();
        var emailOnly = AddPartner("EMAIL ONLY", contact: null);
        var nothing = AddPartner("NOTHING", contact: null, email: null);
        AddRow(emailOnly, TimeSpan.FromDays(-1), channel: "whatsapp", sentAgo: TimeSpan.FromDays(91));
        AddRow(nothing, TimeSpan.FromDays(-1), sentAgo: TimeSpan.FromDays(91));
        await Context.SaveChangesAsync();
        var handler = SelfServe(tokens);

        var viaEmail = await handler.Handle(new RenewReferralLinkSelfServeCommand(emailOnly.ReferrerId, TokenExpiringIn(tokens, emailOnly.ReferrerId, 0, TimeSpan.FromDays(-1))), CancellationToken.None);
        Assert.Equal("email", viaEmail.Channel);

        await Assert.ThrowsAsync<BusinessRuleViolationException>(() =>
            handler.Handle(new RenewReferralLinkSelfServeCommand(nothing.ReferrerId, TokenExpiringIn(tokens, nothing.ReferrerId, 0, TimeSpan.FromDays(-1))), CancellationToken.None));
    }

    [Fact]
    public async Task SelfServe_RefusesARevokedLink_ALinkThatIsStillValid_AndAForgery()
    {
        var tokens = Tokens();
        AddHospital();
        var p = AddPartner("DR A");
        var revoked = AddPartner("REVOKED");
        AddRow(revoked, TimeSpan.FromDays(-1), version: 2, autoRenew: false);   // centre pulled the links back
        await Context.SaveChangesAsync();
        var handler = SelfServe(tokens);

        // Revoked / replaced: the token is expired but from an OLDER version - must stay dead.
        await Assert.ThrowsAsync<BusinessRuleViolationException>(() =>
            handler.Handle(new RenewReferralLinkSelfServeCommand(revoked.ReferrerId, TokenExpiringIn(tokens, revoked.ReferrerId, 1, TimeSpan.FromDays(-1))), CancellationToken.None));

        // Still comfortably valid (60 days left): no new link needed.
        await Assert.ThrowsAsync<BusinessRuleViolationException>(() =>
            handler.Handle(new RenewReferralLinkSelfServeCommand(p.ReferrerId, TokenExpiringIn(tokens, p.ReferrerId, 0, TimeSpan.FromDays(60))), CancellationToken.None));

        // Someone else's token, garbage, and no token.
        await Assert.ThrowsAsync<BusinessRuleViolationException>(() =>
            handler.Handle(new RenewReferralLinkSelfServeCommand(p.ReferrerId, TokenExpiringIn(tokens, Guid.NewGuid(), 0, TimeSpan.FromDays(-1))), CancellationToken.None));
        await Assert.ThrowsAsync<BusinessRuleViolationException>(() => handler.Handle(new RenewReferralLinkSelfServeCommand(p.ReferrerId, "garbage"), CancellationToken.None));
        await Assert.ThrowsAsync<BusinessRuleViolationException>(() => handler.Handle(new RenewReferralLinkSelfServeCommand(p.ReferrerId, null), CancellationToken.None));

        Assert.Empty(_smsLinks);
        Assert.Empty(_emailBodies);
    }

    [Fact]
    public async Task SelfServe_IsRateLimitedPerDoctor_AndNeedsAPortalAddressTheCentreSupplied()
    {
        var tokens = Tokens();
        AddHospital();
        var p = AddPartner("DR A");
        AddRow(p, TimeSpan.FromDays(-2), sentAgo: TimeSpan.FromDays(92));
        var neverSent = AddPartner("NEVER SENT FROM APP");                  // no row -> no recorded portal
        await Context.SaveChangesAsync();
        var handler = SelfServe(tokens);
        var expired = TokenExpiringIn(tokens, p.ReferrerId, 0, TimeSpan.FromDays(-2));

        await handler.Handle(new RenewReferralLinkSelfServeCommand(p.ReferrerId, expired), CancellationToken.None);
        // Asking again straight away (the old link is still expired) is refused - no message spam.
        var again = await Assert.ThrowsAsync<BusinessRuleViolationException>(() =>
            handler.Handle(new RenewReferralLinkSelfServeCommand(p.ReferrerId, expired), CancellationToken.None));
        Assert.Contains("just sent", again.Message);
        Assert.Single(_smsLinks);

        // No recorded portal and none configured: refuse rather than guess an address.
        var noPortalToken = TokenExpiringIn(tokens, neverSent.ReferrerId, 0, TimeSpan.FromDays(-2));
        await Assert.ThrowsAsync<BusinessRuleViolationException>(() =>
            handler.Handle(new RenewReferralLinkSelfServeCommand(neverSent.ReferrerId, noPortalToken), CancellationToken.None));

        // ...but a configured fallback origin works.
        await SelfServe(tokens, new() { ["ReferralLinks:PortalBaseUrl"] = "https://configured.test" })
            .Handle(new RenewReferralLinkSelfServeCommand(neverSent.ReferrerId, noPortalToken), CancellationToken.None);
        Assert.StartsWith("https://configured.test/r/", _smsLinks.Last());
    }

    // ── what the portal is told, and the status screen ──────────────────────────

    [Fact]
    public async Task Check_TellsAnExpiredRenewableLinkApartFromARevokedOrForgedOne()
    {
        var tokens = Tokens();
        var p = AddPartner("DR A");
        AddRow(p, TimeSpan.FromDays(30), version: 1);
        await Context.SaveChangesAsync();

        async Task<ReferralLinkState> State(string t) => await ReferralLinkAccess.CheckAsync(Context, tokens, t, p.ReferrerId, CancellationToken.None);

        Assert.Equal(ReferralLinkState.Valid, await State(TokenExpiringIn(tokens, p.ReferrerId, 1, TimeSpan.FromDays(10))));
        Assert.Equal(ReferralLinkState.Expired, await State(TokenExpiringIn(tokens, p.ReferrerId, 1, TimeSpan.FromDays(-1))));
        Assert.Equal(ReferralLinkState.Invalid, await State(TokenExpiringIn(tokens, p.ReferrerId, 0, TimeSpan.FromDays(-1))));   // expired AND revoked
        Assert.Equal(ReferralLinkState.Invalid, await State(TokenExpiringIn(tokens, Guid.NewGuid(), 1, TimeSpan.FromDays(-1))));
        Assert.Equal(ReferralLinkState.Invalid, await State("garbage"));
        Assert.Equal(ReferralLinkState.Invalid, await State(""));
    }

    [Fact]
    public async Task LinkStatus_ListsThisCentresPartnersOnly()
    {
        var mine = AddPartner("MINE");
        var theirs = AddPartner("THEIRS", hospital: Guid.NewGuid());
        AddRow(mine, TimeSpan.FromDays(40));
        AddRow(theirs, TimeSpan.FromDays(40));
        await Context.SaveChangesAsync();

        var list = await new GetReferralLinkStatusQueryHandler(Context).Handle(new GetReferralLinkStatusQuery(), CancellationToken.None);

        var only = Assert.Single(list);
        Assert.Equal(mine.ReferrerId, only.ReferrerId);
        Assert.Equal("whatsapp", only.LastSentChannel);
        Assert.True(only.AutoRenew);
    }
}
