# 05 — Teacher: Payments & Subscription

Source: `lib/feature/teacher_module/payment/**` (~129 files) and `lib/feature/teacher_module/subscription/**`
(~13 files). One screen documented here actually lives outside both folders —
`lib/feature/teacher_module/student_profile/view/teacher_student_payment_history_view.dart` — because it is
reached from the student-profile "Activity" section, not from a Payment-tab route.
Endpoint constants: `lib/core/network_services/web_constant.dart`, lines ~287–401 (`webPathPayment*`,
`webPathCollect*`) and ~392–401 + ~492–500 (`webPathSubscription*`, `webPathAssistantWallet*V1`).

**Data flow:** `TeacherPaymentRemoteDataSource` (Dio) → `TeacherPaymentRepositoryImpl` (adds offline outbox +
snapshot-cache fallback behaviour) → cubits → views. Subscription has its own parallel, much simpler stack:
`TeacherSubscriptionRemoteDataSource` → `TeacherSubscriptionRepositoryImpl` → **one app-lifetime singleton
cubit** (`getIt<TeacherSubscriptionCubit>()`, registered in `service_locator.dart`) shared by the Home tab,
the side-menu badge, the student-links screen and the subscription screen itself. All responses are read
through the standard envelope `{success, message, data}` (`code` is **not** read by the generic envelope
parser — only two unrelated failure types inspect a raw `code`/`message` off the HTTP error body directly:
`DeviceLockFailure` and `TeacherLinkCapacityFailure`, the latter documented under Subscription below).

**Pagination is NOT the app's generic `PaginatedApiResponse` on the payment v1 endpoints.** Every
`api/v1/payments/*` list response nests its own paging fields **inside** `data`: `page`, `limit`,
`totalItems`, `totalPages` (client derives `hasMore = page*limit < totalItems` or `page < totalPages`
depending on the model). Legacy `api/Payment/*` endpoints (`departures`, `unpaid`) use the same shape but
sometimes call the count field `totalCount` instead of `totalItems` — check each table below. The
subscription requests list is the one outlier: `GET api/subscription/requests` returns an envelope whose
`data` is **itself** a paginated object (`data.data[]` + sibling `totalCount`/`page`/`pageSize`) — the app
reads only the inner array and ignores the paging fields entirely (no "load more" on that screen).

**Status wire values** — `status` on students-by-status / collect-students / session-roster rows is sent
and read as a lowercase string: `"paid"` | `"prorated"` | `"unpaid"` (`paymentStatusApiKey()` — note
**no capital R** in `prorated` here). Confusingly, the **per-month breakdown slices**
(`unpaidMonthsBreakdown[].isProRated`) use a different, capital-R boolean key `isProRated` — the two
families are not spelled consistently on the wire; the client tolerates both casings defensively but the
canonical casing differs by response family.

**Shared collector-fields reader** (`PaymentCollectorFields.fromJson`, read on periods/transactions/ledger
rows across many endpoints): `collectedByUserId` (int), `collectedByUserName` (string), `paymentMethod`
(string), `collectedAt` (UTC instant, localized for display), `localCollectedAt` (already the teacher's
local wall-clock — parsed as-is, **never** re-localized; re-localizing it would double-shift it).

**Date/month query formats actually sent** (see the Collections Ledger section for the full from/to
contract, which is the one place this gets complex):
- Most `api/v1/payments/*` list/tracking endpoints take `month` as a combined `"YYYY-MM"` string OR split
  `month`(int)+`year`(int) — check the per-endpoint table; both forms appear across the module.
- A **single-day filter** sends `from`/`to` as identical bare `"YYYY-MM-DD"` strings (inclusive day).
- A **wallet "in hand now" / departures / drawer scope** sends `from`/`to` as full UTC ISO instants with a
  `Z` suffix (e.g. `"2026-09-04T00:00:00.000Z"`) — the backend is documented to switch to exact
  `[from, to)` range semantics purely because the raw value **carries a time component**; a bare date
  string is always treated as an inclusive whole day. The app deliberately exploits this rather than
  sending an explicit "mode" flag.
- Departure-summary/confirm and edit/forgive/revert endpoints carry no date query params at all.

**Error-code handling — the one cross-cutting rule worth stating up front:** almost nothing in this module
branches on a machine-readable `code`/business-rule string on the **live/online** path — a 400/409/422 is
shown by simply displaying the envelope's localized `message` verbatim (`ServerFailure.fromResponse`).
The one path that DOES read a stable `errorCode` string is the **offline payment sync reconcile**
(`POST api/Payment/sync` → `PaymentSyncEntryResultApiDto.errorCode`), documented in its own section below.
Treat any other "business code" named in backend docs (`PaymentAmountExceedsAdvanceLimit`,
`CollectNoteRequired`, `EditNoteRequired`, `ForgiveAmountExceedsOutstanding`, `LinkedStudentCapacityReached`,
etc.) as **message-text-only** on this client unless a table below explicitly says otherwise — changing
that message string changes what the teacher sees; changing the `errorCode` string only breaks the sync
reconcile path.

---

## Payment Tracking Dashboard
_Dart file: `lib/feature/teacher_module/payment/view/teacher_payment_view.dart`_
_Cubit: `lib/feature/teacher_module/payment/manager/teacher_payment_cubit/teacher_payment_cubit.dart`_
**Reached from:** persistent bottom-nav tab ("Payment") — not a pushed route. Assistants see a different
tab body (`AssistantPaymentView`, documented separately below) instead of this screen.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Tab opens / reactivates / pull-to-refresh | `GET api/v1/payments/tracking` | query `month=YYYY-MM` | see shape below |
| ‹ / › month-nav arrows | same | `month` shifted ±1 calendar month | same |
| Tap a status stat card (Paid/Pro-rated/Unpaid) | *(navigation only)* | — | opens **Payment Students By Status** below |
| Tap "Collected with you" card | *(navigation only)* | — | opens **Collections Ledger** scoped to the teacher's own `collectedByUserId` |
| Tap an assistant's collector card | *(navigation only)* | — | opens **Assistant Wallet View** |
| Tap a "collected by sessions" row | *(navigation only)* | — | opens **Session Payment Detail** |
| "Departed students" link | *(navigation only, no refetch after)* | — | opens **Departed Students List** |
| "Pending offline" chip | *(none — reads the local offline outbox only)* | — | local `pendingCollectAmounts()` |

Response `data` shape (`PaymentTrackingApiDto`):
```json
{
  "month": "2026-09",
  "monthLabel": "September 2026",
  "summary": {
    "expectedRevenue": 15000, "totalStudents": 60, "totalStudentsCollected": 45,
    "totalSessions": 5, "remainingAmount": 3000, "progressPercent": 75,
    "collectedThisMonth": 12000, "expectedTotal": 16000, "collectedTotal": 13000,
    "collectedPreviousMonths": 1000, "collectedInAdvance": 500, "remainingTotal": 3000
  },
  "statusBreakdown": { "paid": 45, "prorated": 5, "unpaid": 10, "total": 60 },
  "collectedWithYou": {
    "totalCollected": 8000,
    "assistants": [ { "id": "42", "assistantName": "You", "role": "Teacher", "transactionCount": 12, "collectedAmount": 8000 } ]
  },
  "collectedByAssistant": {
    "totalCollected": 4000,
    "assistants": [ { "id": "56", "assistantId": "A-56", "assistantName": "Sara", "role": "Assistant",
      "transactionCount": 6, "collectedAmount": 4000, "walletBalance": 1200, "isRemoved": false } ]
  },
  "collectedBySessions": {
    "sessions": [ { "id": "228", "title": "Grade 10 - Physics", "collectedAmount": 6000,
      "studentsCollected": 20, "studentsTotal": 25, "groupId": null, "groupName": null } ]
  }
}
```
Notes:
- `summary.collectedTotal` **omitted entirely** (not sent as `0`) signals a legacy/no-"Total perspective"
  response — the client checks `containsKey`, not the value.
