# 1Rad / EasyRad — Backend Security & Architecture Review

_Generated: 2026-06-14 · Scope: backend API & auth (`1RadAPI`, `1Rad.Application`, `1Rad.Infrastructure`), plus a light secrets/config pass and a scalability/system-design review. Method: targeted source review of auth, authorization/multi-tenant isolation, public/anonymous endpoints, signed tokens, file upload/SAS, injection, config/secrets, and core infrastructure. Static analysis only (no runtime testing). Findings cite `file:line`; each carries a confidence level._

> **Read this first.** Three issues are **Critical** and need action now: (1) live production credentials are committed to the repo, (2) OTPs are stored and compared in plaintext, and (3) two endpoints allow cross-tenant read/write of other hospitals' data. Details and exact locations below.

---

## Severity summary

| Severity | Count | Theme |
|---|---|---|
| Critical | 3 | Committed secrets; plaintext OTP; cross-tenant IDOR |
| High | 8 | JWT fallback key; dead subscription gate; no brute-force protection; login enumeration; public PHI container; unvalidated uploads; open prescriptions proxy; OTP-login bypasses session policy |
| Medium | ~12 | Sparse role checks; long-lived bearer tokens; PII over-exposure; Swagger in prod; no HSTS; no global rate limit; plaintext refresh tokens; weak input validation; SAS lifetime; idempotency race |
| Low / Info | ~7 | AllowedHosts `*`; weak OTP RNG; OTP purpose scoping; path-only asset signing; CORS note |
| Scalability / design | 9 | In-memory caches block scale-out; load-everything finance queries; missing pagination; god DbContext; hosted jobs without distributed lock |

A **Strengths** section near the end lists the (many) things this codebase does well — the architecture is fundamentally sound; most findings are gaps within an otherwise solid design.

---

# CRITICAL

## C1 — Live production credentials committed to source control
**Files:** `1RadAPI/appsettings.Development.json:10-23`; CI exemption `azure-pipelines.yml:80,88-94`; `.gitignore` does **not** ignore `appsettings*.json`. **Confidence: Confirmed.**

The Development config contains **real, working secrets**, and the file is tracked by git (no ignore rule):
- Azure SQL connection string **with password** (`...Password=@Change25;`) for `easyhmserver.database.windows.net` / `1RadDatabase`
- Azure Blob Storage **AccountKey** (full 88-char key) for account `1radstorage`
- **JWT signing `Secret`** (full HS256 key)
- Gmail SMTP **app password**
- WhatsApp / Meta Graph **access token**

Worse, the CI secret-scan explicitly **excludes** `appsettings.Development.json` (`$_.Name -ne 'appsettings.Development.json'`) and a `SkipSecretScan=true` switch bypasses the gate entirely — so the one file holding live secrets is the one not scanned.

**Impact:** Anyone with repo read access can: forge valid JWTs for any user/role/tenant (the signing key is symmetric HS256 — see C-context and H1), read/write/delete the clinical SQL database (PHI), read/write/delete patient prescription & DICOM blobs (storage key), send mail as the practice, and send WhatsApp messages from the business number. This is a full compromise of confidentiality and integrity.

**Recommendation (do now):** Treat all five as compromised and **rotate immediately** — SQL password, regenerate Storage account key, new JWT secret (this logs everyone out, intended), revoke the Gmail app password, rotate the Meta token. Remove `appsettings.Development.json` from the repo, add `appsettings.Development.json` to `.gitignore`, and **purge it from git history** (`git filter-repo`/BFG). Use .NET User Secrets locally. Remove the CI scan exemption. _(Good news: `appsettings.Production.json` is clean — secrets are `null` and injected from Azure DevOps variable groups at deploy.)_

## C2 — OTPs stored and compared in plaintext (registration/login OTP flow)
**Files:** `1Rad.Application/Features/Auth/Commands/SendOTP/SendOTPCommandHandler.cs:44-48`; `.../VerifyOTP/VerifyOTPCommandHandler.cs:42`. **Confidence: Confirmed.**

