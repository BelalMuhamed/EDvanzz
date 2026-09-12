# 11 — Student App: Home, Linking, Attendance & Payments

Covers `lib/feature/student_module/home`, `linking`, `link`, `teacher`, `attendance`
and `payment`: the student's "My Teachers" home tab, the add-teacher / link-request
flow, the bottom-nav "Link" tab, the per-teacher home aggregate (the screen opened
from a teacher card), the student's own barcode, device registration, the
attendance month calendar and the payment tracking screen.

Every call uses `ApiService.client(requireAuth: true)` (JWT bearer) and responses
are unwrapped through the standard `{success, code, message, data}` envelope
(`ensureApiSuccess` throws on `success:false`, surfacing `message` as a toast).
`teacherId` in every path below is the numeric `Teacher.Id` of a *linked* teacher —
resolved once from the teacher-list row, never entered by the student. Two
cross-cutting things worth knowing up front:

- **Device lock.** Every request from this chapter's `ApiService` client silently
  carries an `X-Device-Id` header (plus `X-Device-Id-Previous` during a migration
  window) — not set per call, added globally. A 4xx body whose envelope `code` is
  `DeviceRegistrationRequired`, `DeviceMismatch` or `DeviceIdMissing` is intercepted
  app-wide (`ServerFailure.fromResponse` in `core/network_services/api_service_failure.dart`)
  and turned into a typed `DeviceLockFailure` instead of a plain toast — see
  **Teacher home** below for the two dialogs it drives.
- **Dead code worth flagging up front** (kept out of the endpoint coverage list,
  details noted where relevant): `linking/view/student_linked_teacher_detail_screen.dart`
  (+ its cubit, profile card and module-tiles widgets) is never reached from any
  live navigation — superseded by **Teacher home** below, reachable only via an
  unused `AppRoute.goToStudentLinkedTeacherDetail` helper. `link/view/widgets/scan_qr_code_bottom_sheet.dart`
  and `link/model/link_models.dart` / `link/view/widgets/link_widgets.dart` are
  likewise unreferenced (the QR-scan-to-link idea was replaced by "show my code"
  before shipping). `teacher/data/teacher_enrollment_repository_impl.dart` is an
  explicit stub — `enrollTeacher()` never calls the network; it fakes a 350ms
  delay and returns a canned DTO, with a `// TODO` for a `POST /student/teachers/enroll`
  that doesn't exist yet and isn't in `web_constant.dart`. `home/view/widgets/teacher_card_widget.dart`,
  `home_teachers_section_widget.dart` and the `add_teacher_dialog_*`/`add_teacher_modal.dart`
  files live under `student_module/home/` but the student flow never uses them —
  they back the **parent** module's home tab instead.

---

## My Teachers (Home tab)
_Dart file: `lib/feature/student_module/home/view/home_tab_view.dart`_
**Reached from:** bottom nav "Home" tab — the student's default landing tab.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen open / pull-to-refresh / app resumed from background | `GET api/studentuser/me/dashboard` | — | `isFirstLogin` (gates the FAB before any teacher is added). `studentAccountCode` and `linkedTeachers[]` are also parsed by the DTO but **not used here** — the list below comes from a separate call, and the code is only read by "My Student Code" |
| same trigger | `GET api/studentuser/me/teachers` | — | array of teacher-link rows, see keys below |
| Profile header (name + "Welcome back") | *(no call)* | — | `displayName` comes from the **already-loaded auth/login session**, not from any `me/profile` GET — `StudentHomeProfileRepository` is wired to a mock that reads `AuthCubit.state.user` |
| Tap "+" FAB / empty-state "Add teacher" button | opens **Add a teacher** (below) | — | — |
| Tap a card whose `status` is `Active` | *(navigation only)* → **Teacher home** | — | — |
| Tap a card in any other status | *(no call)* | — | status-specific toast (below); `Unlinked`/`CancelledByStudent` cards are inert (no message — the student ended those themselves) |
| Tap ⋮ on a card | opens **Manage teacher** (below) | — | — |