- `collectedWithYou` also read from the misspelled key `collecterWithYou` (historical-typo fallback).
- Per-assistant `id` (the collector's **user id**, used for `collectedByUserId` filtering elsewhere) and
  `assistantId` (a **separate** wallet-routing id) must both be sent — conflating them previously caused
  wallet-route 404s.
- `statusBreakdown.total` is optional; the client recomputes `paid+prorated+unpaid` when absent/≤0.
- No business-code branching on this call — any failure is a generic toast.
- **Offline:** a background hydrator snapshots the raw envelope for the **current month only**
  (`teacher_payment_offline_hydrator.dart`, key `paymentTracking(ownerKey, "YYYY-MM")`). A `NetworkFailure`
  serves that cached snapshot; navigating to a different month while offline still surfaces a network
  error (only "current month as of last sync" is cached).

---

## Payment Students By Status (Paid / Pro-rated / Unpaid tabs)
_Dart file: `lib/feature/teacher_module/payment/view/teacher_payment_students_by_status_view.dart`_
_Cubit: `lib/feature/teacher_module/payment/manager/teacher_payment_students_by_status_cubit/teacher_payment_students_by_status_cubit.dart`_
**Reached from:** Tracking Dashboard (tap a status stat card) and the Reports hub's "Unpaid" quick-report
tile.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen open / pull-to-refresh | `GET api/v1/payments/students` | `month=YYYY-MM`, `status=paid\|prorated\|unpaid`, `page=1`, `limit=10` | see shape below |
| Search box (debounced 350 ms) | same | + `search=<text>`, `page=1` | same |
| Infinite scroll | same | `page=n+1` | appended `students[]` |
| Filter bottom sheet → pick a sub-status chip → Apply | *(no request — not wired up)* | — | "Coming soon" toast; query never changes (see note) |
| Row ⋮ → "Set joining-month amount" | `PUT api/v1/payments/students/{teacherStudentId}/proration` | `{"amount": 275.00}` or `{"amount": null}` | — |
| Row ⋮ → "Forgive balance" (teacher only) | `POST api/v1/payments/forgive` | see **Forgive balance** section | `data.forgiveness.id` |
| Row tap → collect | *(opens the Collect flow, see below)* | — | — |

Response `data` shape (`PaymentStudentsByStatusApiDto`):
```json
{
  "month": "2026-09", "monthLabel": "September 2026", "status": "unpaid",
  "totalCollected": 0, "monthAmount": 3000, "amountPerMonth": 300,
  "totalUnpaidAmount": 3000, "expectedAmount": 3000, "total": 10, "page": 1, "limit": 10,
  "students": [ {
    "id": "981", "name": "Ahmed Ali", "avatarUrl": null, "status": "unpaid",
    "amountPerMonth": 300, "amountPaid": 0, "amountDue": 300, "unpaidAmount": 300, "unpaidMonths": 1,
    "studentCode": "A12", "sessionName": "Grade 10 - Physics", "sessionId": 228,
    "paidOn": null, "joinedAt": "2026-09-01", "overdueOn": "2026-09-05",
    "collectedByName": null, "collectedByRole": null,
    "isProrated": false, "proRatedFraction": null, "proratedAmount": null,
    "joinedAtIsFirstAttendance": false, "isProrationManual": false,
    "prorationClassesTotal": null, "prorationClassesBilled": null
  } ]
}
```
Notes:
- `expectedAmount` (full-scope Σ of each in-scope student's monthly rate) defaults to `0` on legacy
  responses; client falls back to `monthAmount / total`.
- `paidOn` is a UTC instant; `joinedAt`/`overdueOn` are calendar dates (never localized).
- **The "sub-status" filter chips (Unpaid/Partially unpaid, All paid/Partially paid) are non-functional
  today** — `toggleFilter()` is a documented no-op; there is no query param today that narrows "full vs.
  partial" within a status.
- No offline fallback on this endpoint (unlike the dashboard) — a `NetworkFailure` surfaces directly.

---

## Collect Students List
_Dart file: `lib/feature/teacher_module/payment/view/teacher_collect_payment_view.dart`_
_Cubit: `lib/feature/teacher_module/payment/manager/teacher_collect_payment_cubit/teacher_collect_payment_cubit.dart`_
**Reached from:** the "Collect Payment" action on the Tracking Dashboard, on `AssistantPaymentView`, on the
session-detail bottom bar (passes `sessionId`+`sessionTitle`, scoping the list to that session + linked
sessions), and a session's violations screen.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load / pull-to-refresh / infinite scroll | `GET api/v1/payments/collect/students` | `filter` (`all`\|`assigned`\|`unassigned`), `page`, `limit`, `search?`, `sessionId?` | `data.counts.{all,assigned,unassigned}`, `page`,`limit`,`totalItems`,`totalPages`, `students[]` |
| Filter chips (All/Assigned/Unassigned) | same, re-fetched | new `filter` | — |
| Row tap (not selecting) | *(opens the shared single-student collect flow)* | — | — |
| "Select" → multi-select → "Mark N students as Paid" | **no call here** — builds a local queue (full owed amount per student) and pushes **Scanned/Queued Collect Review**, which submits via `POST api/v1/collect/submit` | — | — |
| QR icon | *(navigation only)* | — | opens **QR / Barcode Scan Entry** |

Student row (`CollectStudentApiDto`): `id`/`teacherStudentId`, `name`/`studentName`, `studentCode`/`code`,
`avatarUrl`, `monthlyAmount`/`amount`, `assignment`, `status`, `unpaidMonths`, `isExempt`, `totalOwed`,
`sessionName`, `isProrated`, `proRatedFraction`, `proratedAmount`, `joinedAt`/`joinAt`/`joinDate`,
`joinedAtIsFirstAttendance`, `unpaidMonthsBreakdown[]` (`PaymentMonthSlice`: `month`,`monthLabel`,`amount`,
`periodId`,`isProRated`,`proRatedFraction`), `isProrationManual`, `prorationClassesTotal`,
`prorationClassesBilled`.

**Offline:** on a `NetworkFailure` the repo falls back to a locally cached snapshot of this same endpoint
(merged across pages, `filter=all` only).

---

## QR / Barcode / Manual-Code Scan Entry
_Dart file: `lib/feature/teacher_module/payment/view/teacher_payment_qr_scan_view.dart`_
**Reached from:** the QR icon on the Collect Students List header only.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Camera decodes a code, or manual student-code field + submit | `GET api/v1/collect/lookup` | `code?` (decoded/typed student code), `qr?`, `name?`, `month?` ("YYYY-MM") | see shape below |
| Manual-entry submit (after lookup) | *(no call yet)* | — | opens the amount-editor sheet before queueing |
| "View actions (N)" | *(navigation only)* | — | opens **Scanned/Queued Collect Review** with the in-memory queue |

`GET api/v1/collect/lookup` response (`CollectLookupApiDto`, shared by every collect entry point):
```json
{
  "student": {"id": "123", "name": "Sara Ali", "code": "A12", "group": "Grade 10 - Sun/Tue", "avatarUrl": null},
  "amountDue": 300, "paymentStatus": "unpaid", "monthlyAmount": 300, "monthsOwed": 2, "totalOwed": 550,
  "unpaidMonthsBreakdown": [
    {"month":"2026-08","monthLabel":"August 2026","amount":250,"periodId":"9001","isProRated":true,"proRatedFraction":0.83},
    {"month":"2026-09","monthLabel":"September 2026","amount":300,"periodId":"9002"}
  ],
  "advanceMonth": null,
  "isJoiningMonthProrated": true, "suggestedProratedAmount": 250,
  "classesAttendedThisMonth": 5, "classesTotalThisMonth": 12, "classesBilledThisMonth": 7,
  "firstClassDate": "2026-08-14", "joinDate": "2026-08-14",
  "proratedReason": null, "isProrationManual": false, "prorationSetByName": null, "prorationSetAt": null,
  "todayPaidByName": null, "todayPaidAmount": null, "todayPaidMonthLabel": null
}
```
- `404` → "student not found" (returns `null`, no error toast beyond the generic not-found message).
- `advanceMonth` (a `PaymentMonthSlice`, nullable) is a one-month-ahead offer for an already-`paid` student.
- `todayPaidByName`/`Amount`/`MonthLabel` (also read from a nested `todayPayment`/`sameDayPayment` object)
  drive the "already collected today — collect anyway?" soft-confirm dialog.
- Camera path enqueues silently at the full owed amount; **manual-entry path always shows the amount-edit
  sheet first**.
- If `paymentStatus == "paid"` and no `advanceMonth`, a "Fully paid — collect anyway?" dialog blocks enqueue.
- **Offline:** falls back to matching the scanned code against the cached collect-students snapshot (or
  cached attendance rosters), synthesizing an equivalent result — this suppresses the proration editor
  (setting proration is live-only).

---

## Scanned/Queued Collect Review ("View Result")
_Dart file: `lib/feature/teacher_module/payment/view/teacher_payment_collect_view_result_view.dart`_
_Cubit: `lib/feature/teacher_module/payment/manager/teacher_payment_collect_view_result_cubit/teacher_payment_collect_view_result_cubit.dart`_
**Reached from:** QR-scan's "View actions (N)" and the Collect Students List's bulk "Mark N students as
Paid" — both build a local `List<TeacherPaymentQueuedCollect>` and push this screen.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Edit-mode amount edit / month-counter stepper / swipe-to-dismiss | *(all purely local)* | — | — |
| "Collect payments" confirm | `POST api/v1/collect/submit` per item, or routed to the offline outbox | see below | see below |

Request body (`buildCollectSubmitRequestBody`):
```json
{
  "students": [
    {"studentId": 123, "amount": 250.00, "note": "paid cash, partial for August"},
    {"studentId": 456, "amount": 300.00}
  ],
  "month": "2026-08",
  "classSessionId": 42,
  "duplicateConfirmed": true
}
```
- `note` per-student **omitted entirely when empty**; required server-side (`CollectNoteRequired`) for a
  partial/custom (non-whole-month) amount.
- `month` sent only when the initiating screen was month-scoped.
- `classSessionId` sent only when the collect originated from a session context.
- `duplicateConfirmed: true` sent only on retry after the "already collected today" dialog is confirmed.

Response (`CollectSubmitApiDto`):
```json
{
  "submittedCount": 2, "totalCollected": 550.00,
  "results": [ {"studentId": "123", "status": "collected"}, {"studentId": "456", "status": "failed", "reason": "..."} ],
  "collectedByUserName": "Omar (assistant)"
}
```
- `results[].status == "failed"` items stay queued for retry; the first failure's `reason` is toasted.
- **HTTP handling:** 409 (same-day duplicate) and 400 (business rejection incl. `CollectNoteRequired`) →
  raw envelope `message`/`error` shown verbatim. **422** (`PaymentAmountExceedsAdvanceLimit` lives here)
  falls through the same generic path — **the UI never branches on a `code` string on this live path**,
  purely message-text-driven. (Contrast with the offline-sync path below, which does read `errorCode`.)

**Offline queueing:** items with a non-empty `note` try online first, falling back to the queue only on a
`NetworkFailure`; items without a note always queue. Each becomes an `OfflineOp`
(`TeacherPaymentOfflineOps.buildCollectOp`):
```json
{ "studentId": 123, "amount": 250.00, "offlineCollectedAt": "2026-09-12T08:15:00.000Z",
  "studentName": "Sara Ali", "studentCode": "A12", "note": "..." }
```
`opId` (the future `clientEntryId`) is client-generated (`OfflineIds.newOpId()`); `entityKey =
"pay:{studentId}:{localDayKey}"` — one live op per student per local calendar day. If online, the engine
attempts an immediate drain (20 s timeout) before returning. See **Offline Payment Sync** below for the
actual wire replay and error-code handling.

---

## Single-Student Collect Launcher (shared "one-tap" flow)
_Dart file: `lib/feature/teacher_module/payment/view/teacher_single_student_collect_launcher.dart`_
**Reached from (`launchSingleStudentCollect(context, studentCode, month)`):** Collect Students List row tap,
attendance QR scan / Take Attendance's "Collect payment" pop-up action, Payment Students By Status row,
Session Payment Detail's per-student ⋮ menu, and the center front-desk scan screen.

Not a screen — a function. Calls `GET api/v1/collect/lookup` (same shape as above), shows the same
warning/same-day dialogs, then opens the **Collect Amount Sheet** wired directly to a single-item
`POST api/v1/collect/submit` (or offline-queued the same way as the review screen). No separate endpoint.

- If `advanceMonth` is present and the student is `paid`, builds an "advance" queued item.
- After a **partial** collect (money still owed), shows a sheet naming the remaining balance and
  (teacher-only) offers a one-tap route into **Forgive Balance** (`POST api/v1/payments/forgive`).

---

## Collect Amount Sheet (shared editor)
_Dart file: `lib/feature/teacher_module/payment/view/widgets/teacher_payment_collect_queue_amount_sheet.dart`_

Purely a local editor (month counter / custom-amount toggle / note), with two side-effecting calls wired in
only when opened from the single-student launcher:

| Action | Endpoint | Sends |
|---|---|---|
| Move the "Joining month" amber stepper, then Save/Collect | `PUT api/v1/payments/students/{teacherStudentId}/proration` | `{"amount": 275.00}` (only if the teacher actually moved it) — sent **before** the collect submit call |
| Tap "Reset to automatic" | same | `{"amount": null}` |

`note` is required client-side (mirroring `CollectNoteRequired`) whenever the amount is a free-typed
custom/partial value. **Offline:** when the lookup was served from cache, the proration editor is disabled
(setting proration has no offline queue and would be silently lost).

## Standalone "Set Joining-Month Amount" Sheet
_Dart file: `lib/feature/teacher_module/payment/view/widgets/teacher_payment_set_proration_sheet.dart`_
**Reached from:** a student's ⋮ menu on Session Payment Detail — prices the joining month without
collecting anything.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Opens | `GET api/v1/collect/lookup` | `code`, `month?` | same `CollectLookupApiDto`; if `isJoiningMonthProrated == false` shows an info toast and never opens |
| Save | `PUT api/v1/payments/students/{teacherStudentId}/proration` | `{"amount": 275.00}` | success toast, pops `true` |
| "Reset to automatic" | same | `{"amount": null}` | same |

Explicitly gated by `ensureOnline(context)` — never queues offline.

## Custom Subscription-Amount Sheet
_Dart file: `lib/feature/teacher_module/payment/view/widgets/teacher_collect_payment_custom_amount_sheet.dart`_
**Reached from:** Session Payment Detail's student ⋮ menu ("Edit subscription amount").

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Save | `PUT api/Payment/students/{teacherStudentId}/custom-amount` (legacy `api/Payment` prefix, **not** `v1`) | `{"teacherId": 42, "teacherStudentId": 123, "customAmount": 350.00}` | `data.{repriced, keptManual, keptPaid}` |
| "Reset to default" | same | `customAmount: null` | same |

`repriced` = still-owed bills rewritten to the new price; `keptManual` = joining months left alone (hand-set,
sticky); `keptPaid` = bills left alone (already collected/forgiven) — a 2026-09-09 addition, defaults to 0
on older responses.

## Collect Success Screen
_Dart file: `lib/feature/teacher_module/payment/view/teacher_payment_success_view.dart`_
**Reached from:** after a successful queue/QR submit (never from the single-student launcher, which shows a
sheet/toast instead). No API calls — purely presentational; shows `collectedByUserName` passed from the
submit result. "Done" pops `true` so callers refresh.

## Mark-Paid Shortcut — orphaned wiring
`POST api/v1/payments/collect/mark-paid` (`{"studentIds":[...], "month":"2026-08"}` →
`{markedPaidCount, results[], collectedByUserName}`) plus a queued variant are still fully implemented in
the data/repository layers, but **no UI in the current app calls either** — the "Mark N students as Paid"
button was redesigned (2026-08-22) to route through `collect/submit` instead. Treat this endpoint as
orphaned from the mobile client. `teacher_collect_payment_student_actions_sheet.dart` (a 3-item ⋮ menu) is
similarly defined but never invoked.

---

## Session Payment Detail (per-session roster)
_Dart file: `lib/feature/teacher_module/payment/view/teacher_payment_session_detail_view.dart`_
_Cubit: `lib/feature/teacher_module/payment/manager/teacher_payment_session_detail_cubit/teacher_payment_session_detail_cubit.dart`_
**Reached from:** Tracking Dashboard's "collected by sessions" row tap (`goToTeacherPaymentSessionDetail`).
Swiping the same row's "Collect Payment" action opens the *general* Collect Students List instead (no
`sessionId`). Pops `bool` (`didMutate`) so the dashboard refreshes.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Header | `GET api/v1/payments/tracking` | `month=YYYY-MM` | `collectedBySessions.sessions[]` matched by `id==sessionId` for header labels |
| Roster load / search / infinite scroll | `GET api/v1/payments/students` | `month`,`sessionId`,`page`,`limit=10`,`search?` (status **omitted** → mixed statuses) | same student shape as Students-By-Status, plus `sessionId` per row |
| Bottom bar "Collect Payment" | *(navigation only)* | — | opens general collect screen, `sessionId` pre-set |
| Bottom bar "Student leaving" (picker) | `GET api/v1/payments/students` (picker list) then **Departure** endpoints | picker: `month`,`sessionId`,`search`,`page`,`limit` | picker rows |
| ⋮ → Collect Payment (single student) | `GET api/v1/collect/lookup` → `POST api/v1/collect/submit` | see Collect flow above | — |
| ⋮ → Set joining-month amount | `GET api/v1/collect/lookup` → `PUT .../proration` | `{"amount": ...}` / `null` | — |
| ⋮ → Edit subscription amount | `PUT api/Payment/students/{id}/custom-amount` | see above | `{repriced,keptManual,keptPaid}` |
| ⋮ → Edit payment | `GET .../history`, `PUT`/`DELETE api/Payment/transactions/{id}`, `GET .../edit-logs` | see **Edit/Delete Transaction** below | — |
| ⋮ → Forgive balance (teacher only) | `POST api/v1/payments/forgive` | see **Forgive Balance** below | `data.forgiveness.id` |
| ⋮ → Student leaving (entry point 2) | `GET .../departure-summary` → `POST .../departure/confirm` | see **Departure** below | — |

Notes:
- **The proration story line IS shown here** (`"Joined {date} · {billed} of {total} classes left ·
  {amount} of {full}"`), driven directly by the roster row's own fields — no separate call.
- `expectedAmount` (full-scope, at-today's-prices) is shown only when it differs from `monthAmount`,
  labelled "At current prices: {amount}".
- Header figures are kept frozen client-side while a search is active (the students endpoint's aggregates
  would otherwise shrink to the filtered subset).
- `teacherStudentId` (not JWT-derived) is sent as a route/body id on custom-amount, proration, forgive,
  edit-transaction and departure calls — the backend still resolves `teacherId` from the JWT and only uses
  this id to scope within that tenant (§3.3 rule).

---

## Collections Ledger (shared wire contract — three screens below reuse this exactly)
_Endpoints: `GET api/v1/payments/collections`, `GET api/v1/payments/collections/summary`,
`GET api/v1/payments/collections/yearly`_
_Owning cubit: `TeacherPaymentSessionCollectedCubit`_ — the query-building logic lives entirely here and is
shared, unmodified, by **Session Collections View**, **the assistant wallet's ledger tab**, and
**Assistant's Own Payment Tab**, below.

### The from/to convention (exact, as implemented)

| Mode | Trigger | `from`/`to` sent? | Literal example | Format |
|---|---|---|---|---|
| Whole-month view | Default / month-nav, no day picked | Neither | — (`month=9&year=2026`) | — |
| Single-day filter | Tap a date in the day strip | Both, identical | `from=2026-09-04&to=2026-09-04` | bare `YYYY-MM-DD`, local, no time |
| Wallet "in hand now" (drawer scope, no day) | Wallet's first success anchors on `heldSinceAt` | Both, full instants | `from=2026-09-04T14:32:10.001Z&to=2026-09-05T09:15:00.000Z` | `toUtc().toIso8601String()` |
| Drawer scope + a day also picked | Drawer window intersected with that UTC day | Both, still full instants | `from=2026-09-01T00:00:00.000Z&to=2026-09-02T00:00:00.000Z` | midnight UTC bounds, but still carries `T…Z` |
| "All collections" (exit drawer scope) | tap the chip | falls back to whole-month / bare-day rules above | — | — |
| Yearly view | `fetchCollectionsByYear` | none | — | only `year`,`page`,`limit` |

The backend is documented to detect "exact instant" mode purely from the raw query value **carrying a time
component** — a bare date is an inclusive whole day, a full `T…Z` string is an exact `[from, to)` bound.
Range wins over a plain day param when both could apply. Drawer-range construction:
`since.toUtc().add(1ms)` as `from` (excludes the hand-over event itself; `since == null` → starts at
`2020-01-01T00:00:00.000Z`), `now.toUtc().add(1 day)` as `to`.

### `GET api/v1/payments/collections`

| Sends | Uses from response |
|---|---|
| `month`(int), `year`(int), `page`, `limit` (15 in this cubit; 10 in the wallet's raw fetch), optional `from`/`to` (see table above), optional `collectedByUserId` (int — omitted = account-wide), optional `search` (student name/code), optional `includeAdjustments=false` (only ever sent as literal `false`, drops refund/withdrawal rows; absence = include everything) | `month`,`year`,`monthLabel`,`page`,`limit`,`totalItems`,`totalPages`,`items[]`,`amountTiers[]`,`dailyNets[]` |

Ledger item (`PaymentCollectionLedgerItemApiDto`):
```json
{
  "id": "5501", "index": 1, "studentId": "1234", "studentName": "Sara Ahmed", "studentCode": "A12",
  "amount": 160.0, "status": "success", "sessionName": "Grade 10 - Physics",
  "collectedAt": "2026-09-14T09:12:00Z", "isRefund": false, "isWithdrawal": false,
  "periodsCovered": 1, "refundedForMonthLabel": null,
  "appliedMonths": [ {"month":"2026-09","monthLabel":"September 2026","amount":160.0} ],
  "isEdited": false, "originalAmount": null, "performedByName": null,
  "dayKey": "2026-09-14", "note": null,
  "isProratedFirstMonth": true, "systemSuggestedProratedAmount": 160.0,
  "prorationSetByName": null, "prorationJoinedAt": "2026-09-14",
  "prorationClassesTotal": 13, "prorationClassesBilled": 7,
  "proratedFirstMonthAmount": 160.0, "prorationFullMonthAmount": 300.0, "isProrationManual": false
}
```
- `dayKey` is the **authoritative** raw-UTC day-bucket key that day separators group on (falls back to
  `collectedAt.toLocal()` only if absent on an older row).
- `amountTiers[]` = `{amount, count}` ("how many paid X"). `dailyNets[]` = `{dateKey, collected, deducted,
  net, collectionsCount}`, joining `items[].dayKey`.
- **No server-side "pending" status** on this ledger — every row is already settled/recorded. The app's
  "pending" chip is a purely local/offline concept from the device's outbox queue, unrelated to this wire
  contract.
- Proration story fields are rendered here too (ledger rows, even historical/paid ones, "by design —
  history"), name-free (the ledger is already scoped to one collector).

### `GET api/v1/payments/collections/summary`

Same `from`/`to`/`collectedByUserId`/`search`/`includeAdjustments` params as above (day-scope only; no
month-only call for this one). Response (`PaymentDayInsights`): `netCashCollected` (+`refundsTotal` summed
client-side into "collected"), `refundsTotal`→`refundedAmount`, `studentsPaidCount`→`studentsPaid`,
`transactionCount`→`collectionsCount`, **`departedRefundDueCount`→`refundsCount`** (naming gotcha — read
from the "departed refund due" field, not a field literally named refunds-count), `departedCount`. No
pagination.

### `GET api/v1/payments/collections/yearly`

`year`(int), `page`, `limit`. Response: `year`,`page`,`limit`,`totalItems`,`totalPages`, `items[]` (each:
`id`,`index`,`studentName`, `summary.{paidMonths,unpaidMonths,totalCollected,label}`,
`months[].{month,status,amount}`). **Dormant on the Flutter client** — the data/repository/domain layers
fully exist but no cubit or view currently calls `fetchCollectionsByYear`. All three endpoints here are
read-only, never offline-queued.

---

## Session Collections View
_Dart file: `lib/feature/teacher_module/payment/view/teacher_payment_session_collections_view.dart`_
**Important caveat:** despite the class name, **there is no per-session filter anywhere in this screen's
data flow** — `sessionId` is never sent. `sessionTitleFilter` is a **display-only** string (empty-state
copy) never sent to the API. This is a collector-scoped or teacher-wide money ledger, not a per-session
roster.

**Reached from — same widget, three different scopes:**

| Caller | Scope sent |
|---|---|
| Reports hub "Collected by session" report → `goToTeacherPaymentSessionCollections` | none — teacher-wide, every session/collector |
| Tracking Dashboard "collected with you" card → `goToTeacherOwnCollections` | `collectedByUserId = <teacher's own userId>` |
| Tracking Dashboard assistant card → `goToTeacherAssistantCollections` | `collectedByUserId = <assistant's userId>`, plus **wallet mode**: also mounts `TeacherAssistantWalletCubit` for a balance/withdraw header |

Uses the Collections Ledger wire contract above verbatim (`GET .../collections`, `.../collections/summary`
for the day-strip). In wallet mode it additionally calls `GET api/v1/assistants/{assistantId}/wallet` for
the header card and the withdraw actions documented under Assistant Wallet View below.

---

## Assistant Wallet View
_Dart file: `lib/feature/teacher_module/payment/view/teacher_assistant_wallet_view.dart`_
_Cubit: `lib/feature/teacher_module/payment/manager/teacher_assistant_wallet_cubit/teacher_assistant_wallet_cubit.dart`_
**Reached from:** Tracking Dashboard's collectors card → `goToTeacherAssistantWallet(assistantId,
assistantName, avatarUrl, collectorUserId)`.

**Two wallet-endpoint families exist; only one is live:**

| Constant | Path | Status |
|---|---|---|
| `webPathPaymentWallets` | `GET api/Payment/wallets` | live, but **only** for the tracking dashboard's offline-snapshot hydration — not called by any wallet/ledger screen |
| `webPathPaymentAssistantWallet(id)` | `GET api/Payment/wallets/{id}` | **dead** — zero call sites |
| `webPathPaymentAssistantWalletReset(id)` | `POST api/Payment/wallets/{id}/reset` | **dead** — zero call sites |
| `webPathAssistantWalletV1(id)` | `GET api/v1/assistants/{id}/wallet` | **live** — backs this screen and the assistant's own cash-bag card |
| `webPathAssistantWalletWithdrawV1(id)` | `POST api/v1/assistants/{id}/wallet/withdraw` | **live** — the only "reset" action that actually fires |
| `webPathAssistantWalletWithdrawalsV1(id)` | `GET api/v1/assistants/{id}/wallet/withdrawals` | **live** — hand-over history |

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load / pull-to-refresh | `GET api/v1/assistants/{assistantId}/wallet` | `page`, `limit=10`, `search?` | `assistant.{id,assistantName\|userName\|fullName\|name,avatarUrl,role,transactionCount,walletBalance\|currentBalance}`; `wallet.{totalCashCollected,walletBalance,collectionsCount,lastActivityAt}`; `collections.{total,page,limit,items[],sinceAt,heldSinceAt}` |
| Search field | same, page reset to 1 | `search` | same |
| "Withdraw" button → sheet → submit | `POST api/v1/assistants/{assistantId}/wallet/withdraw` | `{"amount": 350.00}` (omitted entirely, i.e. body `{}`, only if the field is somehow cleared — the sheet pre-fills the full balance; there is no explicit "withdraw all" flag) | `withdrawalId`(`\|resetId\|id`), `status`(default `"completed"`), `amount`(`\|resetAmount\|totalAmountReset`), `walletBalanceAfter`(`\|balanceAfter\|balance`), `requestedAt`(`\|resetAt\|createdAt`) |
| "Withdrawal history" | *(navigation only)* | — | opens **Wallet Withdrawals View** |
| "View all" (collections) | *(navigation only)* | — | opens **Assistant Wallet Collections View**, or the shared unified ledger (via `collectedByUserId`) when `collectorUserId` is already known |

**404 handling:** a 404 whose body's `code`/`message` matches "wallet not found"/"assistant not found"
(case-insensitive) is treated as an EMPTY wallet, not an error. Withdraw errors (409/400) surface the raw
`message` verbatim — no business-code branching. **Wire-naming quirk:** even on the live v1 endpoints the
JSON keys still say `resetAt`/`amountReset`/`resetId` — "withdraw" and "reset" are the same operation under
different vocabulary at different layers (also confirmed by the success toast: *"Assistant wallet reset
successfully."*). **No offline queueing** anywhere in this wallet/withdraw family — `ensureOnline(context)`
gates the withdraw submit; no `Idempotency-Key` header either (unlike collect/mark-paid), so a double-tap
withdraw is not guarded client-side.

## Assistant Wallet Collections View (legacy paginated path)
_Dart file: `lib/feature/teacher_module/payment/view/teacher_assistant_wallet_collections_view.dart`_
**Reached from:** Assistant Wallet View's "View all", only when `collectorUserId` is unknown. Same cubit,
same `GET api/v1/assistants/{id}/wallet` endpoint — just infinite-scroll paginated instead of bounded. No
new endpoints.

## Wallet Withdrawals View
_Dart file: `lib/feature/teacher_module/payment/view/teacher_wallet_withdrawals_view.dart`_
**Reached from:** Assistant Wallet View's "Withdrawal history" button.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load / pull-to-refresh | `GET api/v1/assistants/{assistantId}/wallet/withdrawals` | none | bare array; each: `id`\|`Id`, `amountReset`\|`AmountReset`, `resetAt`\|`ResetAt` (UTC instant) — PascalCase fallbacks tolerated |

Read-only, no write actions.

## Assistant's Own Payment Tab
_Dart file: `lib/feature/teacher_module/payment/view/assistant_payment_view.dart`_
**Reached from:** replaces the "Payment" bottom-nav tab entirely whenever the logged-in account is an
assistant (`TeacherNavBarView`). Not a pushed route.

Two cubits, both forced to the caller's **own** identity (`selfUserId`, from local storage):
1. `TeacherAssistantWalletCubit(assistantId: '$selfUserId')` — drives the "My cash bag" card via
   `GET api/v1/assistants/{selfUserId}/wallet`. Code comment: *"For an assistant caller the backend resolves
   the wallet by their own user id and ignores this route id — so passing the account id is safe."*
2. `TeacherPaymentSessionCollectedCubit(collectedByUserId: selfUserId)` — the confirmed own-scope forcing
   param, sent on both `GET api/v1/payments/collections` and `.../collections/summary`, `if (collectedByUserId
   != null)`.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| "My cash bag" card | `GET api/v1/assistants/{selfUserId}/wallet` | `page=1`, `limit=10` | `wallet.walletBalance` → "Holding now"; `totalCashCollected`+`collectionsCount` → "Collected {amount} · {count} payments"; `collections.heldSinceAt` → drawer-scope anchor |
| "Collect a payment" | *(navigation only)* | — | opens Collect Students List |
| "Student leaving" | *(navigation only)* | — | opens Departure picker → sheet |
| Scope chip "Collections in wallet" | `GET .../collections` + `.../collections/summary` | `collectedByUserId=<selfUserId>`, `from`/`to` = exact drawer-range instants | ledger rows |
| Scope chip "All collections" | same two | `collectedByUserId=<selfUserId>`, `month`,`year` | same |
| Month nav / day filter / search / "collections only" | same two | adds `search`, `includeAdjustments=false`, single-day `from=to` | same, plus day-insights when a day is picked |

No withdraw action anywhere on this screen (teacher-only). `PaymentWalletLedgerCoordinator` defers the
ledger's first `load()` until the wallet cubit's first success, then opens the ledger in drawer scope
anchored on `heldSinceAt` — pure orchestration, no wire call of its own.

---

## Student Payment View / History
_Dart file: `lib/feature/teacher_module/student_profile/view/teacher_student_payment_history_view.dart`_
_Cubit: `lib/feature/teacher_module/student_profile/manager/teacher_student_payment_history_cubit/teacher_student_payment_history_cubit.dart`_
**Reached from:** `TeacherStudentProfileView` → "Activity" section → "Payment history" row — **outside**
the payment module's own folder.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load (summary + progress bars) | `GET api/Payment/students/{teacherStudentId}/payment-view` | — (path id only) | `sessionName`, `amountPaid`, `outstanding`, computed `overdueCount` |
| Screen load (paged list) / infinite scroll | `GET api/Payment/students/{teacherStudentId}/history` | `page`, `pageSize` (default 10) | `sessionName`, `totalAmountPaid`\|`amountPaid`, `periods[]` (`id`,`studentName`\|`sessionName`,`periodType`,`periodStart`,`amountDue`,`amountPaid`,`paymentStatus`,`paymentMethod`+collector fields,`collectedAt`,`localCollectedAt`,`transactions[]`), `forgivenesses[]` (`type`,`amount`,`note`,`byName`,`date` — page 1 only), `page`,`totalPages` |
| Pull-to-refresh | same two GETs, `force: true` | — | re-emits both |

Read-only screen — no edit/delete/forgive actions live here (those are on Session Payment Detail / Students
By Status). `paymentStatus`/`periodType` also accept legacy numeric codes (`0/1/2` →
`Unpaid/Paid/ProRated`, `0/1` → `Monthly/PerSession`) as a defensive fallback, not a documented contract.

---

## Edit or Delete Payment Transaction + Edit Logs
_Dart file: `lib/feature/teacher_module/payment/view/widgets/teacher_payment_edit_payments_sheet.dart`_
_Cubit: `lib/feature/teacher_module/payment/manager/teacher_payment_edit_payments_cubit/teacher_payment_edit_payments_cubit.dart`_
**Reached from:** Session Payment Detail's ⋮ menu → "Edit payment" — lists the student's transactions
(re-derived from `GET .../payment-view`, flattening every period's `transactions[]`, newest first).

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Sheet load / reload after mutation | `GET api/Payment/students/{teacherStudentId}/payment-view` | — | each transaction: `id`,`amountPaid`,`paymentTransactionStatus`\|`paymentStatus`\|`status`,`collectedByUserName`,`collectedAt`,`localCollectedAt`,`periodLabel`,`note`,`isEdited` (or `editCount`>0); owning period's `amountDue` as `fullMonthAmount` |
| Row ⋮ → "Change amount" | `PUT api/Payment/transactions/{transactionId}` | `{"newAmount": 350.00, "note": "..."}` (note omitted if blank; **the wire key is `note`, not `editReason`**) | 200 envelope only; triggers reload |
| Row ⋮ → "Delete payment" | `DELETE api/Payment/transactions/{transactionId}` | — | 200 envelope only |
| Row ⋮ → "Refund" | `POST api/Payment/transactions/batch-revert` | `{"transactionIds": [4821]}` — single-element array, **no `reason` sent from this action** | `data.revertedCount`,`data.failedCount`; if `revertedCount==0 && failedCount>0`, `results[0].message` is thrown |
| "View edit note" (when `isEdited`) | `GET api/Payment/transactions/{transactionId}/edit-logs` | — | list of `{editReason\|note\|reason, previousAmount\|oldAmount\|amountBefore, newAmount\|amountAfter, editedByName\|editedBy\|byName, editedAt\|createdAt\|date}` — fetched per transaction on demand |

Edit-log entry example (canonical keys per the code's own doc comment):
```json
{ "editReason": "Parent paid half now, rest next week", "previousAmount": 500.00,
  "newAmount": 350.00, "editedByName": "Mona Kamal", "editedAt": "2026-09-10T14:32:00Z" }
```
Error handling: `PUT` special-cases HTTP 400 → throws the envelope message verbatim (used for
`EditNoteRequired`, message-text-only, not a code-branch). Client rule (UX only, not server-enforced client
side): a note is required in the sheet whenever the typed amount differs from `fullMonthAmount` by ≥0.01,
or `fullMonthAmount` is unknown. Nothing here is offline-queued — all four calls require
`ensureOnline(context)`.

---

## Batch-Revert (Refund)
_Endpoint: `POST api/Payment/transactions/batch-revert`_

The app exposes only a **single-transaction** repository method
(`refundPaymentTransaction({transactionId, reason})`) that calls this endpoint with a one-element
`transactionIds` array — the "Refund" row action above is currently its only caller anywhere in the app; no
call site sends more than one id, so the "batch" shape exists on the wire but the client never truly batches.

```json
// Request
{ "transactionIds": [4821, 4822], "reason": "Duplicate collection entered twice" }
```
```json
// Response (200, partial-success shape)
{ "success": true, "data": { "revertedCount": 1, "failedCount": 1,
  "results": [ { "message": "Transaction already reverted" } ] } }
```
HTTP 200 is treated as success regardless of content; the client inspects `revertedCount`/`failedCount` and
throws using `results[0].message` when nothing reverted. `reason` is audited server-side (per the actor +
reason expectation) but is only ever sent when non-empty — and the app's one live call site never populates
it.

---

## Forgive Balance + Reverse
_Dart file: `lib/feature/teacher_module/payment/view/widgets/teacher_payment_forgive_balance_sheet.dart`_
_Cubit: `lib/feature/teacher_module/payment/manager/teacher_payment_forgive_cubit/teacher_payment_forgive_cubit.dart`_
**Reached from (3 entry points, teacher-only — hidden for assistants):** Session Payment Detail ⋮ →
"Forgive balance"; Payment Students By Status ⋮ row menu (non-paid tabs); the single-student launcher's
post-partial-collect remainder sheet ("Forgive the remainder").

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| "Forgive" submit | `POST api/v1/payments/forgive` | `{"teacherStudentId": 4821, "amount": 350.00, "note": "Family hardship, teacher approved waiver"}` (`note` omitted if blank) | `data.forgiveness.id` (captured for a possible later reverse); `data.student.*` (not parsed into a model) |
| Reverse a forgiveness | `POST api/v1/payments/forgive/{forgivenessId}/reverse` | `{"note": "..."}` (omitted if blank) | 200 envelope only |

Error handling: 400/404/409/422 on either call surface the envelope message verbatim (doc comments name
`ForgiveAmountInvalid`/`ForgiveAmountExceedsOutstanding` (422), `StudentNotFound` (404),
`ForgivenessNotFound` (404), `ForgivenessAlreadyReversed` (409) — all message-text-only, no code branch).

**Important gap: `reverseForgiveness` is fully implemented at the data/repository layer but is called from
ZERO cubits/views anywhere in the app.** There is no "undo forgiveness" UI hook today — the forgiveness id
returned by a forgive call is captured but never handed to a reverse action. Neither call is offline-queued;
both require connectivity.

---

## Departure Summary + Confirm
_Dart file: `lib/feature/teacher_module/payment/view/widgets/teacher_payment_departure_sheet.dart`_
_Cubit: `lib/feature/teacher_module/payment/manager/teacher_payment_departure_cubit/teacher_payment_departure_cubit.dart`_
**Reached from:** a "Student leaving" action — `assistant_payment_view.dart` and
`teacher_payment_session_detail_view.dart`. Auto-loads the summary on open; pops `true` on confirm success.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Sheet opens (auto) | `GET api/Payment/students/{teacherStudentId}/departure-summary` | path id only | `studentName`,`studentCode`,`sessionName`,`currentPeriodLabel`,`monthLabel`,`periodStart`,`totalOccurrencesInPeriod`,`attendedOccurrences`,`fullPeriodAmount`,`proRatedAmount`,`paidAmount`,`finalAmount`,`paymentStatusAtDeparture`,`departureOutcome`,`outcomeLabel`,`paymentPeriodId` |
| Editable amount field (only when `hasAmountToSettle`) | *(local)* | pre-filled from `finalAmount`, clamped [0, max] | — |
| "Confirm departure" | `POST api/Payment/departure/confirm` | `{"teacherStudentId":741,"sessionId":228,"deleteStudent":false,"overrideAmount":0}` (`overrideAmount` omitted if unchanged from `finalAmount`) | `id`,`sessionName`,`studentName`,`paymentStatusAtDeparture`,`totalOccurrencesInPeriod`,`attendedOccurrences`,`fullPeriodAmount`,`proRatedAmount`,`finalAmount`,`isTutorOverride`,`departureOutcome`,`departedAt` |

`teacherId`/`confirmedByUserId` are never sent — JWT-resolved server-side. `departureOutcome` wire values:
`RefundDue` \| `AmountOwed` \| `NoObligation`. **The departure-summary response does NOT carry
`isWaivedRefund` or `isTutorOverride`** — those only exist on the departed-students list / confirm response
respectively; the pre-confirm sheet's action-color logic is driven purely by `departureOutcome` +
`hasAmountToSettle`. Errors (422/409/400 — a rejected override) surface the localized message verbatim. Not
offline-queued — requires connectivity.

## Departed Students List
_Dart file: `lib/feature/teacher_module/payment/view/teacher_departed_students_view.dart`_
**Reached from:** Tracking Dashboard's "Departed students" link.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load / refresh / scroll-to-load-more | `GET api/Payment/departures` | `page`,`limit=20`,`search?` (name/code), `from`/`to` (UTC ISO instants, present only when a day/month scope is active) | `data.departures[]`,`data.page`,`data.totalItems`,`data.totalPages`,`data.dailyTotals[]` |
| Search bar | same, page 1 | `search` | same |
| Scope chips "All time"/"By month" + stepper | same | `from`=1st-of-month UTC, `to`=1st-of-next-month UTC | same |
| Single-day picker | same | `from`=day UTC midnight, `to`=+1 day UTC | same |

Per-row keys (`TeacherDepartedStudent`): `id`,`studentName`,`studentCode`,`sessionName`,`departedAt` (UTC),
`finalAmount`,`isRefund`,`departureOutcome`,`attendedOccurrences`,`totalOccurrencesInPeriod`,
`fullPeriodAmount`,`proRatedAmount`,`paymentStatusAtDeparture`,`originalCalculatedAmount`,`isTutorOverride`,
**`isWaivedRefund`**, `anchorPeriodStart`, `paidAmountAtDeparture`, `dayKey`. Per-day totals
(`dailyTotals[]`): `dateKey`,`departedCount`,`refundedTotal`,`owedTotal`.

**"Waived"/outcome badge precedence** (exact): (1) `isWaivedRefund==true` → "Waived"; (2)
`isAmountOwed && isTutorOverride && finalAmount<=0` → "Written off"; (3) `RefundDue` → "Refunded"; (4)
`AmountOwed` → "Owed"; (5) else → "Settled". `dayKey`/`from`/`to` are UTC-anchored deliberately —
`departedAt` is UTC and the backend buckets by its raw UTC value; a local-midnight window would straddle
two buckets (same bug class the wallet ledger's day filter had). Read-only, online-only.

## Unpaid Students List
_Dart file: `lib/feature/teacher_module/sessions/view/teacher_violation_students_list_view.dart`_ (the
`kind == unpaid` branch)
**Reached from:** a session's "View violations" screen → "Unpaid students" → View all.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load / month chip / scroll | `GET api/Payment/unpaid` | `SessionId`(int), `PaymentType`="Monthly" (fixed), `MinConsecutiveUnpaid`=1 (fixed), `AsOfMonth`="YYYY-MM", `Page`, `PageSize`=10 | generic paginated envelope: `totalCount`,`page`,`pageSize`,`totalPages`,`items[]` |

Per-item (`PaymentUnpaidStudentApiDto`, defensive multi-key fallbacks): `teacherStudentId`, `studentName`,
`studentCode`, `sessionName`, `sessionId`, `profileImageUrl`\|`avatarUrl`,
`outstandingAmount`\|`totalOutstanding`\|`amount`, `consecutiveUnpaidPeriods`\|`consecutiveUnpaid`\|
`overduePeriods`, `totalUnpaidPeriods`, `isPartialUnpaid`\|`isPartialPayment`. **This screen is
session-scoped only** — there is no teacher-wide unpaid list on this endpoint. Read-only, online-only, no
offline snapshot (unlike Collect Students List).

---

## Offline Payment Sync (`POST api/Payment/sync`)
_Dart files: `lib/feature/teacher_module/payment/data/teacher_payment_offline_ops.dart`,
`teacher_payment_op_transport.dart`, `teacher_payment_remote_data_source.dart`, `model/payment_v1_api_dto.dart`_

Batch replay of the local offline outbox (SQLite table `outbox`, one row per queued write, states
`pending`/`inFlight`/`needsConfirmation`/terminal — via `OutboxStore`/`OfflineDatabase`, shared with other
modules' offline queues). Payment uses **two composed transport strategies**:
- `BatchPaymentSyncTransport` (primary) — batches every fresh, never-yet-attempted collect op into ONE
  `POST api/Payment/sync` call (exactly-once via `clientEntryId`); any op that is a retry, ambiguous, or not
  a plain collect falls through to:
- `PerOpIdempotentPaymentTransport` (fallback/composition) — per-op v1 endpoint calls with an
  `Idempotency-Key` header, safe against a legacy backend that lacks the sync endpoint (a 404 from the sync
  call is treated as transient and the op reroutes here on the next drain).

**Trigger:** connectivity restored (`ConnectivityService.onReconnected` listener), app foreground resume
(`didChangeAppLifecycleState`), immediately after a repository enqueues a write while already online, and
an exponential backoff retry ladder on failure (30 s → 1 m → 5 m → 15 m → 60 m). Surfaced app-wide via the
generic "Unsent records" banner/screen (`lib/core/offline/view/widgets/offline_unsent_records_banner.dart`,
shown on the Home tab) — shared infrastructure across modules, never called a "sync center" in copy.

**Request body** (`{"offlineRecords": [...]}`, one object per queued collect):
```json
{
  "offlineRecords": [
    {
      "teacherStudentId": 123, "amount": 250.00,
      "isOfflineRecord": true, "offlineDeviceId": "a1b2c3-device",
      "offlineCollectedAt": "2026-09-12T08:15:00.000Z",
      "clientEntryId": "op_9f8e7d6c",
      "collectionNote": "paid cash, partial for August"
    },
    { "teacherStudentId": 456, "amount": 300.00,
      "isOfflineRecord": true, "offlineDeviceId": "a1b2c3-device",
      "offlineCollectedAt": "2026-09-12T08:16:00.000Z",
      "clientEntryId": "op_1a2b3c4d" }
  ]
}
```
- `collectionNote` is the CURRENT, fixed key — an in-code comment documents a past bug where this key was
  sent as `note` (which "binds to nothing... is why note-bearing collects were forced online"); it is
  `collectionNote` today. **Omitted entirely** when the collect carried no note.
- `clientEntryId` is the local `OfflineOp.opId` (`OfflineIds.newOpId()`), the exactly-once dedup key.

**Response** (`PaymentSyncResultApiDto`):
```json
{
  "success": true,
  "data": {
    "syncedCount": 1, "conflictCount": 1, "failedCount": 0,
    "entryResults": [
      { "clientEntryId": "op_9f8e7d6c", "success": true, "isConflict": false, "alreadySynced": false,
        "appliedAmount": 250.00, "amountDueAtSync": 250.00, "settledMonths": ["2026-08"] },
      { "clientEntryId": "op_1a2b3c4d", "success": false, "isConflict": false, "alreadySynced": false,
        "errorCode": "PaymentAmountExceedsAdvanceLimit",
        "errorMessage": "This exceeds the advance payment limit." }
    ]
  }
}
```
Per-entry fields (`PaymentSyncEntryResultApiDto`): `clientEntryId`, `success`, `isConflict`, `alreadySynced`,
`errorMessage`, **`errorCode`** (stable, null on success/older backends), `existingRecord` (nullable map),
`appliedAmount`, `amountDueAtSync`, `settledMonths[]` (all null/empty on older backends that don't echo
them).

**`errorCode` values the client switches on** (`_rejectionReason`, the one place this app reads a business
code rather than displaying raw text):
```
'StudentNotAssignedToSession' → "This student isn't in a class yet. Add them to a class, then retry."
'PaymentAmountExceedsAdvanceLimit' → "This is more than what's due now. Collect a smaller amount, then retry."
else → entry.errorMessage, or a generic "The server has a different record"
```
**`isConflict == true` is NOT treated as an unresolved conflict** — it means the student was already fully
paid elsewhere (someone else's payment covered them), so the op is resolved as `OpSynced` (with
`alreadyPaid: true`) rather than parked for the teacher to dismiss; the local snapshot flips the student to
"paid". A legacy backend with no `entryResults` (or a missing per-entry echo) is treated as "delivery
unknown" — `OpRetryLater(ambiguous: true)`, reconciled via the per-op fallback path next drain, never
silently assumed successful.

Local outbox item shape (`TeacherPaymentOfflineOps.buildCollectOp`): `entityKey =
"pay:{studentId}:{localDayKey}"` — one live op per student per local calendar day (a second same-day
collect attempt replaces the pending op via the duplicate-confirm dialog, mirroring the server's own
same-day rule).

---

## Subscription — Current Plan / Status Card / Requests
_Dart file: `lib/feature/teacher_module/subscription/view/teacher_subscription_view.dart`_
_Cubit: `lib/feature/teacher_module/subscription/manager/teacher_subscription_cubit.dart`, registered as an
app-lifetime **singleton** (`getIt<TeacherSubscriptionCubit>()`)_
**Reached from:** side-menu "Subscription" item (hidden for center-owned teachers), Settings → "increase
students number" footer, the Status Banner's CTA, the Feature-Locked Card's "View plans" button, and a
notification tap. The `isPaidPlan` param on the route is kept for wire-compat only and ignored by the page.

On open, `loadPage()` fires three independent GETs; only a `status` failure blocks the page.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen open / pull-to-refresh | `GET api/subscription/status` | — | `hasSubscription`,`planType`,`status`,`daysRemaining`,`endDate`(calendar date),`renewalAmountEGP`,`attentionLevel`,`ctaType`,`message`,`hasPendingRequest`,`whatsAppNumber`,`features.{studentAccountsAllowed,parentFollowUpAllowed,linkedStudentCapacity,linkedStudentsUsed}` |
| Screen open / pull-to-refresh | `GET api/subscription/pricing` | — | `perStudentMonthlyEGP`,`managerialMonthlyEGP`,`managerialPlusMonthlyEGP` |
| Screen open / pull-to-refresh | `GET api/subscription/requests` | `page=1`,`pageSize=20` (hardcoded, no pagination UI) | `data.data[]` — note the **double-nested** `data`; sibling `totalCount`/`page`/`pageSize` on the inner object are parsed but ignored (no "load more") |
| Plan card select (Full / Managerial+Parents / Managerial) | *(local — recomputes fee preview)* | — | — |
| "Send subscription request" | `POST api/subscription/requests` | see below | full `SubscriptionRequestInfo` → triggers `loadPage()` reload |
| "Cancel request" (pending card) | `DELETE api/subscription/requests/{requestId}` | path id only | same shape → triggers reload |
| "Contact the team on WhatsApp" | *(none)* | — | uses `whatsAppNumber` cached from the last status response |

`GET api/subscription/status` response:
```json
{ "success": true, "message": "", "data": {
  "hasSubscription": true, "planType": "Full", "status": "ExpiringSoon", "daysRemaining": 4,
  "endDate": "2026-09-16", "renewalAmountEGP": 1500.0, "attentionLevel": "warning", "ctaType": "renew",
  "message": "Your subscription expires in 4 days. Renew to avoid interruption.",
  "hasPendingRequest": false, "whatsAppNumber": "+201234567890",
  "features": { "studentAccountsAllowed": true, "parentFollowUpAllowed": true,
    "linkedStudentCapacity": 150, "linkedStudentsUsed": 132 } } }
```
`GET api/subscription/pricing`:
```json
{ "data": { "perStudentMonthlyEGP": 10.0, "managerialMonthlyEGP": 400.0, "managerialPlusMonthlyEGP": 650.0 } }
```
`planType` wire literals (exact case, used for both send and receive): `"Full"` \| `"Managerial"` \|
`"ManagerialPlus"` (displayed as "Managerial + Parents").

`POST api/subscription/requests` body (Full plan):
```json
{ "planType": "Full", "requestedStudents": 200, "requestedLinkedStudents": 150 }
```
Body (Managerial/ManagerialPlus — no student-count fields shown for these plans):
```json
{ "planType": "ManagerialPlus", "requestedStudents": 0 }
```
`requestedLinkedStudents` and `note` are **omitted entirely** (not `null`) whenever unset — the data source
uses a conditional spread. **`note` is never sent from this screen** — there is no note input field in the
current UI, even though the model/repo/data-source fully support it (a center-side subscription screen does
use its own note field, out of this chapter's scope).

`SubscriptionRequestInfo` (create/read/cancel response, one row of the requests list):
```json
{ "id": 88, "planType": "Full", "requestedStudents": 200, "requestedLinkedStudents": 150,
  "computedAmountEGP": 2250.0, "status": "Pending", "note": null, "rejectionReason": null,
  "requestedAt": "2026-09-12T09:14:00Z", "resolvedAt": null }
```
`status` is compared only against the literal `"Pending"` (`isPending`) to decide whether to show the
pending card / hide the form — `Approved`/`Rejected`/`Cancelled` are not otherwise specially rendered.
Fee preview (`_fee()`) is a client-side estimate only — Full = `requestedLinkedStudents ×
perStudentMonthlyEGP` (priced on **app accounts**, never `requestedStudents`); Managerial/ManagerialPlus =
their flat price. The server recomputes the authoritative `computedAmountEGP`.

**Dead/unused in this section:** `GET api/Subscription/current` (`webPathSubscriptionCurrent`) is fully
implemented end-to-end but **never called** — the live screen gets everything from `/status` instead.
`GET api/subscription/requests/{id}` is **never called** — `webPathSubscriptionRequestById` is used only
for the `DELETE` (cancel). Three widget files (`teacher_subscription_paid_plan_widget.dart`,
`teacher_subscription_status_row_widget.dart`, and the Blocked Dialog below) are orphaned/superseded UI —
not referenced anywhere outside their own file.

## Status Banner
_Dart file: `lib/feature/teacher_module/subscription/view/widgets/subscription_attention_banner.dart`_
**Reached from:** rendered inline on the teacher Home tab, fed by the shared singleton cubit — makes no
fetch of its own; `teacher_home_tab_view.dart` triggers `loadStatus()` (`GET api/subscription/status` only)
every time the Home tab reactivates.

Purely presentational, driven entirely by the cached `/status` fields: hidden when `attentionLevel ==
'none'`; red when `'critical'`, else amber; body = `message` verbatim; CTA shown only when `ctaType !=
'none' && !hasPendingRequest`, labelled "Renew subscription" for `ctaType=='renew'` else "Subscribe now" for
anything else non-`'none'`. Tapping it opens the Current Plan screen (no network call itself).

## Blocked Dialog — dead code
_Dart file: `lib/feature/teacher_module/subscription/view/widgets/teacher_subscription_blocked_dialog.dart`_
**Reached from:** nowhere — `TeacherSubscriptionBlockedDialog.show()` has no call site in the current app.
No network calls of its own; static locale copy only. The live "you're blocked" experience is the Status
Banner + Status Card instead.

## Feature-Locked Card
_Dart file: `lib/feature/teacher_module/subscription/view/widgets/feature_locked_card.dart`_
**Reached from three distinct triggers**, none of which make a network call themselves:

| Preset | Triggered by | Gate |
|---|---|---|
| Parent follow-up locked | Side-menu "Parent requests" item / parent-portal screens | `parentState.summary.portalAllowed` from the **parent-portal-summary** endpoint — **not** `features.parentFollowUpAllowed` from `/status`, which is parsed but never actually consulted by any gate in the app today |
| Student accounts locked | Inline card above the pending link-requests list | `subscriptionStatusCubit.state.statusInfo?.features.studentAccountsAllowed` (cached, fail-open `?? true`) |
| Linked-student-capacity reached | An accept/bind call in the **student-links module** (different endpoint family) returns HTTP 403 whose body `code`/`message` equals `LinkedStudentCapacityReached` or `LinkedStudentCapacityReachedCenterManaged` (case-insensitive) | `TeacherLinkCapacityFailure.tryFrom`, reading the raw error body directly, bypassing the envelope |

For the capacity-reached case: if the server's raw `message` happens to equal one of those two literal code
strings (an untranslated echo), the client blanks it and shows canned copy instead; otherwise the server's
own localized sentence is shown verbatim. Center-owned teachers get an "ask your center" hint instead of a
"View plans" button on every variant. `ManagerialSubscriptionNoStudents` / `PlanNoStudentAccounts` /
`ParentPortalRequiresSubscription` are **not pattern-matched anywhere in the Flutter app** — if returned,
they would render as a plain generic message toast.

---

### Endpoint coverage

**Used:**
- `GET api/v1/payments/tracking`
- `GET api/v1/payments/students`
- `GET api/v1/payments/collect/students`
- `GET api/v1/collect/lookup`
- `POST api/v1/collect/submit`
- `PUT api/v1/payments/students/{teacherStudentId}/proration`
- `GET api/v1/payments/collections`
- `GET api/v1/payments/collections/summary`
- `POST api/v1/payments/forgive`
- `POST api/v1/payments/forgive/{forgivenessId}/reverse` (implemented end-to-end, **zero UI callers** — dead wiring, no "undo forgiveness" button exists)
- `GET api/v1/assistants/{assistantId}/wallet`
- `POST api/v1/assistants/{assistantId}/wallet/withdraw`
- `GET api/v1/assistants/{assistantId}/wallet/withdrawals`
- `GET api/Payment/students/{teacherStudentId}/payment-view`
- `GET api/Payment/students/{teacherStudentId}/history`
- `PUT api/Payment/students/{teacherStudentId}/custom-amount`
- `PUT api/Payment/transactions/{transactionId}`
- `DELETE api/Payment/transactions/{transactionId}`
- `GET api/Payment/transactions/{transactionId}/edit-logs`
- `POST api/Payment/transactions/batch-revert` (only ever called with a single-element `transactionIds[]`)
- `GET api/Payment/students/{teacherStudentId}/departure-summary`
- `POST api/Payment/departure/confirm`
- `GET api/Payment/departures`
- `GET api/Payment/unpaid`
- `GET api/Payment/wallets` (offline-snapshot hydration only, not any live screen)
- `POST api/Payment/sync`
- `GET api/subscription/status`
- `GET api/subscription/pricing`
- `GET api/subscription/requests`
- `POST api/subscription/requests`
- `DELETE api/subscription/requests/{requestId}`

**Defined in `web_constant.dart` but never called anywhere in the app (dead client-side wiring):**
- `POST api/v1/payments/collect/mark-paid` — fully implemented (incl. an offline-queued variant); the
  "Mark N students as Paid" button was redesigned onto `collect/submit` instead (2026-08-22)
- `GET api/Payment/wallets/{assistantId}` — superseded by `api/v1/assistants/{id}/wallet`
- `POST api/Payment/wallets/{assistantId}/reset` — superseded by the v1 withdraw endpoint
- `GET api/v1/payments/collections/yearly` — data/domain layers exist; no cubit or view calls it
- `GET api/Subscription/current` — fully implemented; the live screen uses `/subscription/status` instead
- `GET api/subscription/requests/{requestId}` — `webPathSubscriptionRequestById` is used only for the
  `DELETE` (cancel); the app never issues a GET-by-id
