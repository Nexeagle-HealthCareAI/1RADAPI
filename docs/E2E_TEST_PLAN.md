# 1Rad End-to-End Test Plan — SKU split, tiers/PAYG, Cloud PACS Phase 2

Scope: everything currently uncommitted across the five repos — editions (RIS /
Cloud PACS / RIS+PACS), tier catalog + per-study PAYG, user/site caps, storage
metering, appointment-free PACS (ingest → match → browse → view → report →
delete/export), downgrade lifecycle, registration picker, Posture A delivery.

Execute phases in order — later phases depend on earlier state. Each test has a
checkbox; record failures with the test ID.

---

## Phase 0 — Pre-flight (environment)

- [ ] **0.1 DB pipeline green.** Push oneRadDB → Dev deploy runs `01…76` +
      `bundle` + `data` with exit 0. Watch specifically: `67` prints
      "+ Added column …Modules", `68` prints table + index creations + backfill
      counts, `70–76` all print their `+`/`=` lines.
- [ ] **0.2 Catalog seeded.** `SELECT Edition, Tier, Name, Price, BillingMode,
      MaxUsers, MaxSites, IncludedStorageGb FROM SubscriptionPlans WHERE
      IsActive=1 ORDER BY Edition, Price;` → 24 rows (9 tiers × 2 cycles + 3
      PAYG + 3 Chain), legacy GUID plans `A1B2…`/`B2C3…` have `IsActive=0`.
- [ ] **0.3 Backend deployed** with config: `AzureBlobStorage:CdnBaseUrl` =
      Front Door host; `Pacs:GraceDays` (default 30 — set to `0` or `1` on the
      test environment for Phase 9); `Pacs:AutoDeleteEnabled` (**default
      true** — confirm intentional per environment).
- [ ] **0.4 Frontend deployed** (Vite build of easyrad).
- [ ] **0.5 Bridge configured** (nexegale-dicom-bridge): API base URL, hospital
      credentials, `PACS_FALLBACK` as desired.
- [ ] **0.6 Test data ready**: two DICOM studies — one whose AccessionNumber
      (0008,0050) will be set to a real appointment id (auto-match), one with a
      foreign/blank accession (manual-assign path). One multi-frame/large study
      (>1 GB) if storage overage is to be exercised for real.

---

## Phase 1 — Registration & package picker

- [ ] **1.1 RIS-only signup.** Register a new center, pick **RIS** in the
      package picker (card shows "from ₹1,999/mo"). → trial subscription
      created with `Modules='RIS'`. Verify: PACS nav (Studies/upload DICOM)
      absent; `POST radiology/upload` family returns 403 module error.
- [ ] **1.2 PACS-only signup.** Same with **Cloud PACS** → `Modules='PACS'`.
      Studies UI present; RIS-only surfaces (referrals, finance) gated off.
- [ ] **1.3 Bundle signup.** **RIS + Cloud PACS** → `Modules='RIS,PACS'`,
      everything visible.
- [ ] **1.4 Cycle toggle** on the picker switches "from" prices to yearly
      (10% off, e.g. RIS from ₹21,589/yr).

DB check after each: `SELECT Modules, IsTrial FROM HospitalSubscriptions WHERE
HospitalId=@h ORDER BY CreatedAt DESC;`

---

## Phase 2 — Plan catalog & pricing UI

- [ ] **2.1 GET `/subscriptions/plans`** (anonymous) returns 24 plans with
      `tier`, `billingMode`, `perStudyPrice`, `maxUsers`, `maxSites`,
      `isCustom`, ordered by edition then price.
- [ ] **2.2 SubscriptionPage → Upgrade tab.** Edition switcher shows RIS /
      Cloud PACS / RIS+PACS with "● current" on the active edition. Tier cards
      show price, user cap, site cap, storage line. Growth is badged POPULAR.
- [ ] **2.3 PAYG card** shows the per-study rate (₹8 / ₹15 / ₹25 by edition);
      **Chain card** shows "Custom / Contact us" and is not selectable.
- [ ] **2.4 Monthly/Yearly toggle** re-prices tier cards (yearly = 10% off,
      shown per year). Known quirk: toggling resets selection to Starter.
- [ ] **2.5 Summary bar** shows "You selected <edition> · <tier>" and the
      estimate total; for a tier with storage overage it appends
      "(incl. ₹N storage overage)".

---

## Phase 3 — Payment request → approval lifecycle

- [ ] **3.1 Submit for a tier.** Select PACS Growth Monthly → Submit Payment →
      drawer prefilled with the estimate amount → submit. Verify request row:
      `SELECT PlanId, Amount, Modules, StorageOverageGb FROM
      SubscriptionPaymentRequests ORDER BY CreatedAt DESC;` — server-computed
      amount (client amount ignored), Modules='PACS'.
