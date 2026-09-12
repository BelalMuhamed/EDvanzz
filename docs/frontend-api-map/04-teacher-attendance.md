# 04 — Teacher: Attendance & Reports

Source: `lib/feature/teacher_module/attendance/**` (~66 files) and `lib/feature/teacher_module/reports/**`.
Endpoint constants: `lib/core/network_services/web_constant.dart` (`webPathAttendance*`, lines ~239–285).
Data flow: `TeacherAttendanceRemoteDataSource` (Dio) → `TeacherAttendanceRepositoryImpl` (adds offline
queue/snapshot behaviour) → cubits → views. All responses are read through the standard envelope
`{success, code, message, data}`; list endpoints return the standard paginated shape
(`items`/`data`, `page`, `pageSize`, `totalCount`, `totalPages`) unless noted.

**Attendance status wire values** (`TeacherAttendanceStatus` / `lib/.../teacher_attendance_status.dart`)
— sent and read as **strings**, exact casing: `"Present"`, `"Absent"`, `"CrossSessionPresent"`, `"Held"`.
`CrossSessionPresent` is server-only (never sent by the client) — it is read back and treated as a
"present" variant everywhere (summary counts, month-grid cycling, roster filters).

**Attendance method wire values** (`TeacherAttendanceMethod`) — string, sent as `attendanceMethod` on
every mark/hold/sync body: `"ManualCode"` (typed student code), `"MultiSelect"` (tap-to-mark or bulk
"mark present" from the register), `"BarcodeScan"` (QR/barcode scan queue submit).

**Date/time formats actually sent:**
- `occurrenceDate` on `mark` / `mark-bulk` / `mark-hold` / `release-hold` / `add` / `sync` request
  bodies is a **full ISO-8601 datetime string at local midnight** —
  `DateTime(year, month, day).toIso8601String()`, e.g. `"2026-09-12T00:00:00.000"` — never a bare
  `"yyyy-MM-dd"` date.
- `occurrenceDate` as a **query parameter** (`GET .../sessions/{id}/students`) uses the same
  ISO-8601-at-midnight string.
- The **path-fallback** route (`GET .../sessions/{id}/occurrences/{date}/students`) URL-encodes that
  same ISO string into the path segment.
- Month endpoints (`GET .../sessions/{id}/month`, `GET .../timeline/students/{id}/month`) send plain
  `year`/`Year` + `month`/`Month` integers, not a date string.

---

## Attendance (sessions/groups tab)
_Dart file: `lib/feature/teacher_module/attendance/view/teacher_attendance_view.dart`_
**Reached from:** bottom nav / side menu → "Attendance" tab.