The hashing call is commented out — `// var hash = _hasher.Hash(otp);` then `var hash = otp;` — so the raw 6-digit code is persisted into the column named `CodeHash`. Verification does a plain string compare (`request.Code.Equals(verification.CodeHash, ...)`), not `_hasher.Verify`. (The **password-reset** OTP path does this correctly via `OtpService` + `_hasher.Verify`, so the two flows are inconsistent.)

**Impact:** Any read of the `OTPVerifications` table (DB compromise — see C1 — backup leak, or an over-broad query) exposes live OTPs in cleartext during the login/registration window → account takeover. The misleading column name will also trip up future maintainers.

**Recommendation:** Restore `_hasher.Hash(otp)` on write and `_hasher.Verify(request.Code, verification.CodeHash)` on read, matching the reset-password flow.

## C3 — Cross-tenant read & write of other hospitals' data (BOLA / IDOR)
**Confidence: Confirmed.** Two endpoints operate on objects that are **not** tenant-filtered because their entities don't implement `IHospitalContext` (so the global query filter at `ApplicationDbContext.cs:1142-1158` never applies), and the handlers never compare against the caller's `HospitalId`:

- **Any hospital's full profile — read & overwrite.** `HospitalsController.cs:23-46` → `GetHospitalDetailsQueryHandler.cs:19-72` / `UpdateHospitalDetailsCommandHandler.cs:21-39`. The route `id` is used unmodified. GET returns another centre's staff PII (names, emails, mobiles, license numbers), GSTIN/PAN/registration, and patient count. PUT overwrites another centre's legal/tax identity (name, GSTIN, PAN, NABH, auto-billing flag). `Hospital : BaseEntity` (not `IHospitalContext`).
- **Any user's clinical credentials — overwrite.** `PersonnelController.cs:73-85` → `UpdateClinicalCredentialsCommandHandler.cs:26-43`. No `HospitalId` on the command; loads `Users` by id and overwrites `Specialization/Degree/LicenseNo`. `User : BaseEntity` (not `IHospitalContext`), and unlike `UpdateStaffCommandHandler` it does no `UserHospitalMapping` check.

**Impact:** Any authenticated user of any tenant can enumerate hospital GUIDs and read/modify other practices' data, including regulatory-sensitive radiologist license numbers. Classic broken object-level authorization on the tenant-root objects.

**Recommendation:** Resolve the hospital from the `cid` claim and ignore the route id (or reject when `id != userContext.HospitalId` / not in the authorized-hubs set). For clinical-credentials, require a `UserHospitalMapping` for `(targetUserId, callerHospitalId)` (mirror `UpdateStaffCommandHandler.cs:23-28`) and restrict to an admin role. Add a startup assertion/test that **every entity with a `HospitalId` implements `IHospitalContext`** — this class of bug recurs whenever that's forgotten.

---

# HIGH

## H1 — Hardcoded fallback JWT secret in `JwtProvider`
**File:** `1Rad.Infrastructure/Authentication/JwtProvider.cs:115,157`. **Confidence: Confirmed.**
`var secretKey = _configuration["Jwt:Secret"] ?? "a_very_long_and_secure_secret_key_for_1rad_api_development_2026";`. `Program.cs` correctly fails fast if the secret is missing, but `JwtProvider` (both the mint path and the independent validate path used by password reset) silently falls back to a public string baked into the binary. If config ever fails to bind (the codebase elsewhere warns about the Linux `:`→`__` keying pitfall), the API would issue and accept tokens signed with an attacker-known key → token forgery. **Fix:** remove both fallbacks; throw.