- [ ] **3.2 Admin approve** → new HospitalSubscription has `Modules`,
      `IncludedStorageGb=500`, `MaxUsers=10`, `MaxSites=1`,
      `BillingMode='Subscription'` copied from the plan.
- [ ] **3.3 Entitlement cache.** Within ~60 s of approval the center's module
      access reflects the new subscription without re-login.
- [ ] **3.4 Tier upgrade.** Starter → Growth via a second request/approve →
      caps raised on the new subscription row.
- [ ] **3.5 Downgrade dropping PACS.** Bundle → RIS-only approve →
      `PacsRemovedAt` stamped on the new subscription (grace clock starts).
- [ ] **3.6 Pending state.** While a request is Pending, the upgrade tab shows
      "Payment Under Review" instead of the submit button.

---

## Phase 4 — User & site caps

- [ ] **4.1 User cap blocks.** On RIS Starter (`MaxUsers=2`; note the admin's
      mapping counts): add staff until the cap, then one more → API returns
      `USER_LIMIT_REACHED…`; StaffPage shows the friendly "User limit reached /
      upgrade" notification, not the raw code.
- [ ] **4.2 User cap raised.** Approve Growth (5) → the blocked add now
      succeeds.
- [ ] **4.3 Site cap blocks.** On a `MaxSites=1` plan, AdminBoard → Register
      new chain/centre → `SITE_LIMIT_REACHED` message surfaces in the toast.
- [ ] **4.4 Site cap ok.** On Clinic (`MaxSites=3`) the same flow creates the
      centre and switches into it.
- [ ] **4.5 Unlimited.** On a PAYG or Chain-activated subscription
      (`MaxUsers NULL`) staff adds are never blocked.
- [ ] **4.6 Existing users unaffected.** A downgrade below current headcount
      must not delete/lock existing mappings — only block *new* adds.

---

## Phase 5 — Per-study PAYG

- [ ] **5.1 Switch to PAYG.** Select "Pay per study" → estimate shows
      "0 studies × rate"; submit (₹0) → approve → subscription
      `BillingMode='PerStudy'`, `PerStudyPrice` = edition rate.
- [ ] **5.2 Counter increments.** Finalize 3 reports →
      `GET /subscriptions/status` returns `paygStudiesThisCycle=3`,
      `paygAmountDue = 3 × rate`; SubscriptionPage Current Plan shows the
      "Pay-per-study — this cycle" strip with the same numbers.
- [ ] **5.3 Drafts don't count.** Saved-but-not-finalized reports do not move
      the counter.
- [ ] **5.4 Estimate parity.** `GET /subscriptions/estimate?planId=<payg>`
      matches the status numbers.
- [ ] **5.5 Cycle reset.** After renewal approval (new StartDate) the counter
      restarts from finalizations ≥ new StartDate.

---

## Phase 6 — Storage metering

- [ ] **6.1 Usage accrues.** Upload a study on a PACS tier → hospital usage =
      `SUM(StorageBytes)` grows (original + HTJ2K slices after extraction).