Teacher-link row keys (`StudentLinkedTeacherApiDto.fromJson`, one row per
`api/studentuser/me/teachers` array item):

```json
{
  "linkId": 501,
  "teacherId": 42,
  "status": "Active",
  "teacherCode": "A12",
  "teacherFullName": "Mohamed Omar",
  "subjectName": "Mathematics",
  "requestedAt": "2026-08-01T10:00:00Z",
  "respondedAt": "2026-08-01T14:30:00Z",
  "linkedAt": "2026-08-01T14:30:00Z",
  "isEnrollmentActive": true,
  "isLinked": true,
  "visibilityAttendance": true,
  "visibilityPayment": true,
  "visibilityHomework": false,
  "visibilityExamDefault": true
}
```

`status` is a plain server-authoritative string switched on directly (no
client-side derivation): `Active`, `Pending`, `Rejected`, `RemovedByTeacher`,
`AwaitingLink` (teacher accepted but hasn't bound a roster record yet — connected,
no access), `Unlinked`, `CancelledByStudent`. Only `Active` opens **Teacher home**;
tapping `RemovedByTeacher` / `AwaitingLink` / `Pending` / `Rejected` shows a
"contact your teacher" toast (`studentLinkRemovedContactTeacher` /
`studentLinkAwaitingContactTeacher` / `studentLinkPendingContactTeacher` /
`studentLinkRejectedContactTeacher`); `Unlinked`/`CancelledByStudent` render a
neutral "Not linked" badge and do nothing on tap. `visibilityAttendance` /
`visibilityPayment` / `visibilityHomework` / `visibilityExamDefault` are parsed
here but **not read by this screen** — the tiles they gate live on **Teacher
home**'s own `attendance.visible`/`payment.visible`/etc. from the per-teacher
aggregate, not from this list row.

---

## Add a teacher
_Dart file: `lib/feature/student_module/linking/view/widgets/add_teacher_link_bottom_sheet.dart`_
**Reached from:** the "+" FAB or the empty-state button on **My Teachers**; also
opened as a fallback by **Manage teacher**'s "Send request again" when the
student's display name isn't known yet.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| "Send Request" button | `POST api/studentuser/me/link-requests` | `{teacherCode, studentName, studentCode?}` | the created row (same shape as a teacher-list item above) is inserted/updated into the in-memory list by matching `teacherCode`; on success the sheet closes and the app navigates to **Request sent** |

```json
{
  "teacherCode": "12345678",
  "studentName": "Malak Ahmed",
  "studentCode": "A7"
}
```
- `teacherCode`: digits only, max 8 chars, required.
- `studentName`: required, max 200 chars.
- `studentCode` (optional): the **teacher's roster code** for this student
  (alphanumeric, max 10 chars) — sent only when non-empty; omitted entirely
  rather than sent as `null`/`""`.

No special `code` handling in this flow — a rejected request (e.g. unknown
teacher code) surfaces the server's `message` as a plain toast; the sheet stays
open with `_isSubmitting` reset so the student can retry.

---

## Manage teacher (⋮ actions)
_Dart file: `lib/feature/student_module/linking/view/widgets/student_teacher_link_actions_sheet.dart`_
(confirmation dialogs and the actual calls live in `home/view/home_tab_view.dart`)
**Reached from:** the ⋮ icon on any teacher card in **My Teachers**.

The sheet offers a status-appropriate pair of actions; every status can always
also be Deleted:

| Card status | Primary action offered | Endpoint | Sends |
|---|---|---|---|
| `Active` / `AwaitingLink` / `RemovedByTeacher` | Unlink teacher | `DELETE api/studentuser/me/teachers/{teacherId}` | — |
| `Pending` | Cancel request | `DELETE api/studentuser/me/teachers/{teacherId}` (same endpoint as unlink) | — |
| `Rejected` / `Unlinked` / `CancelledByStudent` | Send request again | `POST api/studentuser/me/link-requests` | `{teacherCode, studentName}` — reuses the card's own `teacherCode` and the student's own profile display name; **no `studentCode`** on this path |
| any status | Delete from my list | `DELETE api/studentuser/me/teachers/{teacherId}/delete` | — |