This screen has **no attendance-specific API of its own** — it re-lists the teacher's Sessions/Groups
via `TeacherSessionsCubit` (Sessions module; `GET api/sessions`, `GET api/session-groups`, outside
this chapter's endpoint family) and routes each row into the screens below.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Tap a session → "Take attendance" | *(navigation only)* | — | `AppRoute.goToTeacherTakeAttendance(sessionId, sessionTitle)` |
| Tap a session → "View attendance" | *(navigation only)* | — | `AppRoute.goToTeacherViewAttendance(sessionId, sessionTitle)` |
| Tap a session → "Edit attendance" | *(navigation only)* | — | `AppRoute.goToTeacherEditAttendance(sessionId, sessionTitle)` |
| Tap a group | *(navigation only)* | — | opens **Group Sessions** below |

## Group Sessions (attendance context)
_Dart file: `lib/feature/teacher_module/attendance/view/teacher_attendance_group_sessions_view.dart`_
**Reached from:** Attendance tab → tap a group.

Same session list as above, scoped to one group (`TeacherSessionsCubit(groupId: …)`, Sessions
module API) with the identical take/view/edit-attendance row actions. No attendance-family endpoint.

---

## Take Attendance (the register)
_Dart file: `lib/feature/teacher_module/attendance/view/teacher_take_attendance_view.dart`_
_Cubit: `TeacherAttendanceSessionStudentsCubit` (implements the shared `TeacherTakeAttendanceCubit` contract, also used by the exam SeparateTime flow via a sibling cubit)_
**Reached from:** Attendance tab / Group Sessions → "Take attendance"; also opened by an exam's
"take attendance for this class day" action (`examId` + `occurrenceDate` passed in) and by
View/Edit Attendance's month-grid header. Header names the session **and** the exact class day
(`teacherAttendanceMarkingToday` / `teacherAttendanceMarkingOtherDay`) — a scanner or a tap always
writes to the day shown here, never to "today" implicitly (BUG-15 fix, see repo CLAUDE.md §7.8).

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load / pull-to-refresh / search / filter chip / infinite scroll | `GET api/Attendance/sessions/{sessionId}/students` | Query: `occurrenceDate` (ISO datetime, local midnight), `Page`, `PageSize`, `Search` (trimmed, omitted if empty), `UnmarkedOnly=true` \| `MarkedOnly=true` \| `AssignedOnly=true` \| `LinkedOnly=true` (one of these per active filter chip, omitted for "All") | `data.students[]` (or `items`/`data` fallback keys) each with `teacherStudentId`/`studentId`, `studentName`/`name`, `studentCode`/`code`, `profileImageUrl`/`avatarUrl`, `sessionName`/`sourceSessionName`, `sessionId`/`sourceSessionId`, `status`/`attendanceStatus`/`currentStatus` (string enum, absent = unmarked), `attendanceRecordId`/`recordId`/`id`, `wasAbsentLastSession`, `totalAbsences`, `consecutiveAbsences`, `absenceConsequenceLabel` (from nested `historyInfo`), `paymentInfo` object (see below), plus `page`/`totalCount`/`pageSize` → `hasMore`, and top-level `assigned_count`/`assignedCount`, `not_assigned_count`/`notAssignedCount`, `showAttendanceHistory`, `showPaymentInfo` |
| Row tap (unmarked student) → mark-status sheet → pick Present/Absent/Hold | `POST api/Attendance/mark` (Present/Absent) **or** `POST api/Attendance/mark-hold` (Hold) | `mark`: `{teacherId, sessionId, teacherStudentId, status, attendanceMethod:"MultiSelect", occurrenceDate, absenceAlertConfirmed?}` (last key sent only when `true`). `mark-hold`: `{teacherId, sessionId, teacherStudentId, occurrenceDate}` | `data.attendanceRecordId`/`recordId`/`id`; `data.requiresAbsenceAlertConfirmation`/`requiresAbsenceAlert` (true → re-prompts the absence-alert sheet, then resends with `absenceAlertConfirmed:true`); `data.isDuplicate`/`duplicate` (true → "already recorded" toast, no list change) |
| Absence/payment pop-up appears (before a Present mark, when the teacher enabled the settings) | *(no call of its own)* | — | Reads `student.wasAbsentLastSession` + `consecutiveAbsences` + `absenceConsequenceLabel`, and `student.paymentInfo.{hasUnpaidLastMonth, hasUnpaidCurrentMonth, unpaidMonthsCount, unpaidAmount, unpaidMonthLabels}` already present on the roster row (`showAttendanceHistory`/`showPaymentInfo` gate whether these ever populate) |
| Pop-up → "Collect" | *(opens the Payment collect flow — separate chapter)* | — | on success, clears `paymentInfo` optimistically client-side so the same student doesn't re-alert until next roster reload |
| "Select" → checkbox rows → "Mark N students as present" | `POST api/Attendance/mark` **once per selected student**, sequentially (not a single batch call) — each student that has an absence/payment alert is prompted first | Same `mark` body as above, `attendanceMethod:"MultiSelect"` | Same per-call `mark` response; students that come back duplicate/errored stay selected with a per-row reason, and a retry dialog offers to resubmit only those |
| QR icon (top-right) | *(navigation only)* | — | opens **QR/Barcode Scan** below, handing it the CURRENT `occurrenceDate` |
| Filter icon | *(no call — local sheet)* | — | `TeacherAttendanceListFilter`: `all` / `assigned` / `unassigned` / `marked` / `unmarked`, badge counts from the roster summary already loaded |
| Present/Remaining/Hold summary cards (via "Attendance Saved"/"Attended Result" screens, not this one directly) | see **Roster by status** below | — | — |

Non-trivial request body — a Present mark with the absence-alert already confirmed:
```json
{
  "teacherId": 42,
  "sessionId": 228,
  "teacherStudentId": 5311,
  "status": "Present",
  "attendanceMethod": "MultiSelect",
  "occurrenceDate": "2026-09-12T00:00:00.000",
  "absenceAlertConfirmed": true
}
```

Error handling: a `409`/business-conflict response is read via `isDuplicate` and shown as
`teacher_attendance_scan_result_duplicate` ("Attendance was already recorded for this student
today."), never as a generic error. `requiresAbsenceAlertConfirmation` is not an HTTP error — it is
a normal `200` whose payload asks the client to re-confirm before persisting.

**Offline behavior (this screen and the QR scan screen share it):** see the dedicated
[Offline queue & sync](#offline-queue--sync-api-attendancesync) section below — every mark/hold from
here goes through the same outbox.

---

## QR / Barcode Scan
_Dart file: `lib/feature/teacher_module/attendance/view/teacher_attendance_qr_scan_view.dart`_
**Reached from:** Take Attendance → QR icon. Builds its **own** roster cubit
(`TeacherAttendanceSessionStudentsCubit`, loaded after a 600 ms delay so the camera can bind first) —
it does **not** share the register's cubit — so the caller must pass `occurrenceDate` explicitly
(BUG-15: dropping this made every scan land on "today" regardless of which class day was open).

**Resolution is entirely client-side — there is no "resolve scanned code" endpoint.** Two payload
shapes are handled:
1. **JSON QR payload** `{"id": <int>, "name": <string>, "code": <string>, "module": "student"}`,
   decoded locally (`StudentBarcodeQrPayload.tryParse`); the app then calls
   `cubit.lookupStudentByCode(payload.code)` to enrich it with absence/payment info from the roster.
2. **Plain 1D barcode** (just the student code text) → `cubit.lookupStudentByCode(rawText)` directly.

`lookupStudentByCode`: first an in-memory exact-match scan over students already loaded (works
offline, zero network); on a miss it re-runs the roster GET with `Search=<code>` (same
`GET api/Attendance/sessions/{sessionId}/students` as the register) and does an **exact** code match
client-side over the results (never `.first` on a fuzzy match). No dedicated lookup/resolve endpoint
exists on the wire for this.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Camera detects / manual code submit | `GET api/Attendance/sessions/{sessionId}/students` (via `lookupStudentByCode`, only on local-cache miss) | Query: `occurrenceDate`, `Search=<code>` | Student row (see Take Attendance table) — enqueued locally into a scan queue, NOT yet sent to the server |
| Student found + absence/payment alert applies | *(local sheet only)* | — | Same `shouldShowAbsenceAlert`/`shouldShowPaymentAlert` gates as the register |
| Student not found (JSON payload's `id` isn't in this session's roster) | *(no call — local)* | — | Center-mode hook `onCenterRosterMiss`, else a "Student not found" dialog reading the roster GET's last error message |
| "Revert last" | *(local only — removes the last queued item)* | — | — |
| "View actions (N)" | *(navigation only)* | — | opens **Scan Queue Review** below with the in-memory queue |
| Leave / cancel | *(navigation only, pops `didMutate`)* | — | — |

The scan queue itself (`TeacherAttendanceQueuedScan`) is pure client state — `teacherStudentId`,
`studentName`, `studentCode`, `profileImageUrl`, `absenceConsequenceLabel`, `status` (Present or Held
only at this stage), `failureReason` — nothing is sent to the server until the review screen submits.

---

## Scan Queue Review ("View Result")
_Dart file: `lib/feature/teacher_module/attendance/view/teacher_attendance_scan_view_result_view.dart`_
_Cubit: `TeacherAttendanceScanViewResultCubit`_
**Reached from:** QR/Barcode Scan → "View actions (N)".

Lets the teacher cycle each queued student's status (Present → Hold → Absent → Present) and remove
rows (edit mode, swipe-to-dismiss) before one combined submit.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| "Submit ({count}) students" (session flow — the common case) | **Offline-first batch**: each queued item is durably queued locally first (`OfflineOp`), then drained — see [Offline queue & sync](#offline-queue--sync-api-attendancesync) for exactly what goes over the wire (`POST mark`/`POST mark-hold` for a single fresh item, `POST api/Attendance/sync` for a batch) | one `{teacherStudentId, status, studentName, studentCode, absenceAlertConfirmed}` queue entry per row → mapped to the offline-op payload shown below | Per-item outcome: `synced` / `queued` (saved offline) / `needsConfirmation` / `conflict` / `failed` — only `synced`/`queued` rows are cleared from the queue; everything else stays on screen with its own reason so the teacher can retry |
| Submit (exam SeparateTime flow — different cubit) | `POST api/Attendance/mark-bulk` (legacy online-only bulk path, unrelated to the offline outbox) | `{teacherId, sessionId, items:[{teacherStudentId, status}], attendanceMethod:"BarcodeScan", occurrenceDate}` | `data.successCount`, `data.skippedCount`, `data.results[]` → `{teacherStudentId, success, code, reason}` per student; skipped students stay in the queue with their reason |
| Toggle status of one row (edit mode) | *(local only — cycles Present→Held→Absent)* | — | — |
| Swipe to remove a row (edit mode) | *(local only)* | — | — |

```json
// mark-bulk request body (exam SeparateTime path only)
{
  "teacherId": 14,
  "sessionId": 81,
  "items": [
    { "teacherStudentId": 5311, "status": "Present" },
    { "teacherStudentId": 5312, "status": "Absent" }
  ],
  "attendanceMethod": "BarcodeScan",
  "occurrenceDate": "2026-09-12T00:00:00.000"
}
```

A `409`-style "already marked" duplicate never bulk-fails the whole request — the backend reports it
per student in `results[]`; the client never treats HTTP success as "every scanned student was
marked" (see `BulkMarkAttendanceResultApiDto`).

---

## Attendance Saved (success splash)
_Dart file: `lib/feature/teacher_module/attendance/view/teacher_attendance_saved_view.dart`_
**Reached from:** after a successful session-flow submit (bulk mark or scan-queue submit).
No API call — a celebratory animation that auto-advances (1.8s) to **Attended Students Result**.

## Attended Students Result
_Dart file: `lib/feature/teacher_module/attendance/view/teacher_attendance_attended_result_view.dart`_
**Reached from:** Attendance Saved (auto) or directly after a submit.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load / infinite scroll | `GET api/Attendance/sessions/{sessionId}/students` (same as Take Attendance, no filter — full page-1 load then paged) | Query: `occurrenceDate`, `Page`, `PageSize` | Same roster row shape as Take Attendance, rendered read-only |
| Present / Remaining / Hold summary cards | *(navigation only)* | — | opens **Roster by status** below (`TeacherAttendanceRosterKind.present`/`.remaining`/`.hold`) |
| "Export" button | *(none — placeholder)* | — | shows a "Coming soon" toast; not wired to `api/Attendance/reports/export` |

---

## Roster by status (Present / Absent / Hold / Remaining)
_Dart file: `lib/feature/teacher_module/attendance/view/teacher_attendance_roster_view.dart`_
**Reached from:** Attended Result's summary cards.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load (`present`/`hold` kind) | `GET api/Attendance/sessions/{sessionId}/students`, paged through **all** pages client-side (`fetchAllPaginatedEitherItems`, pageSize 10 per call) then filtered locally to `status == Present/CrossSessionPresent` or `status == Held` | Query: `occurrenceDate`, `MarkedOnly=true`, `Page`, `PageSize` | Full filtered student list, no further filter param exists for a specific status server-side |
| Screen load (`remaining` kind) | Same endpoint, server filter `UnmarkedOnly=true` | Query: `occurrenceDate`, `UnmarkedOnly=true`, `Page`, `PageSize` | Unmarked students |
| Pull to refresh | Re-runs the same fetch | — | — |

Note: there is **no server-side status-specific filter** (e.g. `?status=Present`) — the client always
requests `MarkedOnly`/`UnmarkedOnly` and narrows Present vs Hold itself from the returned `status`
field.

---

## View / Edit Attendance (monthly grid)
_Dart file: `lib/feature/teacher_module/attendance/view/teacher_session_attendance_month_view.dart`_
_Cubit: `TeacherSessionAttendanceMonthCubit`_
**Reached from:** Attendance tab session row → "View attendance" / "Edit attendance"; session-actions
bottom sheet. Edit mode is gated client-side by `canEditPastAttendance(user)` (permission check) — a
denied user sees the **Needs Permission** empty state instead.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load / month arrow / search (View only) / infinite-scroll more students / "load more occurrence columns" | `GET api/Attendance/sessions/{sessionId}/month` | Query: `year`, `month`, `page` (student page, clamped 1–100), `pageSize` (25), `occurrencePage`, `occurrencePageSize` (clamped 1–31), `search` (View screen's debounced search box, name OR code) | `data.occurrences[]` → `{occurrenceId, date/occurrenceDate}` (date columns); `data.students[]`/`data.rows[]` → `{teacherStudentId/studentId, studentName/name, studentCode, monthPresentCount, monthAbsentCount, cells[]}`; each cell → `{occurrenceId, occurrenceDate/date, isMarked, status/attendanceStatus, attendanceRecordId}`; pagination fields `page/totalPages/totalCount` (students) and `occurrencePage/occurrenceTotalPages/occurrenceTotalCount` (date columns) |
| Edit mode: tap a cell to cycle Present↔Absent (existing record) | `PUT api/Attendance/edit` | `{teacherId, attendanceRecordId, newStatus}` (`newStatus` is `"Present"` or `"Absent"` only — the grid never produces Held) | 204/void envelope; on success the month page is reloaded (`force:true`) |
| Edit mode: tap a cell with no existing record | `POST api/Attendance/add` | `{teacherId, sessionId, teacherStudentId, occurrenceDate, status}` | Same; a `409`/duplicate-style failure triggers a fallback record-id lookup (below) then retries as `PUT edit` |
| — resolving which `attendanceRecordId` an edit/add-conflict belongs to | `GET api/Attendance/sessions/{sessionId}/students` (paged, `PageSize=10`, up to 100 pages) **or** its path-fallback `GET .../occurrences/{date}/students` | Query: `occurrenceDate`; matched client-side by `teacherStudentId` | First non-null `attendanceRecordId`/`recordId`/`id` for that student on that occurrence |
| "Save {count} changes" | Applies every buffered cell edit **sequentially** as the two calls above (not a single bulk endpoint) | — | On any failure, the specific server message is surfaced (e.g. "date before the student was assigned") |
| Leave with unsaved edits | *(local confirm dialog only — "Discard changes?")* | — | — |
| Permission denied (edit mode, non-owner) | *(no call — local gate)* | — | Shows **Needs Permission** in place |

```json
// PUT api/Attendance/edit
{ "teacherId": 42, "attendanceRecordId": 91234, "newStatus": "Absent" }
```
```json
// POST api/Attendance/add
{
  "teacherId": 42,
  "sessionId": 228,
  "teacherStudentId": 5311,
  "occurrenceDate": "2026-08-15T00:00:00.000",
  "status": "Present"
}
```

## Needs Permission
_Dart file: `lib/feature/teacher_module/attendance/view/teacher_attendance_needs_permission_view.dart`_
No API — static empty state shown when a non-owner account (assistant) lacks the "Edit past
Attendance" permission.

---

## Attendance reverted / deleted (unreachable in current build)
_Dart files: `teacher_attendance_reverted_view.dart`, `teacher_attendance_deleted_view.dart`_

Both are plain success splashes wired end-to-end (`AppRoute.goToTeacherAttendanceReverted` /
`goToTeacherAttendanceDeleted`), and the cubit methods that would justify them —
`TeacherAttendanceSessionStudentsCubit.revertLastRecord()` (→ `DELETE api/Attendance/delete`) and
`.deleteAllOccurrenceRecords()` (→ the same endpoint, once per record) — are fully implemented, but
**no view or widget in the current codebase calls either the cubit methods or the two route helpers.**
`DELETE api/Attendance/delete` is therefore not reachable from any live navigation path today.

## Legacy scan result (unreachable in current build)
_Dart file: `teacher_attendance_scan_result_view.dart`_

A single-student scan-outcome screen (`TeacherAttendanceScanOutcome`: studentFound/present/absent/
hold/notFound/duplicate). `AppRoute.goToTeacherAttendanceScanResult` is only ever called from
*inside this same file* — nothing else in the app pushes it. The live QR flow uses **Scan Queue
Review** instead. One line, no live API surface.

---

## Absent Students (violations list)
_Dart file: `lib/feature/teacher_module/sessions/view/teacher_violation_students_list_view.dart` (Sessions module UI; backed by the attendance-absences endpoint)_
_Cubit: `lib/feature/teacher_module/sessions/manager/teacher_session_absent_students_cubit/`_
**Reached from:** a session's Hub view → absence-violations card → "View all".

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load / search / infinite scroll | `GET api/Attendance/sessions/{sessionId}/absences` | Query: `MinConsecutiveAbsences` (default 1), `Page`, `PageSize` (20), `Search` (trimmed, omitted if empty) | `data.items[]`/paginated → `{teacherStudentId, studentName, studentCode, consecutiveAbsences}`; `totalCount`/`hasMore` drive paging |

Server-filtered and worst-first sorted — the client never loads the whole roster to compute this
list. No status filter beyond the absence-streak threshold.

---

## Student Attendance History (timeline + month)
_Dart file: `lib/feature/teacher_module/student_profile/view/teacher_student_attendance_history_view.dart` (lives under `student_profile/`, driven entirely by the attendance timeline endpoints)_
_Cubit: `lib/feature/teacher_module/attendance/manager/teacher_student_attendance_timeline_cubit/`_
**Reached from:** a student's profile → "Attendance history".

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load (fires both, sequentially) | `GET api/Attendance/timeline/students/{teacherStudentId}` | — | `data.sessionsAttended`, `data.sessionsMissed` (or `attendedCount`/`missedCount`/`presentCount`/`absentCount` fallbacks), `data.totalAbsences` — powers the two progress bars |
| Screen load (2nd call, current calendar month) | `GET api/Attendance/timeline/students/{teacherStudentId}/month` | Query: `Year`, `Month` (current month, not user-selectable on this screen) | `data.records[]`/`items`/`occurrences`/`missedSessions` → `{attendanceRecordId/recordId/id, sessionName/sessionTitle, sessionSubtitle/packageSummary/scheduleLabel, occurrenceDate/periodStart/date/sessionDate, status/attendanceStatus/currentStatus}`; the view keeps only `Absent`/`Held` records as "missed sessions" (both render as the same "absent" chip) |
| Pull to refresh | Re-runs both calls | — | — |

`GET api/Attendance/timeline/students/{studentId}/export` is **defined in `web_constant.dart` but
never called anywhere in the app** — there is no export button on this screen.

---

## Attendance dashboard (consumed by Teacher Home, not its own screen)
_Consumer: `lib/feature/teacher_module/home/manager/teacher_home_cubit/teacher_home_cubit.dart`_

`GET api/Attendance/dashboard` (optional query `date`, `yyyy-MM-dd`-equivalent ISO string) is called
once from the **Teacher Home** screen load, not from anything under the Attendance tab. Response:
`data.sessionCards[]` → `{sessionId, sessionName, isExamSession, totalStudents, examId, examName}`
and `data.examsToday[]` → `{examId, name, sessionId, sessionName, assignedCount}`. Home uses these
purely to flag which session cards are "exam sessions today" and list today's exams — it carries no
present/absent counts.

---

## Reports Hub
_Dart file: `lib/feature/teacher_module/reports/view/teacher_reports_hub_view.dart`_
**Reached from:** bottom nav / side menu → "Reports".

**No report-generation API exists yet.** Every row is a static catalog entry
(`TeacherReportCatalog.all`, `lib/feature/teacher_module/reports/model/teacher_report_type.dart`) that
either navigates elsewhere or opens a placeholder preview:

| Report row | Action taken |
|---|---|
| Full Student List, Unassigned Students | Navigates to the Students list (`AppRoute.goToTeacherAddStudent`) — no attendance-family API |
| Session Students, Group Students, Session Attendance History | Navigates to the Sessions list (`AppRoute.goToTeacherSessions`) — no attendance-family API |
| Single Student Absence, Session Absence, All Sessions Absence, Single Student Payment History, Session Payment | Opens **Report Preview** below with a hardcoded "coming soon" summary — **no API call at all**, not even `api/Attendance/reports` |
| All Sessions Payment Summary | Navigates to Payment collections screen (separate chapter) |
| Unpaid Students | Navigates to Payment students-by-status screen (separate chapter) |

`api/Attendance/reports` and `api/Attendance/reports/export` are **defined in `web_constant.dart` and
never referenced anywhere else in the codebase** — the Reports Hub does not call them.

## Report Preview
_Dart file: `lib/feature/teacher_module/reports/view/teacher_report_preview_view.dart`_
**Reached from:** Reports Hub → any "coming soon" row.

No API. Renders the summary rows it was handed and offers Excel/PDF format tiles; "Generate" checks
`args.canGenerateLocally` (true only when a caller supplied real export rows/student ids — never the
case for the attendance/payment "coming soon" rows) and otherwise just shows a "Coming soon" toast.
When it IS generatable, it hands off to the shared teacher-export pipeline (different chapter), not
to `api/Attendance/reports/export`.

---

## Offline queue & sync (`api/Attendance/sync`)

Every mark/hold made from **Take Attendance** and **Scan Queue Review** is written **enqueue-first**
into a local outbox (`OfflineOp`, `lib/core/offline/offline_op.dart`) before any network call — this
is true even when the device is online; the outbox is drained immediately after enqueueing rather
than skipped.

**What gets queued.** One `OfflineOp` per student action:
- `opType: attendanceMark` (Present/Absent), built by `TeacherAttendanceOfflineOps.buildMarkOp`
- `opType: attendanceHold` (Held), built by `.buildHoldOp`
- `entityKey`: `"att:{sessionId}:{teacherStudentId}:{yyyy-MM-dd}"` — coalescing key, at most one live
  op per student per session per class day
- `payload` (local storage shape, NOT the wire body): `{teacherId, sessionId, teacherStudentId,
  status, attendanceMethod, occurrenceDate, studentName, studentCode}` — `studentName`/`studentCode`
  are display-only for the sync-center UI, never sent to the server
- `opId` — doubles as the server-facing **`clientEntryId`** on `POST api/Attendance/sync` and is the
  idempotency key the server is expected to de-duplicate replays against
- `absenceAlertConfirmed` (bool, rides on the op itself, not the payload)
- `ambiguousDelivery` (bool) — set when a previous send may have reached the server without a
  response (timeout / app killed mid-send); such ops are reconciled against the live roster row
  before any resend, never blindly resent

**How it's sent** (`TeacherAttendanceOpTransport.send`, routing logic):

| Case | Endpoint |
|---|---|
| A single fresh unconfirmed mark (0 prior attempts, not ambiguous) | `POST api/Attendance/mark` or `POST api/Attendance/mark-hold` — identical body to an online tap (parity path) |
| 2+ unconfirmed marks, or a batch from the scan queue | `POST api/Attendance/sync`, chunked at **50 entries per call** |
| A mark already confirmed via the absence-alert sheet (`absenceAlertConfirmed:true`) | Always `POST api/Attendance/mark` (with `absenceAlertConfirmed:true`) — **`sync` hardcodes this flag to `false` server-side**, so a confirmed mark is never routed through `sync` |
| Holds | Always `POST api/Attendance/mark-hold`; a 409 is reconciled against the current roster row for that student (matching status ⇒ treated as synced; mismatched ⇒ conflict) |

**`POST api/Attendance/sync` request body** (`AttendanceSyncRequest.toJson`):
```json
{
  "teacherId": 42,
  "entries": [
    {
      "teacherStudentId": 5311,
      "sessionId": 228,
      "status": "Present",
      "attendanceMethod": "BarcodeScan",
      "occurrenceDate": "2026-09-12T00:00:00.000",
      "clientRecordedAt": "2026-09-12T07:41:03.512Z",
      "clientEntryId": "8f2c9e10-...-a1b2"
    }
  ]
}
```
- `clientEntryId` = the outbox `opId` — the idempotency key; the server is expected to treat a
  replayed `clientEntryId` against an already-existing same-status record as an idempotent success
  rather than a fresh write.
- `clientRecordedAt` = the device-local UTC instant the mark actually happened offline (NOT the send
  time), so a delayed drain doesn't misdate when the action occurred.
- `status` uses the same string enum as every other attendance call.

**Per-entry result** (`data.entryResults[]`, read by `AttendanceSyncEntryResultApiDto`), plus the
envelope's own totals:
- `data.totalSubmitted`, `data.successCount`, `data.conflictCount`, `data.failedCount`,
  `data.requiresConfirmationCount`
- Each entry: `clientEntryId` (echoed back, used to match to the local op), `success` (bool),
  `isConflict` (bool), `requiresAbsenceConfirmation` (bool), `absenceAlertRaised` (bool — **the mark
  WAS recorded**; this is purely informational, never a reason to treat the op as unsaved),
  `errorMessage` (server sentence, used as-is), `errorCode` (stable string the client re-localizes
  itself — the one defined value is `"NoOccurrenceOnDate"`, worded client-side as "no class for this
  session on that day"), `serverRecord` (raw object shown on the unsent-records screen for a
  conflict), `absenceAlertInfo` (object carried through to the "absence follow-up" UI when
  `absenceAlertRaised` is true).
- Outcome mapping the app applies: `success:true` → op marked **synced** (roster snapshot patched
  with the new status); `isConflict:true` → **conflict** (kept visible, never silently dropped);
  `requiresAbsenceConfirmation:true` → **needsConfirmation** (held back until re-confirmed — never
  painted as marked in the meantime); anything else → **failed**, with `errorCode`/`errorMessage` as
  the reason shown next to that student.
- A `clientEntryId` the server didn't echo back at all is treated as **ambiguous** and retried on the
  next drain rather than assumed lost or assumed successful.

**Reconciliation instead of blind conflict** (`SyncOfflineRecordsAsync`-adjacent client logic,
matches CLAUDE.md §7.2c): a `409`/mismatch on a single `mark`/`mark-hold` call triggers
`_reconcileAgainstRosterRow` — one `GET api/Attendance/sessions/{sessionId}/students?Search=<code>`
call to fetch that student's current row; if its live `status` already matches what was intended, the
op is treated as **synced** (not a conflict — this is what makes the client tolerant of the
auto-absent sweep / held-row resolution happening server-side between the offline action and the
drain). Only a genuinely different status is surfaced as a conflict.

**Sync center entry point:** the offline banner (shown on every attendance screen while any op is
queued/draining) taps through to `AppRoute.goToSyncCenter` (`lib/core/offline/view/sync_center_view.dart`
— shared across modules, not attendance-specific, so not detailed further in this chapter).

---

### Endpoint coverage

**Used:**
- `GET api/Attendance/dashboard` — Teacher Home (exam-awareness hints only)
- `POST api/Attendance/mark`
- `POST api/Attendance/mark-bulk` (exam SeparateTime flow only)
- `PUT api/Attendance/edit`
- `POST api/Attendance/add`
- `POST api/Attendance/mark-hold`
- `POST api/Attendance/sync`
- `GET api/Attendance/sessions/{sessionId}/students`
- `GET api/Attendance/sessions/{sessionId}/occurrences/{occurrenceDate}/students` (404 path-fallback of the above, and used for `attendanceRecordId` lookups)
- `GET api/Attendance/sessions/{sessionId}/occurrences/calendar` (offline hydration + snapshot fallback only — no UI screen renders it directly)
- `GET api/Attendance/sessions/{sessionId}/absences`
- `GET api/Attendance/timeline/students/{teacherStudentId}`
- `GET api/Attendance/timeline/students/{teacherStudentId}/month`

**Defined in `web_constant.dart` but never called anywhere in the app (dead client-side wiring):**
- `POST api/Attendance/release-hold` — `ReleaseHoldRequest`/`releaseHold()` fully implemented end to
  end; nothing invokes it (a Held row is only ever overwritten via a fresh `mark`/`mark-bulk` call,
  never explicitly "released")
- `DELETE api/Attendance/delete` — reachable only through
  `TeacherAttendanceSessionStudentsCubit.revertLastRecord()` /`.deleteAllOccurrenceRecords()`, neither
  of which any view calls
- `GET api/Attendance/sessions/{sessionId}/unmarked-count` — the unmarked badge shown in the UI is
  computed client-side from the roster summary, not this endpoint
- `GET api/Attendance/records/{recordId}/history` — no "record history" screen exists; the closest
  equivalent is the student timeline/month pair above
- `GET api/Attendance/reports`
- `GET api/Attendance/reports/export`
- `GET api/Attendance/timeline/students/{studentId}/export`
