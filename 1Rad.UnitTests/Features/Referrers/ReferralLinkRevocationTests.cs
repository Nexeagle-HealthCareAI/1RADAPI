using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Common;
using _1Rad.Application.Features.Referrers.Commands.RevokeReferralLinks;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Exceptions;
using _1Rad.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace _1Rad.UnitTests.Features.Referrers;

/// <summary>
/// Doctor-portal links: signed, stateless tokens that carry a per-partner version so a
/// partner's links can be revoked individually and instantly.
/// </summary>
public class ReferralLinkRevocationTests : BaseHandlerTest
{
    private const string Secret = "unit-test-secret-0123456789-abcdefghij";

    private static ReferralLinkTokenService NewTokens() =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Secret"] = Secret }).Build());

    // A token exactly as the service minted them BEFORE versions existed (24-byte payload).
    private static string LegacyToken(Guid referrerId, TimeSpan ttl)
    {
        var payload = new byte[24];
        referrerId.TryWriteBytes(payload.AsSpan(0, 16));
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(16, 8), DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeSeconds());
        var domain = Encoding.ASCII.GetBytes("REFERRAL_LINK_V1");
        var buf = domain.Concat(payload).ToArray();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
        string B64(byte[] d) => Convert.ToBase64String(d).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64(payload)}.{B64(hmac.ComputeHash(buf))}";
    }

    private Referrer AddReferrer(string name, Guid? mergedInto = null, Guid? hospital = null)
    {
        var r = new Referrer { ReferrerId = Guid.NewGuid(), HospitalId = hospital ?? HospitalId, Name = name, MergedIntoId = mergedInto };
        Context.Referrers.Add(r);
        return r;
    }

    // ── the token itself ────────────────────────────────────────────────────────

    [Fact]
    public void Token_IsValidOnlyForItsPartner_ItsVersion_AndBeforeExpiry()
    {
        var tokens = NewTokens();
        var id = Guid.NewGuid();
        var token = tokens.Issue(id, version: 2);

        Assert.True(tokens.Validate(token, id, 2));
        Assert.False(tokens.Validate(token, id, 3));                 // partner's links were revoked since
        Assert.False(tokens.Validate(token, id, 0));
        Assert.False(tokens.Validate(token, Guid.NewGuid(), 2));     // someone else's id
        Assert.False(tokens.Validate(tokens.Issue(id, 2, TimeSpan.FromSeconds(-5)), id, 2));   // expired
    }

    [Fact]
    public void Token_RejectsTamperingAndGarbage()
    {
        var tokens = NewTokens();
        var id = Guid.NewGuid();
        var token = tokens.Issue(id, 1);
        var parts = token.Split('.');

        Assert.False(tokens.Validate(parts[0] + "." + parts[1][..^2] + "AA", id, 1));      // signature altered
        Assert.False(tokens.Validate("not-a-token", id, 1));
        Assert.False(tokens.Validate("", id, 1));
    }

    [Fact]
    public void LegacyTokens_IssuedBeforeVersionsExisted_StayValidAtVersionZero_AndDieOnFirstRevocation()
    {
        var tokens = NewTokens();
        var id = Guid.NewGuid();
        var legacy = LegacyToken(id, TimeSpan.FromDays(30));

        Assert.True(tokens.Validate(legacy, id, 0));     // nothing breaks when this ships
        Assert.False(tokens.Validate(legacy, id, 1));    // ...but a revocation kills every old link
    }

    // ── revoking ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Revoke_InvalidatesOldLinks_AndAFreshLinkWorksUnderTheNewVersion()
    {
        var tokens = NewTokens();
        var doctor = AddReferrer("DR A");
        await Context.SaveChangesAsync();
        var oldLink = tokens.Issue(doctor.ReferrerId, await ReferralLinkVersions.GetOneAsync(Context, doctor.ReferrerId, CancellationToken.None));

        Assert.True(await ReferralLinkAccess.IsValidAsync(Context, tokens, oldLink, doctor.ReferrerId, CancellationToken.None));

        var result = await new RevokeReferralLinksCommandHandler(Context)
            .Handle(new RevokeReferralLinksCommand(doctor.ReferrerId), CancellationToken.None);

        Assert.Equal(1, result.Version);
        Assert.False(await ReferralLinkAccess.IsValidAsync(Context, tokens, oldLink, doctor.ReferrerId, CancellationToken.None));

        var freshLink = tokens.Issue(doctor.ReferrerId, await ReferralLinkVersions.GetOneAsync(Context, doctor.ReferrerId, CancellationToken.None));
        Assert.True(await ReferralLinkAccess.IsValidAsync(Context, tokens, freshLink, doctor.ReferrerId, CancellationToken.None));
    }

    [Fact]
    public async Task Revoke_CanBeRepeated_EachTimeKillingTheLinksMintedSinceTheLast()
    {
        var tokens = NewTokens();
        var doctor = AddReferrer("DR A");
        await Context.SaveChangesAsync();
        var handler = new RevokeReferralLinksCommandHandler(Context);

        await handler.Handle(new RevokeReferralLinksCommand(doctor.ReferrerId), CancellationToken.None);
        var second = tokens.Issue(doctor.ReferrerId, 1);
        var again = await handler.Handle(new RevokeReferralLinksCommand(doctor.ReferrerId), CancellationToken.None);

        Assert.Equal(2, again.Version);
        Assert.False(await ReferralLinkAccess.IsValidAsync(Context, tokens, second, doctor.ReferrerId, CancellationToken.None));
    }

    [Fact]
    public async Task Revoke_AlsoKillsTheLinksOfPartnersMergedIntoThisOne_AndLeavesOthersAlone()
    {
        var tokens = NewTokens();
        var primary = AddReferrer("DR PRIMARY");
        var dupe = AddReferrer("DR DUPE", mergedInto: primary.ReferrerId);
        var other = AddReferrer("DR OTHER");
        await Context.SaveChangesAsync();
        var dupeLink = tokens.Issue(dupe.ReferrerId, 0);
        var otherLink = tokens.Issue(other.ReferrerId, 0);

        var result = await new RevokeReferralLinksCommandHandler(Context)
            .Handle(new RevokeReferralLinksCommand(primary.ReferrerId), CancellationToken.None);

        Assert.Equal(2, result.LinksInvalidatedFor);
        // The duplicate's portal shows the primary's data — its old link must not survive.
        Assert.False(await ReferralLinkAccess.IsValidAsync(Context, tokens, dupeLink, dupe.ReferrerId, CancellationToken.None));
        Assert.True(await ReferralLinkAccess.IsValidAsync(Context, tokens, otherLink, other.ReferrerId, CancellationToken.None));
    }

    [Fact]
    public async Task Revoke_RefusesAnUnknownDeletedOrOtherCentresPartner()
    {
        var mine = AddReferrer("DR GONE");
        mine.DeletedAt = DateTime.UtcNow;
        var elsewhere = AddReferrer("DR ELSEWHERE", hospital: Guid.NewGuid());
        await Context.SaveChangesAsync();
        var handler = new RevokeReferralLinksCommandHandler(Context);

        await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(new RevokeReferralLinksCommand(Guid.NewGuid()), CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(new RevokeReferralLinksCommand(mine.ReferrerId), CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(new RevokeReferralLinksCommand(elsewhere.ReferrerId), CancellationToken.None));
    }

    [Fact]
    public async Task Access_RejectsAMissingTokenAndAnUnknownPartnerWithoutThrowing()
    {
        var tokens = NewTokens();
        Assert.False(await ReferralLinkAccess.IsValidAsync(Context, tokens, null, Guid.NewGuid(), CancellationToken.None));
        Assert.False(await ReferralLinkAccess.IsValidAsync(Context, tokens, "  ", Guid.NewGuid(), CancellationToken.None));
    }
}