- [ ] **6.2 Overage in estimate.** Push usage past `IncludedStorageGb` (or
      temporarily lower the plan's allowance in DB) → estimate total = base +
      overageGb × ₹50; summary bar shows the overage note.
- [ ] **6.3 Over-quota blocks uploads only.** New DICOM upload rejected with a
      quota error; viewing existing studies still works.
- [ ] **6.4 RIS-only unmetered.** `IncludedStorageGb NULL` → no overage line,
      uploads of PDF/JPG attachments unaffected.

---

## Phase 7 — Cloud PACS Phase 2 (appointment-free flow)

Ingest — web:
- [ ] **7.1 Register study without appointment.** `POST radiology/studies/register`
      (via the upload UI) → ImagingStudy `Status=Received`,
      `MatchStatus=Unmatched` (no accession) or `AutoMatched` (accession =
      appointment id).
- [ ] **7.2 Upload + extraction.** Upload-token → blob upload →
      upload-complete → extraction runs → `Status=Ready`, slices viewable.

Ingest — bridge:
- [ ] **7.3 C-STORE with accession.** Send the auto-match study from a
      modality/Horos to the bridge → per-instance SAS upload → study appears
      `AutoMatched` to the appointment, reports link correctly.
- [ ] **7.4 C-STORE without accession** → lands in the **Unassigned inbox**.

Match & browse:
- [ ] **7.5 Manual assign.** From the inbox, assign the study to a patient
      (`POST studies/{id}/assign`) → `MatchStatus=ManuallyAssigned`.
- [ ] **7.6 StudiesPage pagination** — seed >1 page of studies; paging,
      filters, and counts behave.
- [ ] **7.7 Viewer auto-refresh.** Open a study while `Received/Processing` →
      status flips to viewable without manual reload when extraction
      completes.

Report, delete, export:
- [ ] **7.8 Study-based report.** Open report editor from a study (no
      appointment) → save draft → finalize. DB: `DiagnosticReports` row has
      `ImagingStudyId` set, `AppointmentId NULL`.
- [ ] **7.9 One report per study.** Attempting a second report on the same
      study is rejected (unique filtered index).
- [ ] **7.10 Export.** `GET studies/{id}/export` downloads a ZIP containing
      the original DICOM.
- [ ] **7.11 Delete.** `DELETE studies/{id}` removes study + assets + blobs;
      usage (`SUM(StorageBytes)`) drops; report (if any) handling matches
      design.
- [ ] **7.12 Tenant isolation.** Hospital B cannot fetch/assign/delete
      hospital A's study ids (404/403, never data).

---

## Phase 8 — Module gating matrix

For each edition verify the API (not just hidden UI):

| Action | RIS | PACS | RIS+PACS |
|---|---|---|---|
| DICOM upload/register | 403 | ✓ | ✓ |
| Studies list/viewer | 403 | ✓ | ✓ |
| PDF/JPG attachment upload | ✓ | per design | ✓ |
| Appointments/worklist | ✓ | gated per design | ✓ |
| Finance/referrals | ✓ | 403 | ✓ |
| Reporting | ✓ | ✓ | ✓ |

- [ ] **8.1** Row-by-row with a user of each edition (curl or UI).
- [ ] **8.2** `[RequiresModule]` errors carry a clear code/message the UI maps.

---

## Phase 9 — PACS downgrade lifecycle

Run on a test center with `Pacs:GraceDays` lowered.

- [ ] **9.1 Grace = read-only.** After 3.5 (PacsRemovedAt set): new DICOM
      uploads blocked; existing studies still viewable; export still works.
- [ ] **9.2 Export-or-delete prompt** visible on the UI during grace.
- [ ] **9.3 Auto-delete.** With `Pacs:AutoDeleteEnabled=true` and grace
      elapsed, `SubscriptionLifecycleJob` deletes studies (≤100 per cycle) —
      verify blobs gone, rows gone/marked, job log lines present.
- [ ] **9.4 Kill-switch.** With `Pacs:AutoDeleteEnabled=false` nothing is
      deleted after grace.
- [ ] **9.5 Re-subscribe within grace.** Approving a PACS plan again clears
      `PacsRemovedAt` (new sub row) and uploads resume; no deletion occurs.

---

## Phase 10 — Posture A delivery (infra)

- [ ] **10.1 URLs are Front Door.** Manifest/viewer asset URLs use the
      CdnBaseUrl host, not `*.blob.core.windows.net`.
- [ ] **10.2 Storage firewall.** After locking the storage account to Front
      Door: direct blob URL → 403; same path via Front Door → 200.
- [ ] **10.3 Uploads still work** post-firewall (SAS upload paths must be
      allowed per the runbook).
- [ ] **10.4 Viewer performance** sanity: first-load and cached slice loads
      acceptable on a real study.

---

## Phase 11 — Offline / sync regression

- [ ] **11.1 Report autosave offline** → reconnect → syncs without loss
      (study-based and appointment-based reports).
- [ ] **11.2 Outbox flows** (staff add, chain deploy) queued offline replay on
      reconnect; a queued chain deploy that hits `SITE_LIMIT_REACHED` surfaces
      an intelligible error, not a silent drop.
- [ ] **11.3 Deltas.** `GET reports/delta` etc. handle `AppointmentId NULL`
      rows (no 500s, ImagingStudyId present in DTO).

---

## Phase 12 — Regression & sign-off

- [ ] **12.1 Unit suite**: `dotnet test` — 16 tier/PAYG/billing tests green
      (note the ~20 pre-existing unrelated failures baseline).
- [ ] **12.2 Core RIS smoke**: register patient → appointment → invoice →
      report → print. Referral commission written with ServiceDate.
- [ ] **12.3 Existing-customer invariant**: a pre-split center (legacy plan
      GUID subscription) still logs in with full RIS+PACS behaviour.
- [ ] **12.4 Trial expiry path** unchanged: expiry → grace → lock screens.

---

## Known issues / waivers (accepted, not test failures)

1. Cycle toggle resets tier selection to Starter (cosmetic).
2. Switching to PAYG submits a ₹0 payment request that still asks for a
   transaction reference (cosmetic).
3. Trials and PAYG/Chain have no user/site caps (by design — confirm with
   business).
4. Script 72 re-asserts catalog prices on every deploy — DB price edits are
   overwritten; edit the script instead.
5. Dev DB has duplicate `DisplayId`/token/referrer-name data, so script 61's
   unique indexes remain skipped until de-duplication.