## H2 — Subscription lock/expiry enforcement is dead code (wrong claim name)
**Files:** `1RadAPI/Middleware/SubscriptionValidationMiddleware.cs:41-42`; token minted with claim `"cid"` (`JwtProvider.cs:54`); `UserContext` reads `"cid"` (`UserContext.cs:30`). **Confidence: Confirmed.**
The middleware reads `context.User.FindFirst("HospitalId")` — a claim that is never issued (it's `"cid"`). So the parse fails, the block is skipped, and every request proceeds. **The entire "subscription expired / Locked → 402" gate never fires** — tenants with expired or locked subscriptions keep full access. (Module gating via `RequiresModule` is unaffected; it reads `cid`.) **Fix:** resolve the hospital from `IUserContext`/`"cid"`; add a test asserting a locked subscription returns 402.

## H3 — No brute-force protection on login or OTP verification
**Files:** `AuthController.cs:125-128` (login has **no** `[EnableRateLimiting]`); `Program.cs:79-90` (OTP limiter is fixed-window, not partitioned by identifier); `VerifyOTPCommandHandler.cs:31-46` and `OTPVerification` entity (no attempt counter). **Confidence: Confirmed.**
Password login is throttled by nothing. The 6-digit OTP (10⁶ space, 5-min validity) has no per-code attempt cap and the rate limit is per-request/global, not per target mobile/email — an attacker rotating IPs bypasses it, and the code is realistically guessable. **Fix:** add a login rate-limit policy + per-account failed-login lockout; add `AttemptCount` to `OTPVerification` (expire after ~5 wrong tries); partition the OTP limiter by normalized identifier.

## H4 — Account enumeration + timing oracle on login
**File:** `LoginCommandHandler.cs:58-66` vs `95-103`. **Confidence: Confirmed.**
Unknown identity returns `USER_NOT_FOUND`, wrong password returns `INVALID_CREDENTIALS`, inactive returns `ACCOUNT_INACTIVE` (+ status), plus a `PASSWORD_NOT_SET` state — so an attacker can enumerate which emails/mobiles are registered and their status. The no-user path also skips BCrypt, creating a timing oracle. (`forgot-password` already handles this correctly.) **Fix:** return one generic credential error for all cases; run a dummy BCrypt verify on the no-user path.

## H5 — DICOM/PHI blob container created with public-read access
**File:** `1Rad.Infrastructure/Services/AzureBlobService.cs:132-146` (`CreateIfNotExistsAsync(PublicAccessType.Blob)`), used for `dicom-files` (the declared `PhiContainer`). **Confidence: Likely (depends on the account's "allow blob public access" setting).**
The PHI container is requested as **public-read**. If the storage account permits public containers, every DICOM blob is anonymously readable by URL (paths are predictable: `{hospitalId}/{appointmentId}/{stamp}_{guid}_{file}`), bypassing the `proxy-asset` capability checks entirely. The only thing standing between this and a PHI breach is an account-level firewall setting outside the code. **Fix:** create `dicom-files` and `staff-documents` with `PublicAccessType.None` unconditionally; serve reads only via SAS or the authenticated proxy. Reserve public-read for a genuinely-public branding container.

## H6 — No server-side file-type/content validation on uploads; client-controlled content-type
**Files:** `StudyController.cs:930-1052,1080-1162,1164-1282`; `AzureBlobService.cs:41,198`. **Confidence: Confirmed.**
The only gate is whether a `.zip/.dcm/.dicom` extension requires PACS; any other extension (`.html`, `.svg`, `.js`, `.exe`) is uploaded unchecked, and the blob's `Content-Type` is taken verbatim from the client. No magic-byte sniffing, no allowlist, no malware scan. Combined with H5 (public/served-back blobs), a malicious `.html`/`.svg` becomes **stored XSS** served from the storage/CDN origin. (Note: the staff **photo** upload path *does* validate against an image allowlist — use it as the model.) **Fix:** enforce a server-side type allowlist by extension **and** magic bytes; override `Content-Type` server-side; add `X-Content-Type-Options: nosniff` and `Content-Disposition: attachment` on proxied responses.

## H7 — Anonymous proxy of any "prescriptions" blob + weak host allowlist on `proxy-asset`
**Files:** `StudyController.cs:72,2236-2254`. **Confidence: Likely / Confirmed (weak check).**
Two issues on the `[AllowAnonymous]` `GET /study/proxy-asset`:
1. **Open container:** any blob whose first path segment is `prescriptions` is served with **no signature, no token, no auth, for every tenant** (`OpenProxyContainers = { "prescriptions" }`). `prescriptions` is also the **default** container/upload target in `AzureBlobService`, so patient prescription PDFs/letterheads may live there and be anonymously readable by anyone who can guess/enumerate the GUID path.
2. **Substring host check:** the origin guard is `url.Contains("1radstorage") && url.Contains(".blob.core.windows.net")` — not a real host allowlist. URLs like `https://evil.com/?x=1radstorage.blob.core.windows.net` pass.

**SSRF is not currently exploitable** (verified: `AzureBlobService.DownloadFileAsync` ignores the URL host and resolves container+blob against the configured account, so `?url=http://169.254.169.254/...` can't reach metadata) — but the check provides no real protection and is one refactor away from live SSRF. **Fix:** require a valid signature for *all* proxied reads (no unconditionally-open container); parse the URL and compare `uri.Host` to an exact allowlist; require HTTPS; reject `userinfo`.

## H8 — OTP login path bypasses the session-management policy
**File:** `VerifyOTPCommandHandler.cs:62-116`. **Confidence: Confirmed.**
OTP-based login mints a full 24h access + 7-day refresh token with `DeviceCategory = "UNKNOWN"` and, unlike password login, does **not** revoke prior same-category sessions, enforce the one-per-category/max-three cap, or emit a new-sign-in alert. It's a parallel full-login path that silently accumulates sessions and weakens takeover detection (and, with C2, is the weakest takeover link). **Fix:** route OTP login through the same session-policy code as `LoginCommandHandler`.

---

# MEDIUM

- **M1 — Sparse backend role enforcement (function-level authZ).** `FinanceController` destructive actions (`DeleteInvoice/DeleteExpense/GenerateInvoice/ApplyDiscount/...`) and most staff/personnel mutations are gated only by `[Authorize]` + module — only 4 `[Authorize(Roles=...)]` exist in the whole Controllers folder. Any authenticated user in a tenant can delete invoices, apply discounts, or (via `RegisterStaff`, which accepts arbitrary `RoleNames` from the body with no grantable-role allowlist) potentially assign a high-privilege role → **intra-tenant privilege escalation** if the frontend is the only guard. _Files: `FinanceController.cs:90-247`, `RegisterStaffCommandHandler.cs:29-46`._ **Fix:** add role-based authorization + an allowlist of grantable roles.
- **M2 — Capability tokens live 365 days, bearer-style, replayable, single shared key.** Tracking & referral tokens default to **1 year** (`TrackingTokenService.cs:24`, `ReferralLinkTokenService.cs:19`); the URL *is* the credential, travels in QR codes/history/logs, and has no revocation except rotating `Jwt:Secret` (which also logs out every user, since all tokens share that one secret). **Fix:** hours/days TTLs, per-token revocation (version/epoch), and a separate signing key per purpose (HKDF-derive).
- **M3 — Anonymous doctor portal exposes full PII/financials despite a "masking" comment.** `GetDoctorPortalQuery.cs:104,127-129` returns full patient `FullName` (the code comment claims initials+PTID masking that isn't implemented), plus PTID, modality, dates, per-patient commissions, and the admin's name/email/mobile. With M2's 1-year link, a single leaked URL discloses a referrer's entire patient population. **Fix:** implement the masking or consciously accept it; minimize admin PII; tighten TTL.
- **M4 — Swagger UI exposed in Production + `/` redirects to it.** `Program.cs:214-227` (no `IsDevelopment()` guard). Full API surface enumerable anonymously. **Fix:** gate behind dev/auth/IP allowlist.
- **M5 — No HSTS.** `Program.cs:229` has `UseHttpsRedirection()` but no `UseHsts()` → first-request/downgrade MITM risk for tokens/PHI. **Fix:** add `UseHsts()` outside Development.
- **M6 — No global rate limiter.** `Program.cs:79-90,233` defines only the OTP policy; login/search/upload/AI endpoints are unthrottled → brute force, scraping, and cost-amplification on Anthropic/Gemini/WhatsApp. **Fix:** add a per-IP/per-user `GlobalLimiter`.
- **M7 — Refresh tokens stored in plaintext.** `RefreshTokenCommandHandler.cs:42` looks up by raw token value; a DB/backup read yields usable 7-day session tokens. **Fix:** store/look-up by SHA-256 hash. (Rotation & revocation-on-refresh are otherwise correctly done.)
- **M8 — 24h access token + reset doesn't evict the session cache.** `JwtProvider.cs:88`; `ResetPasswordCommandHandler.cs:59-66` removes refresh tokens but not the active-session cache entry, so a stolen access token whose `sid` is cached survives a password reset until cache expiry. **Fix:** shorten access token to ~15-60 min; evict sessions on reset.
- **M9 — Recovery endpoints unthrottled; reset JWT replayable.** `AuthController.cs:195-223` (no rate limit); the 5-min reset JWT is stateless with no single-use `jti`, so it can be replayed within its window. **Fix:** rate-limit all three; track a one-time `jti`.
- **M10 — No systematic input validation.** `1Rad.Application/DependencyInjection.cs:9-16` registers FluentValidation but **no MediatR `ValidationBehavior` runs it**; only 4 Auth validators exist. Non-auth commands (patient mobile/age/email, salary decimals, roles, dates) reach handlers/DB unvalidated. **Fix:** add a `ValidationBehavior<,>` pipeline and author validators for create/update commands.
- **M11 — SAS write tokens up to 6h + client content-type; complete/register skip the module+type gate.** `StudyController.cs:339-344,1164-1282,1333-1445`; `AzureBlobService.cs:184-201`. SAS is correctly blob-scoped & tenant-pathed (good), but the generous lifetime and client-set content-type reinforce H6, and `/upload-complete`/`/register` re-derive file type from a *client-supplied* filename, partially bypassing PACS-module/quota gating. **Fix:** cap SAS at 30-60 min; pin content-type server-side; re-validate type/module/quota at finalize.
- **M12 — Idempotency middleware is response-replay, not execute-once.** `IdempotencyMiddleware.cs:89-195`: lookup-then-insert isn't atomic, so two concurrent retries can both execute the handler (only the second insert fails on the unique key). Safe for the upsert-based Study handlers; risky for any future non-idempotent handler that assumes once-only. **Fix:** insert a "pending" row keyed unique *before* invoking the handler; document the guarantee.

---

# LOW / INFO

- **L1 — `AllowedHosts: "*"` in Production** (`appsettings.Production.json:23`) — set to the real hostname(s).
- **L2 — Weak OTP RNG** — `new Random().Next(...)` (`SendOTPCommandHandler.cs:44`, `OtpService.cs:39`); use `RandomNumberGenerator.GetInt32`. (Refresh tokens already use crypto RNG.)
- **L3 — OTP verify not scoped by `Purpose`** (`VerifyOTPCommandHandler.cs:31-34`) — add `Purpose` to the lookup to prevent cross-purpose acceptance.
- **L4 — `AssetUrlSigner` signs path+exp but not host** (`AssetUrlSigner.cs:35,51-55`) — defense-in-depth only; currently unused on the read path.
- **L5 — Distinguishable OTP-verify error messages** (`VerifyOTPCommandHandler.cs:39 vs 45`) — minor enumeration oracle; return one message.
- **L6 — In-process session cache + static last-seen map** — node-local revocation (see Scalability S2).
- **I1 — CORS** uses an explicit origin allowlist with `AllowCredentials()` (the safe pattern, no wildcard); verify the Production origin host is current and drop `AllowCredentials()` if the SPA uses Bearer headers, not cookies.
- **I2 — Exception middleware** correctly hides stack traces/SQL in Production (gated on `IsDevelopment()`); just confirm `ASPNETCORE_ENVIRONMENT=Production` is set in the App Service.

---

# Scalability & system-design gaps

The app is explicitly written for single-instance today, with several interfaces pre-abstracted for a later Redis swap. The items below are what must change to run **multiple instances** safely and to keep the data layer healthy as data grows.

### S1 — In-memory caches are scale-out blockers (RadAI cache & active-session cache)
`RadAiResponseCache` (`RadAiResponseCache.cs:12-22`, singleton over `IMemoryCache`) and `ActiveSessionCache` (`ActiveSessionCache.cs:23-53`) are process-local. On N instances: AI cache hit-rate collapses to ~1/N (re-paying Anthropic/Gemini per node), and session **revocation/forced-logout doesn't propagate across instances** (the DB is authoritative, but cross-instance consistency/latency is uneven, and the DB-fallback path on cache miss should be load-tested). **Fix:** back both with a distributed cache (Redis) before scaling out — the interfaces already allow a registration-only swap. **[Critical for scale-out]**

### S2 — "Load everything, aggregate in memory" finance/intelligence queries
`GetFinancialMatrix` (`GetFinancialMatrixQuery.cs:212-741`), `GetFinanceStats` (`GetFinanceStatsQuery.cs:41-71`), `GetReferralIntelligence` (`GetReferralIntelligenceQuery.cs:63-82`, with per-row correlated subqueries), and `GetStrategicOutlook` (`GetStrategicOutlookQuery.cs`) pull a tenant's **entire** invoice/expense/payment/commission/patient history into memory with **no date defaults**, then do all GroupBy/Sum/cohort/AR-aging math as LINQ-to-Objects. These get permanently slower as data grows and multiply DB load across instances. **Fix:** push aggregations into SQL (`GROUP BY` projections, `SumAsync`/`CountAsync`), enforce a default date window, batch-load instead of per-row subqueries. **[High]**

### S3 — `GetStrategicOutlook` swallows all exceptions → silent zeros
`GetStrategicOutlookQuery.cs:405-419` (plus inner swallows) returns a zero-filled DTO on any exception, so a timeout/SQL error renders a dashboard of zeros indistinguishable from "no data." **Fix:** let it fail/log structured errors; remove blanket `catch`. **[High — observability]**

### S4 — No pagination on list endpoints
`GetAppointments` (`GetAppointmentsQuery.cs:30-248`) returns the full filtered worklist (no `Skip/Take`); same shape elsewhere. The N+1 was correctly fixed and it uses `AsNoTracking`, but payload grows with total volume. **Fix:** cursor pagination on `(Priority, DateTime, AppointmentId)` or a default page cap. **[Medium]**

### S5 — Hosted background jobs run on every instance without a distributed lock
`SubscriptionLifecycleJob`, `DailyFinancialReportJob`, `DailyReferralExcelReportJob`, `BlobOrphanSweepJob` (`DependencyInjection.cs:91-94`) are `BackgroundService`s with no leader election. On N instances they all fire — duplicating emails, redundant blob listing/deletes, and the PACS auto-delete side effects. **Fix:** gate behind a distributed lease/singleton-row claim (the DICOM worker already does this well) or run scheduled work in one dedicated worker. **[Medium]**

### S6 — God `DbContext` and non-pooled registration
`ApplicationDbContext.cs` owns ~45 `DbSet`s in 1383 lines across every bounded context, with an 8-pass ChangeTracker loop per `SaveChanges` (`:1291-1356`); registered via `AddDbContext` (scoped), not pooled (`DependencyInjection.cs:16-29`). **Fix:** split into bounded-context contexts (or at least `IEntityTypeConfiguration` files); collapse the timestamp loop to one pass via an `IHasUpdatedAt` interface; evaluate `AddDbContextPool`. **[Medium — maintainability]**

### S7 — Two controllers bypass MediatR (layering inconsistency)
`StudyController` and `PrescriptionController` inject `IApplicationDbContext` and run multi-step persistence directly in actions, skipping the CQRS/validation/idempotency conventions used everywhere else. (It's against the interface, not the concrete type, so not a Clean-Architecture dependency violation — just inconsistency.) **Fix:** move persistence into commands/queries. **[Medium]**

### S8 — OTP table grows unbounded
`OtpService.cs:33-86` is insert-only with no sweeper (the idempotency table *does* self-purge). **Fix:** add a cleanup sweep. **[Low]**

---

# Strengths (what's done well)

- **Multi-tenancy** is enforced centrally via dynamic global query filters on `IHospitalContext` (24 entities); tenant is taken from the `cid` claim, never from request input; `SwitchContext` verifies membership before re-scoping; `RequiresModule` reads the claim and isn't bypassable. (The C3 gaps are exactly the entities that step outside this framework.)
- **DICOM extraction is genuinely distributed & durable** — DB-backed job state with `READPAST + UPDLOCK` leasing, heartbeats, lease-reclaim, and persisted retries/backoff. Textbook scale-safe.
- **Idempotency** is DB-backed and shared across instances (composite `(Key, UserId)`, 24h TTL, success-only caching, method/path mismatch → 422).
- **Crypto hygiene**: BCrypt workFactor 12; refresh-token rotation + revocation; 64-byte crypto-RNG refresh tokens; timing-safe HMAC compares (`FixedTimeEquals`); token id-binding (no id-swap); expiry embedded & verified; share-link 24h TTL enforced server-side.
- **JWT validation** validates issuer/audience/lifetime/signing-key and fails fast on missing secret (the only fallback problem is inside `JwtProvider`, H1).
- **Production config is clean** — secrets injected from Azure DevOps variable groups, not committed.
- **Exception middleware** never leaks stack traces/SQL to clients in Production.
- **No SQL injection found** — all data access is parameterized LINQ; the one raw query (session validation) is parameterized.
- **Resilience**: `EnableRetryOnFailure`, optimistic concurrency (`RowVersion`) on contended aggregates, gap-free sequence generator, consistent `AsNoTracking` on reads.
- **Forward-looking**: `IActiveSessionCache`/`IRadAiResponseCache` are pre-abstracted so the Redis migration is a registration change, not a rewrite.

---

# Prioritized remediation plan

1. **Now (Critical):** Rotate all five committed secrets and purge `appsettings.Development.json` from history; fix `.gitignore` + CI scan exemption (C1). Restore OTP hashing (C2). Add tenant checks to `HospitalsController` and clinical-credentials, and make `Hospital`/`User` tenant-aware (C3).
2. **This week (High):** Remove the JWT fallback key (H1); fix the subscription-lock claim name + add a test (H2); add login/OTP brute-force protection (H3); de-enumerate login (H4); set DICOM/staff containers to `PublicAccessType.None` (H5); add upload type/content validation (H6); require signatures on all proxied reads + real host allowlist (H7); route OTP login through session policy (H8).
3. **This month (Medium):** Backend role authorization on destructive/financial/staff endpoints + grantable-role allowlist (M1); shorten capability-token TTLs + per-purpose keys (M2); implement doctor-portal masking (M3); gate Swagger + add HSTS + global rate limiter (M4-M6); hash refresh tokens (M7); add a validation pipeline (M10).
4. **Before scale-out:** Redis-back the two in-memory caches (S1); convert load-everything finance/intelligence queries to SQL aggregates with date bounds (S2); add a distributed lock to hosted jobs (S5); add pagination (S4); stop swallowing exceptions in StrategicOutlook (S3).

---

### Caveats
Static review only — no runtime/penetration testing. Items marked **Likely / Needs-verify** depend on deployment settings (e.g., H5 on the storage account's public-access setting; M1 escalation on whether any authorization sits outside the reviewed controllers). Frontend, the DICOM bridge, and non-`1RadAPI` services were out of the agreed scope. Secret values were not reproduced in this document; rotate based on the named locations.