Unlink/cancel/delete each go through a destructive confirm dialog first, then a
localized success toast (`studentLinkUnlinkedSuccess` /
`studentLinkCancelRequestSuccess` / `studentLinkDeletedSuccess` /
`studentLinkRequestResentSuccess`). Unlink and cancel-request **keep the card**
(it reloads to show the new status, re-requestable); delete removes the card
from the in-memory list immediately (optimistic) and the teacher is told the
student left. After unlink/cancel the screen re-runs `GET api/studentuser/me/teachers`
(`force: true`) so the badge reflects the server's authoritative new status
rather than a locally-guessed one.

---

## Request sent (confirmation)
_Dart file: `lib/feature/student_module/linking/view/student_link_request_sent_screen.dart`_
**Reached from:** a successful **Add a teacher** submission.

No API calls — a static confirmation screen ("Request sent!" + a "Waiting for
approval" pill) with two buttons: back to My Teachers, or add another teacher
(reopens the **Add a teacher** sheet).

---

## My Student Code
_Dart file: `lib/feature/student_module/linking/view/student_my_code_screen.dart`_
**Reached from:** the "Show QR code" tile on the **Link tab** (see below); the
QR/code icon is the student's own shareable code, not a scanner.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen open | `GET api/studentuser/me/dashboard` | — | only `studentAccountCode` is read (same call **My Teachers** makes for `isFirstLogin`; here only the code matters) |
| "Copy" button | *(no call)* | — | copies `studentAccountCode` to the clipboard |
| "Share" button | *(no call)* | — | opens the OS share sheet with the code as plain text |

Profile header name/avatar on this screen also come from the local auth session,
same as **My Teachers**.

---

## Link tab (bottom nav)
_Dart file: `lib/feature/student_module/link/view/link_tap_view.dart` → `link/view/widgets/link_tab_view_body.dart`_
**Reached from:** bottom nav "Link" tab (student mode only — hidden for the
parent-mode shell).

A condensed, single-teacher rendering of the **Teacher home** aggregate for "the
first usable" linked teacher — `resolveUsableLinkedTeacher()` picks the first row
from the already-loaded **My Teachers** list with a non-null `teacherId`,
`isLinked == true` and `isEnrollmentActive == true`. It calls the exact same
endpoint with the exact same keys as **Teacher home** below (not re-documented
here); only the tab's own affordances differ:

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Tab opened, a usable teacher exists | `GET api/studentuser/me/teachers/{teacherId}/home` | `Year`, `Month` (device clock) | same fields as **Teacher home** |
| Tab opened, no usable teacher yet | *(no call beyond the already-loaded teacher list)* | — | shows "You haven't added any teachers yet" + a QR card |
| "Show QR code" tile | *(navigation only)* → **My Student Code** (not a scanner) | — | — |
| Pull-to-refresh | `GET api/studentuser/me/teachers` (force) then the home aggregate (force) | — | — |
| Attendance / Payment / Videos / Exams tiles | *(navigation only)* — identical targets to **Teacher home** | — | — |

The barcode card is hidden when `isBarcodeInAppEnabled` is `false` (teacher
issues physical cards only) — fails open (shown) while the summary is still
loading.

---

## Teacher home (per-teacher dashboard)
_Dart file: `lib/feature/student_module/teacher/view/teacher_details_view.dart` → `view/widgets/teacher_details_view_body.dart`_
**Reached from:** tapping an `Active` teacher card on **My Teachers**
(`AppRoute.goToTeacherHome`); also rendered condensed on the **Link tab**.

This is the central aggregate for the whole chapter — one call backs the
teacher's session assignment, attendance/payment/videos/exams/homework tiles.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen open / pull-to-refresh | `GET api/studentuser/me/teachers/{teacherId}/home` | query `Year`, `Month` — **from the device clock** (`DateTime.now()`), not server-resolved like Attendance/Payment below | see exhaustive list + JSON below |
| "Show QR code" | *(navigation only)* → **Student barcode / QR code** | — | — |
| Videos tile "View" | *(navigation only)* → video units list (chapter 10) | — | — |
| Online exams tile "View" / upcoming-exam chevron | *(navigation only)* → exams list | — | — |
| Offline exams "View All" | *(navigation only)* → offline exams screen (chapter 10's module; this screen only renders the pre-baked `exams.offlineMonths[]` chips) | — | — |
| Attendance card tap | *(navigation only)* → **Attendance — month calendar** | — | — |
| Payment card tap | *(navigation only)* → **Payment tracking** | — | — |
| Device-lock consent — "Continue on this device" | `POST api/studentuser/me/teachers/{teacherId}/register-device` | **no body** — identity carried only by the ambient `X-Device-Id` header | envelope success/message only; on success the home aggregate is re-fetched |
| Device-lock consent — "Sign out" | *(no call)* | — | logs the whole app out |
| Device-lock mismatch — "Contact" | *(no call — opens WhatsApp)* | — | pre-filled support message naming the teacher + the student's name/username |

### Home-aggregate response — every key the app reads

Top level (each has a documented fallback key the DTO also tolerates, listed in
parens; a backend only needs to emit the primary name):

- `teacherId` (`id`), `teacherName` (`name`), `subjectName` (`subject`),
  `teacherCode` (`code`) — int/string identity
- `linkStatus`, `isLinked` — string/bool
- `isBarcodeInAppEnabled` — bool, **defaults to `true`** if absent (fail-open)
- `sessionId` (nullable int), `sessionName` (nullable string — blank collapsed to `null`)
- `month` (int), `monthLabel` (string)
- `attendance.visible`, `.monthLabel` (falls back to top-level `monthLabel`),
  `.attendancePercentage`, `.presentDays` (`totalPresent`), `.absentDays`
  (`totalAbsences`), `.totalOccurrences`, `.markedOccurrences`
- `payment.visible`, `.monthLabel`, `.currentMonthStatus`,
  `.currentMonthAmountDue`, `.currentMonthAmountPaid`, `.overdueAmount`,
  `.overdueFromMonthLabel`, `.paidProgressRatio` (clamped `[0,1]`), `.currency`
- `videos.visible`, `.count`, `.notStartedCount`
- `homework.visible`, `.count`, `.upcoming[]` (`id`/`title`/`dueDate`/`status`) —
  **parsed but the homework tile is hard-disabled** in the current build
  (`homeworkVisible: false` forced client-side; kept for a future re-enable)
- `exams.onlineVisible` (`visible`), `.offlineVisible`, `.tileCount`
  (`count`/`total`), `.upcoming[]` (`examId`/`examName`/`subject`/`examDate`/
  `examTime`/`duration`/`questionsCount`), `.offlineMonths[]`
  (`month`/`label`/`status`/`examCount` — `isActive` is client-derived: `false`
  when `status` is `inactive`/`disabled`/`unavailable`, else `examCount > 0`)

```json
{
  "teacherId": 42,
  "teacherName": "Mohamed Omar",
  "subjectName": "Mathematics",
  "teacherCode": "A12",
  "linkStatus": "Active",
  "isLinked": true,
  "isBarcodeInAppEnabled": true,
  "sessionId": 228,
  "sessionName": "Grade 10 - Sunday",
  "month": 9,
  "monthLabel": "September",
  "attendance": {
    "visible": true,
    "monthLabel": "September",
    "attendancePercentage": 87.5,
    "presentDays": 7,
    "absentDays": 1,
    "totalOccurrences": 8,
    "markedOccurrences": 8
  },
  "payment": {
    "visible": true,
    "monthLabel": "September",
    "currentMonthStatus": "Partial",
    "currentMonthAmountDue": 300.0,
    "currentMonthAmountPaid": 150.0,
    "overdueAmount": 0.0,
    "overdueFromMonthLabel": "",
    "paidProgressRatio": 0.5,
    "currency": "EGP"
  },
  "videos": { "visible": true, "count": 12, "notStartedCount": 3 },
  "homework": { "visible": false, "count": 0, "upcoming": [] },
  "exams": {
    "onlineVisible": true,
    "offlineVisible": true,
    "tileCount": 2,
    "upcoming": [
      {
        "examId": 69,
        "examName": "Midterm Exam",
        "subject": "Mathematics",
        "examDate": "2026-09-15",
        "examTime": "22:53:00",
        "duration": 60,
        "questionsCount": 20
      }
    ],
    "offlineMonths": [
      { "month": 9, "label": "Sep", "status": "active", "examCount": 1 },
      { "month": 10, "label": "Oct", "status": "inactive", "examCount": 0 }
    ]
  }
}
```

### "Ask your teacher" note — exact rule

Rendered by `student_session_assignment_card.dart`. The session row itself
(`"Session · <name>"` vs `"Session · Not assigned yet"`) **always** renders. The
extra explanatory note renders only when **both**:
1. not effectively assigned — `sessionId == null` OR `sessionName` is null/blank, **and**
2. `summary.videos.visible == true` OR `summary.exams.onlineVisible == true`
   (the note names Videos and Online exams by name — promising them when the
   teacher has hidden both would be false)

### Envelope `code` handling

Not branched on directly by this screen's own code — `ensureApiSuccess` only
checks `success`. The shared `ServerFailure.fromResponse` (app-wide) is what
turns a non-2xx body into a typed failure using the envelope's `code`:

| `code` | Result |
|---|---|
| `DeviceRegistrationRequired` | `DeviceLockKind.registrationRequired` → consent sheet ("Use this device?" — Continue registers it, Sign out logs out) |
| `DeviceMismatch` or `DeviceIdMissing` | `DeviceLockKind.mismatch` → "Different device" dialog (Contact opens WhatsApp support, Go back dismisses) |
| anything else (4xx/5xx) | generic `ServerFailure`/`ConflictFailure` (409)/`NotFoundFailure` (404)/`NetworkFailure` → plain error toast, summary cleared |

### Device registration — exact trigger

Only reachable from the device-lock consent dialog raised when the home fetch
fails with `DeviceRegistrationRequired`. Tapping "Continue on this device" calls
`POST api/studentuser/me/teachers/{teacherId}/register-device` with **no request
body at all** — the only "payload" is the ambient `X-Device-Id` header
(salted-SHA256 of the Android SSAID, or an iOS Keychain UUID). On success the
lock clears and the home aggregate is re-fetched; if another device won the bind
in the meantime, the retry flips into the mismatch dialog instead.

---

## Student barcode / QR code
_Dart file: `lib/feature/student_module/teacher/view/student_teacher_qr_screen.dart`_
**Reached from:** "Show QR code" on **Teacher home** / **Link tab**.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen open / pull-to-refresh | `GET api/studentuser/me/teachers/{teacherId}/barcode` | — | `teacherId` (`id`), `teacherName` (`name`), `code`, `qrSvg` (`svg`) |
| "Share" button | *(no call — local export)* | — | — |
| "Download" button | *(no call — same local export as Share)* | — | — |

```json
{
  "teacherId": 42,
  "teacherName": "Mohamed Omar",
  "code": "STU-4821",
  "qrSvg": "<svg xmlns=\"http://www.w3.org/2000/svg\" ...>...</svg>"
}
```

`qrSvg` is **raw SVG markup**, rendered in-app via `SvgPicture.string`. Both
"Share" and "Download" call the same exporter: rasterize the SVG to a 1024×1024
PNG, write it to a temp file, then hand it to the OS share sheet
(`SharePlus`) — there is no direct "save to gallery" path; "Download" also opens
the share sheet. A failure here (including a device-lock rejection on the GET)
renders as a plain persistent empty-state message, not the typed dialog **Teacher
home** uses.

---

## Attendance — month calendar
_Dart file: `lib/core/shared_widgets/attendance/view/attendance_view.dart`_ (cubit
and models live in `lib/feature/student_module/attendance/`)
**Reached from:** the Attendance card/tile on **Teacher home** / **Link tab**
(`AppRoute.goToAttendance`).

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen open (first load, no explicit month) | `GET api/attendance/student/teachers/{teacherId}/month` | **no** `Year`/`Month` — omitted on purpose so the **server** picks the teacher-local (Africa/Cairo) current month | see keys below |
| same load | `GET api/attendance/student/teachers/{teacherId}/summary` | — | fetched and stored in state, but **not currently rendered by any widget** on this screen |
| Prev/next month arrows | `GET .../month` again | `Year`, `Month` (the shifted month, computed from the month the server previously resolved — never the device clock) | same keys |
| Pull-to-refresh | same GET(s), forced | same as current month | same |

Month response keys (`StudentAttendanceMonthApiDto`):

```json
{
  "year": 2026,
  "month": 9,
  "sessionId": 228,
  "sessionName": "Grade 10 - Sunday",
  "totalOccurrences": 8,
  "markedOccurrences": 8,
  "totalPresent": 7,
  "totalAbsences": 1,
  "attendancePercentage": 87.5,
  "days": [
    {
      "date": "2026-09-06",
      "sessionOccurrenceId": 9101,
      "sessionId": 228,
      "sessionName": "Grade 10 - Sunday",
      "status": "Present",
      "isPast": true
    }
  ]
}
```

`days[].status` string values the app switches on: `Present`, `Absent`,
`CrossSessionPresent` (both `Present` and `CrossSessionPresent` render as a
"present" calendar dot), `Held` and any other/missing value → unmarked (no
dot — used for scheduled-but-not-yet-marked or future days). Only `isAbsent`
(→ red dot) and `isPresent` (→ blue dot) are actually painted; unmarked days
render as a plain calendar cell. The month stats card below the calendar shows
`totalPresent`/`totalAbsences`/`attendancePercentage` only — `totalOccurrences`/
`markedOccurrences`/`sessionId`/`sessionName` are parsed but not displayed here.

`summary` endpoint keys (`teacherStudentId`, `studentName`, `studentCode`,
`totalOccurrences`, `totalAbsences`, `attendancePercentage`,
`consecutiveAbsences`, `assignmentPeriods[]` with `studentSessionAssignmentId`/
`sessionId`/`sessionName`/`assignedAt`/`unassignedAt`/`isActive`) are all parsed
into state but, as above, nothing on screen currently reads `state.summary`.

No special envelope `code` handling — any failure shows a plain error toast and
the calendar falls back to its empty state.

---

## Payment tracking
_Dart file: `lib/feature/student_module/payment/view/student_payment_tracking_view.dart`_
**Reached from:** the Payment card/tile on **Teacher home** / **Link tab**
(`AppRoute.goToPayment`). The same screen class also serves a parent viewer
(`isParentViewer: true`, `childId` set) calling a different endpoint
(`api/payment/parent/children/{childId}/teachers/{teacherId}/tracking` via
`webPathParentPaymentTracking`) — that path belongs to the parent module, out of
scope here.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen open / pull-to-refresh | `GET api/payment/student/teachers/{teacherId}/tracking` | — (no query params) | see keys below |

```json
{
  "teacherId": 42,
  "currentSessionId": 228,
  "currentSessionName": "Grade 10 - Sunday",
  "paidProgressRatio": 0.65,
  "upcomingPayment": {
    "periodId": 9001,
    "periodStartDate": "2026-10-01",
    "sessionName": "Grade 10 - Sunday",
    "periodType": "Monthly",
    "status": "Unpaid",
    "amountDue": 300.0,
    "amountPaid": 0.0,
    "outstandingAmount": 300.0,
    "isProRated": false,
    "isCarriedForward": false
  },
  "paidSection": {
    "totalAmount": 1200.0,
    "periodCount": 4,
    "periods": [
      {
        "periodId": 8991,
        "periodStartDate": "2026-09-01",
        "sessionName": "Grade 10 - Sunday",
        "periodType": "Monthly",
        "status": "Paid",
        "amountDue": 300.0,
        "amountPaid": 300.0,
        "outstandingAmount": 0.0,
        "paidOnDate": "2026-09-03T00:00:00",
        "isProRated": false,
        "isCarriedForward": false
      }
    ]
  },
  "overdueSection": {
    "totalAmount": 300.0,
    "periodCount": 1,
    "periods": [
      {
        "periodId": 8980,
        "periodStartDate": "2026-08-01",
        "sessionName": "Grade 10 - Sunday",
        "periodType": "Monthly",
        "status": "Unpaid",
        "amountDue": 300.0,
        "amountPaid": 0.0,
        "outstandingAmount": 300.0,
        "monthsOverdue": 1,
        "isProRated": false,
        "isCarriedForward": false
      }
    ]
  }
}
```

`periods[].status` values the app switches on: `Unpaid`, `PartiallyPaid`,
`Paid`, `Overpaid`, else → `unknown` — but the tracking screen itself doesn't
branch per-period status; it just renders whichever section (`paidSection` /
`overdueSection`) the period arrived in, with `amountPaid` (paid rows) or
`outstandingAmount` (overdue rows). `periodType` (`Monthly`/`PerSession`) only
changes the date format (`MMM yyyy` vs `dd MMM yyyy`) and the overdue-count
noun ("months" vs "sessions"). `isProRated`/`isCarriedForward` are parsed by
the DTO but **not read anywhere on this screen** (they back badges on the
teacher-side payment screens instead). `monthsOverdue: 0`/`null` renders as
"current month"; `paidOnDate` is the teacher's local wall-clock collection time,
shown as-is (never re-localized).

No special envelope `code` handling — any failure shows a plain error toast;
`isParentViewer` with a `null childId` shows a distinct "child payment details
not available yet" empty state without calling either endpoint.

---

### Endpoint coverage

- `GET api/studentuser/me/dashboard` — My Teachers (isFirstLogin), My Student Code (studentAccountCode)
- `GET api/studentuser/me/teachers` — My Teachers list, Link tab's teacher resolution
- `POST api/studentuser/me/link-requests` — Add a teacher, Manage teacher → Send request again
- `DELETE api/studentuser/me/teachers/{teacherId}` — Manage teacher → Unlink / Cancel request
- `DELETE api/studentuser/me/teachers/{teacherId}/delete` — Manage teacher → Delete from my list
- `GET api/studentuser/me/teachers/{teacherId}/home` — Teacher home, Link tab
- `GET api/studentuser/me/teachers/{teacherId}/barcode` — Student barcode / QR code
- `POST api/studentuser/me/teachers/{teacherId}/register-device` — Teacher home device-lock consent
- `GET api/attendance/student/teachers/{teacherId}/month` — Attendance month calendar
- `GET api/attendance/student/teachers/{teacherId}/summary` — fetched alongside the month calendar, not currently rendered
- `GET api/payment/student/teachers/{teacherId}/tracking` — Payment tracking

**Defined in `web_constant.dart` but never called from this chapter's live code:**
- `webPathStudentUserMe` (bare `api/studentuser/me`) — no caller anywhere in the app
- `webPathStudentUserMeProfile` (`api/studentuser/me/profile`) — used, but only as a
  `PUT {languagePreference}` from the shared `core/account` language-settings screen
  (outside these folders); there is no GET of it anywhere, so the home header's
  displayed name is never actually sourced from the backend
