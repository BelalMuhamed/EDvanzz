# 02 — Teacher: Students

Source: `lib/feature/teacher_module/add_student/**`, `lib/feature/teacher_module/students/**`,
`lib/feature/teacher_module/student_profile/**`, `lib/feature/teacher_module/recycle_bin/**`,
`lib/feature/teacher_module/export/**`. Endpoint constants: `lib/core/network_services/web_constant.dart`
(`webPathTeacherStudent*`, lines ~59–96). Data flow: `TeacherStudentRemoteDataSource` (Dio) →
`TeacherStudentRepositoryImpl` → cubits → views. All responses are read through the standard envelope
`{success, code, message, data}`; list endpoints return the standard paginated shape (`data`/`page`/
`pageSize`/`totalCount`/`totalPages`) unless noted.

The "Students" tab (`TeacherAddStudentView`, folder `add_student/`) is the actual student **list**
screen — despite its folder name it is not itself a form; it hosts the roster, search, filter, bulk
select/export/delete, and the FAB that opens the Add-Student menu (Single Add / Bulk Add). The
"Assign new students" screen lives physically under `sessions/` (it is opened from a Session's
overview) but is documented here because its counts come from this chapter's `assignment-chips`
endpoint and its list/search reuse the plain student list endpoint.

---

## Students (list / roster)
_Dart file: `lib/feature/teacher_module/add_student/view/teacher_add_student_view.dart`_
_Cubit: `TeacherAddStudentCubit`_
**Reached from:** bottom nav / side menu → "Students" tab.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load / pull-to-refresh / search (350 ms debounce) / filter apply / infinite scroll | `GET api/teacherstudent/students` | Query: `page`, `pageSize` (10 normally, 100 for "select all"/export bulk fetches), `search` (trimmed, omitted if empty), `sessionId` (int, from the session filter), `missingStudentPhone`, `missingParentPhone`, `missingSession` (bools, filter-sheet toggles), `sortBy` (`"DateAdded"` \| `"StudentName"` \| `"StudentCode"`), `sortDirection` (`"Asc"` \| `"Desc"`) | Standard paginated envelope; each item (`TeacherStudentApiDto`): `id`, `teacherId`, `studentName`, `studentCode`, `studentPhoneNumber`, `parentPhoneNumber`, `sessionId`, `sessionName` (list rows do **not** read `barcode`/`isComplete` — those two keys are only parsed on the single-student `fromJson`, not the list-row `fromJsonList`). `totalCount` on the envelope is used directly as both the header count and the recycle-bin-adjacent "active students" count — **no separate counts call backs the main list** |
| FAB "+" → menu → **Single Add** | *(navigation only — see Single Add screen)* | — | — |
| FAB "+" → menu → **Bulk Add** | *(navigation only — see Bulk Import screen)* | — | — |
| Filter icon → bottom sheet → Apply | *(no call itself — reshapes the list query above)* | — | Session dropdown options come from `TeacherSessionRepository.fetchAllSessions` (Sessions chapter, `activeOnly:true, pageSize:10`), not from this family |
| Tap a row (avatar/name) | *(navigation only)* | — | opens **Student Profile** below, passing the row's `studentId` |
| Long-press a row, or tap the "Select" toggle | *(no call — enters selection mode, local state)* | — | — |
| Selection bar → "Select all" | `GET api/teacherstudent/students` (repeated with `pageSize:100` until every page of the current search/filter is fetched) | Same query params as above, page 1..N, `pageSize:100` | Hydrates the full matching id set so "N selected" always matches what a bulk action will actually touch, even past the 10 loaded rows |
| Selection bar → Export (or per-row "Export QR code" quick action, single student) | *(navigation only)* | — | Pre-fetches every matching student via the same `pageSize:100` loop, builds export rows client-side, then pushes **Export → Format** below with `studentIds` |
| Selection bar → Delete | `DELETE api/teacherstudent/students/{studentId}` (exactly 1 selected) **or** `POST api/teacherstudent/bulk-delete` (2+ selected) | Bulk body: `{"studentIds": [5311, 5312]}` | Single delete: boolean envelope. Bulk: envelope `data` = int (count soft-deleted). Both are optimistic on the client (rows removed from the list immediately, rolled back on failure) |

Non-trivial request — bulk delete:
```json
{ "studentIds": [5311, 5312, 5340] }
```

Error handling: no endpoint on this screen branches on a specific business `code`; every failure
(404/409/500/validation) surfaces as a toast built from the envelope's `message` (the shared
`ServerFailure` mapper — see `lib/core/network_services/api_service_failure.dart`). A row deleted by
someone else in the meantime simply reads back as a generic error toast on the failed call, and the
optimistic removal is rolled back.

`GET api/teacherstudent/students/overview` (`fetchStudentsOverview` — a combined counts + first page +
session list response) is fully wired through the repository/data source but has **no caller anywhere
in the app** — dead code path, defined but unused.

---

## Add Student (menu)
_Dart file: `lib/feature/teacher_module/add_student/view/widgets/add_student_menu_list.dart`_
**Reached from:** Students list → FAB "+".

No API. A bottom sheet with two rows, "Single Add" and "Bulk Add", returning an
`AddStudentMenuAction` that the caller routes on.

---

## Add Student — Single Add
_Dart file: `lib/feature/teacher_module/add_student/view/add_student_single_add_view.dart`_
**Reached from:** Add Student menu → "Single Add"; also opened directly from the "Assign new students"
screen's own add-shortcut (pre-filling the session).

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen open | `GET api/Teacher/{teacherId}/configuration` (Settings chapter) | — | `studentCodeGenerationMode` — gates whether the Student Code field is shown (Manual mode only; Auto mode never renders or sends the field) |
| Screen open | *(Sessions chapter)* `GET api/session/{teacherId}/sessions?activeOnly=true&pageSize=10` | — | Populates the session dropdown |
| "Add Student" submit | `POST api/teacherstudent` | `{"studentName": "...", "studentPhoneNumber"?: "...", "parentPhoneNumber"?: "...", "studentCode"?: "...", "sessionId"?: 228}` — optional keys omitted entirely when empty/null; `studentCode` is only ever included when the teacher's mode is Manual | Envelope `data` = `TeacherStudentApiDto` (see fields above); on success the list is force-reloaded (`preserveLoadedPages`, so scrolled pages aren't lost) and the app navigates to the Success screen |

Non-trivial request body:
```json
{
  "studentName": "Ahmed Hassan",
  "studentPhoneNumber": "01012345678",
  "parentPhoneNumber": "01098765432",
  "studentCode": "A5",
  "sessionId": 228
}
```

## Add Student — Success
_Dart file: `lib/feature/teacher_module/add_student/view/teacher_add_student_success_view.dart`_

No API — a static success screen ("Student added successfully") with a single "Continue" button that
pops back to the list.

---

## Add Student — Bulk Import (Excel/CSV)
_Dart file: `lib/feature/teacher_module/add_student/view/add_student_bulk_add_view.dart`_
_Cubit: `TeacherBulkImportCubit`_
**Reached from:** Add Student menu → "Bulk Add".

The template download and the local spreadsheet parse are **entirely client-side** (no API):
`buildBulkImportTemplateXlsx()` generates an `.xlsx` with header row `Student Name | Student Phone
Number | Parent Phone Number | Student Code | Session Name` and is shared/saved via the OS share
sheet (iOS) or public Downloads (Android); the picked file (`.csv`/`.xlsx`/`.xls`) is parsed off the UI
isolate. Each parsed row's `Session Name` text is resolved to a `sessionId` **locally**, by an
exact (trimmed, case-insensitive) match against the teacher's own session list (fetched via the
Sessions-chapter `fetchAllSessions`); a row whose text doesn't match any session is imported with no
`sessionId` at all — the raw session name is never sent to the server.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen open | `GET api/teacherstudent/counts` | Query: none (`missingStudentPhone`/`missingParentPhone`/`missingSession` all default `false`) | `totalActiveStudents` (falls back to `noOfStudentsForTenant` then `totalCount` if the primary key is absent) → `remainingCapacity = teacher.studentCapacity − totalActiveStudents`, shown as a hint and enforced **client-side** before the import even starts (a file with more rows than remaining capacity is rejected locally with a toast, never sent) |
| Pick file → parse succeeds → import starts | `POST api/teacherstudent/bulk-import/stream` — **NDJSON streaming response**, `Accept: application/x-ndjson`, `responseType: stream`. Request timeouts are overridden for this call (2 min send, 2 min receive — reset by every progress line) | `{"students": [{"studentName": "...", "studentPhoneNumber": "...", "parentPhoneNumber": "...", "studentCode": "...", "sessionId"?: 228}, ...]}` (`sessionId` present only for rows whose sheet text matched a session) | See NDJSON line format below |
| Cancel / leave mid-import | *(no explicit cancel endpoint)* | The Dio `CancelToken` is cancelled, which drops the HTTP connection | Server-side, a dropped connection **rolls the whole import back** — nothing is persisted on cancellation |
| Import finishes successfully | *(list is refreshed)* | — | Triggers `GET api/teacherstudent/students` on the list cubit; if any sheet rows had an unmatched session name, an info toast names them |

**NDJSON stream line format** (one JSON object per line, `\n`-delimited; the client buffers partial
lines across chunk boundaries and stops reading at the first non-`progress` line):
- Heartbeat, zero or more: `{"type": "progress", "processed": 42, "total": 500}`
- Terminal success: `{"type": "result", "success": true, "data": { ...BulkImportResult, see below... }}`
- Terminal business failure (e.g. capacity exceeded mid-stream): `{"type": "result", "success": false, "message": "..."}`
- Terminal transport/error line: `{"type": "error", "message": "..."}`

`BulkImportResult` (the `data` object of the terminal success line):
```json
{
  "totalProcessed": 500,
  "successCount": 486,
  "failedCount": 14,
  "succeeded": [
    { "rowNumber": 2, "studentId": 5311, "studentName": "Ahmed Hassan", "studentCode": "A5", "sessionId": 228, "sessionName": "Grade 10 - Sat/Tue" }
  ],
  "failures": [
    { "rowNumber": 7, "studentName": "Sara Adel", "studentCode": "A5", "reason": "Student code already exists" }
  ]
}
```

A **non-streaming** `POST api/teacherstudent/bulk-import` (`bulkImport()`, identical request body, a
plain envelope response instead of NDJSON) is fully implemented in the repository/data source but has
**no caller anywhere in the app** — the bulk-add screen always uses the streaming variant.

---

## Bulk Import — Result Detail (Imported / Failed lists)
_Dart files: `lib/feature/teacher_module/add_student/view/widgets/add_student_bulk_add_record_section.dart`, `lib/feature/teacher_module/add_student/view/teacher_bulk_import_result_detail_view.dart`, `.../teacher_bulk_import_failures_bottom_sheet.dart`_

No API of its own. Renders "Total processed / Total imported / Total failed" counters and, on tap,
a detail list — all read straight out of the `BulkImportResult` already returned by the stream above
(`succeeded[]` rows show `studentName`/`studentCode`/`sessionName`; `failures[]` rows show
`rowNumber`/`studentName`/`studentCode`/`reason`). Nothing here re-fetches from the server.

---

## Filter Students (bottom sheet)
_Dart file: `lib/feature/teacher_module/view/widgets/teacher_students_filter_bottom_sheet.dart`_
**Reached from:** Students list → filter icon.

No API of its own — "Apply" just returns a `TeacherStudentsFilterCriteria` that the list cubit turns
into the `GET api/teacherstudent/students` query params documented in the **Students (list)** table
above: session dropdown → `sessionId`; "Missing student phone" / "Missing parent phone" / "Unassigned
session" toggles → `missingStudentPhone` / `missingParentPhone` / `missingSession`; the separate Sort
sheet → `sortBy`/`sortDirection`.

---

## Assign New Students (session context)
_Dart file: `lib/feature/teacher_module/sessions/view/teacher_assigned_students_view.dart`_
_Cubit: `TeacherAssignedStudentsCubit`_
**Reached from:** a Session's overview screen → "Assign students" (Sessions chapter).

This screen exists to pick which of the teacher's students belong to one session. It reuses the plain
student-list endpoint for its rows (filtered by session membership) and this chapter's
`assignment-chips` endpoint for its three chip counts; the actual assign/unassign/reassign writes are
Sessions-chapter endpoints, named here but not detailed.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load / search (350 ms debounce) / chip switch / infinite scroll | `GET api/teacherstudent/students` | Query: `page`, `pageSize:10`, `search`, and depending on the active chip — **All**: no extra filter; **In session**: `sessionId=<this session>`; **Unassigned**: `missingSession=true` | Same `TeacherStudentApiDto` rows as the main list; `totalCount` seeds that chip's own count on a fresh (non-append) load |
| Chip strip refresh (after every load, and again after every assign/unassign) | `GET api/teacherstudent/assignment-chips` | Query: `search` (trimmed, omitted if empty) — **no session id is sent**; the response already carries every session's count | `data.totalCount` → "All (N)"; `data.unassignedCount` → "Unassigned (N)"; `data.sessions[]` (`{sessionId, sessionName, assignedCount}`) matched by this screen's own `sessionId` → "In session (N)". Replaces what used to be three separate `counts` calls |
| "Select all" (within the active chip + search scope) | `GET api/teacherstudent/students` (repeated, `pageSize:100`, same filter as the active chip, until the full scope is hydrated) | Same query shape as the row above | Full matching id set, so "Select all" never silently short-changes a multi-page result |
| Bottom bar "Assign" (selected unassigned students) | *(Sessions chapter)* `POST api/session/assign-students`, chunked at 50 students per call | `{teacherId, sessionId, studentIds:[...]}` per chunk | A student already active in a **different** session comes back as a reassignment warning (`studentId`, `studentName`, `currentSessionName`, `newSessionName`) rather than a hard failure — the app then confirms and calls `confirm-reassign` |
| Reassignment confirm dialog → "Submit" | *(Sessions chapter)* `POST api/session/{teacherId}/sessions/{sessionId}/confirm-reassign`, chunked at 50 | `{studentIds:[...]}` | Moves the student(s) from their old session into this one |
| Bottom bar "Remove from session" (selected assigned students) | *(Sessions chapter)* `DELETE api/session/{teacherId}/sessions/{sessionId}/students/{studentId}`, one call per student | — | — |
| FAB "+" (add-student shortcut, session pre-filled) | *(navigation only — Single Add / Bulk Add, see above)* | — | — |

Non-trivial request — assign-students:
```json
{ "teacherId": 42, "sessionId": 228, "studentIds": [5311, 5312] }
```

Error handling: assign/unassign/reassign failures surface as a plain error toast from the envelope
message; there is no special business `code` branching on this screen.

---

## Student Profile
_Dart files: `lib/feature/teacher_module/student_profile/view/teacher_student_profile_screen.dart` (shell), `teacher_student_profile_view.dart` (content)_
_Cubit: `TeacherStudentProfileCubit`_
**Reached from:** Students list → tap a row; "Assign new students" list has no profile navigation.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load / pull-to-refresh | `GET api/teacherstudent/students/{studentId}` | — | Envelope `data` is a **nested** object: `data.student` (`TeacherStudentApiDto` — falls back to reading the top-level object directly if `student` is absent, for backward compat), `data.session` **or** `data.assignedSession` (a Sessions-chapter session-summary object — either key is accepted), `data.sessions[]` (`{id, sessionName/name, studentCount}` — **parsed but never read by the UI**, dead field). Mapped into: name, student/parent phone (blank shows an "Add number" chip instead of the number), assigned session title + schedule label (from the session summary), barcode label (`barcode ?? studentCode`). `profileImageUrl` is always empty on this screen — there is no student-photo field/feature yet, so the avatar always falls back to its placeholder |
| Avatar edit-pen badge | *(no call)* | — | Opens **Edit Student**; a bare avatar tap shows a "Profile photo upload is not available yet" toast |
| "Parent is following" chip (only rendered when at least one parent follows) | *(Parent Portal chapter)* `GET api/teacher/parent-portal/students/{teacherStudentId}/followers` | — | Chip is silent (renders nothing) on any failure/older backend — never an error state |
| Contact row → phone icon | *(no call — opens a call/WhatsApp bottom sheet)* | — | — |
| Contact row → "Add number" (empty phone) | *(navigation only)* | — | Opens **Edit Student** |
| "Student Barcode" card → tap | *(navigation only)* | — | Opens **Student Barcode** screen below |
| "Student Barcode" card → Print / Share / Download icons | `POST api/teacherstudent/students/barcodes/export` | `{"studentIds": [<this student>]}` | Binary PDF; Print and Share both hand the bytes to the OS share sheet, Download saves it to device storage — **all three buttons hit the same endpoint** |
| "Activity" → Attendance history | *(navigation only, disabled with an explanatory empty state when the student has no session)* | — | Opens **Attendance History** below |
| "Activity" → Payment history | *(navigation only, same session-required gate)* | — | Opens **Payment History** below |
| Menu (⋮, on the Edit screen) → Delete | see **Edit Student** below | — | — |

Response shape (profile detail):
```json
{
  "success": true,
  "code": "Success",
  "message": "",
  "data": {
    "student": {
      "id": 5311,
      "teacherId": 42,
      "studentName": "Ahmed Hassan",
      "studentCode": "A5",
      "studentPhoneNumber": "01012345678",
      "parentPhoneNumber": "01098765432",
      "barcode": "A5",
      "sessionId": 228,
      "sessionName": "Grade 10 - Sat/Tue",
      "isComplete": true
    },
    "session": { "id": 228, "sessionName": "Grade 10 - Sat/Tue", "...": "session-summary fields, Sessions chapter" },
    "sessions": []
  }
}
```

The same `GET api/teacherstudent/students/{studentId}` also backs a second, unused parse path:
`getStudentById()` reads the payload as a **flat** `TeacherStudentApiDto` (i.e. it expects `id`/
`studentName`/… at the top level, not nested under `student`) — since the real response nests the
student under `student`, this method would read back mostly-default values. It has no caller; only
`getStudentProfileDetail()` (the nested parse above) is ever used.

---

## Edit Student
_Dart file: `lib/feature/teacher_module/student_profile/view/teacher_edit_student_view.dart`_
**Reached from:** Student Profile → avatar edit-pen badge, or "Add number" on an empty phone row.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen open | *(Sessions chapter)* `GET api/session/{teacherId}/sessions?activeOnly=true&pageSize=10` | — | Session dropdown; the student's own (possibly inactive) session is force-included at the top of the list if the active-only fetch dropped it |
| "Update Profile" submit | `PUT api/teacherstudent/students/{studentId}` | `{"studentName": "...", "studentPhoneNumber": "...", "parentPhoneNumber": "...", "sessionId": 228}` — note **both phone keys are always sent** (empty string when cleared, unlike create); `sessionId` is sent as `null` when the dropdown is cleared; `studentCode` is **never sent** from this screen (the field is read-only/locked here — editing the code is a support-only action, `student_code_locked` = "Locked - contact support to change") | Envelope `data` = updated `TeacherStudentApiDto`; on success the profile is force-reloaded and the caller (Students list) patches its row in place |
| Menu (⋮) → Delete → confirm | `DELETE api/teacherstudent/students/{studentId}` | — | Boolean envelope; on success the student moves to the Recycle Bin and the screen pops back with a "deleted" result so the caller removes the row locally |

Non-trivial request body (session cleared):
```json
{
  "studentName": "Ahmed Hassan",
  "studentPhoneNumber": "01012345678",
  "parentPhoneNumber": "",
  "sessionId": null
}
```

---

## Student Barcode
_Dart file: `lib/feature/teacher_module/student_profile/view/teacher_student_barcode_view.dart`_
**Reached from:** Student Profile → "Student Barcode" card.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen open / barcode card render (also used inline on the Profile card) | `GET api/teacherstudent/students/{studentId}/barcode.svg` | — | Raw SVG bytes (`responseType: bytes`), rendered with `SvgPicture.memory`; an empty/failed response falls back to a static QR placeholder illustration, not an error toast |
| Print / Share / Download | `POST api/teacherstudent/students/barcodes/export` | `{"studentIds": [<this student>]}` | Same PDF as the Profile screen's barcode actions |

The barcode SVG fetch is defensive against a misconfigured server: if the response's content-type is
`application/json` or the byte stream starts with `{` (0x7B), the client re-decodes it as a normal
error envelope instead of trying to render it as an image.

---

## Attendance History
_Dart file: `lib/feature/teacher_module/student_profile/view/teacher_student_attendance_history_view.dart`_
**Reached from:** Student Profile → Activity → "Attendance history".

Uses the Attendance chapter's endpoints — named here only:
- `GET api/Attendance/timeline/students/{studentId}` — sessions-attended / sessions-missed totals.
- `GET api/Attendance/timeline/students/{studentId}/month` (current month) — the "Missed sessions"
  list rows.

If the student has no session assigned, the screen shows an empty state ("Student is not assigned")
and makes **no call at all**. Detailed in the Attendance chapter.

## Payment History
_Dart file: `lib/feature/teacher_module/student_profile/view/teacher_student_payment_history_view.dart`_
**Reached from:** Student Profile → Activity → "Payment history".

Uses the Payments chapter's endpoints — named here only:
- `GET api/Payment/students/{teacherStudentId}/payment-view` — session name, amount paid, outstanding,
  overdue count (header stats).
- `GET api/Payment/students/{teacherStudentId}/history` — the paginated transaction list, loaded page
  1 up front and paged further on scroll.

Same session-required empty state as Attendance History. Detailed in the Payments chapter.

---

## Recycle Bin — Students
_Dart file: `lib/feature/teacher_module/recycle_bin/view/teacher_recycle_bin_students_view.dart`_
_Cubit: `TeacherRecycleBinCubit`_
**Reached from:** side menu → "Recycle bin" (students and sessions are two separate side-menu entries;
the sessions recycle bin is a different, unrelated screen/module and is not covered here).

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load / pull-to-refresh / infinite scroll | `GET api/teacherstudent/recycle-bin` | Query: `page`, `pageSize` (10 normally, 100 for "select all") | Standard paginated envelope; each item (`RecycleBinStudentApiDto`): `id`, `studentName`, `studentCode`, `studentPhoneNumber`, `parentPhoneNumber`, `deletedAt` (UTC instant), `daysRemaining` (int — drives the "permanently deleted in N days" row label, cap 10 days) |
| Selection bar → "Select all" | `GET api/teacherstudent/recycle-bin` (repeated, `pageSize:100`, until every page is fetched) | Same query params | Full id set for the current (unfiltered — this screen has no search) scope |
| Row → "Restore" (per row only — there is no bulk-restore control in this screen's UI) | `POST api/teacherstudent/recycle-bin/{studentId}/restore` | — | Envelope `data` = restored `TeacherStudentApiDto`; the student reappears in the main list **without a session** (restore never restores the old session assignment — the success toast says so explicitly) |
| Bottom bar trash icon (bulk, selected rows) → confirm | `DELETE api/teacherstudent/recycle-bin/{studentId}/permanent` — **one call per selected id, sequential, not a batch request** | — | Boolean envelope per call; a failure partway through the loop keeps the ids already-deleted removed from the UI (they are gone server-side) and rolls back only the remaining, not-yet-processed ones |

`POST api/teacherstudent/bulk-restore` (`bulkRestore()` — body `{"studentIds": [...]}`, response `data`
= int count restored) is fully wired through the cubit (`bulkRestoreStudents()`) but **no button in
this screen calls it** — the UI only offers Restore per row; bulk selection here only exposes
Permanent delete. There is likewise **no bulk-permanent-delete endpoint at all** — a multi-select
purge is N sequential `DELETE .../permanent` calls.

---

## Export — Choose Format
_Dart file: `lib/feature/teacher_module/export/view/teacher_export_format_screen.dart`_
**Reached from:** Students list → Select mode → Export (or the per-row "Export QR code" shortcut).

No API. Lets the teacher pick Excel or PDF (students default to PDF — "the scannable barcode sheet");
"Generate" pushes the Export → Generating screen below with the already-collected student rows/ids.

## Export — Generating
_Dart file: `lib/feature/teacher_module/export/view/teacher_export_loading_screen.dart`_

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Format = Excel | *(no call)* | — | Builds an `.xlsx` **entirely client-side** from the rows already fetched by the Students list (`Student Name`, `Student Code`, `Phone Number`, `Parent Phone`, `Session Name` — the barcode column is dropped; the student code IS the barcode value) |
| Format = PDF | `POST api/teacherstudent/students/barcodes/export` | `{"studentIds": [5311, 5312, ...]}` | Binary PDF (`responseType: bytes`); filename taken from the response's `Content-Disposition` header, falling back to `student-barcodes.pdf`. Despite the endpoint's name ("barcodes/export"), it always returns a **PDF**, never an Excel file — the Excel variant is generated locally and never calls this endpoint |

The same content-type/`{`-byte defensive check as the barcode SVG fetch applies here: a JSON error
body returned with a 200 is still caught and surfaced as a normal error message rather than silently
producing a corrupt PDF/Excel file.

## Export — Success
_Dart file: `lib/feature/teacher_module/export/view/teacher_export_success_screen.dart`_

No API — shares or saves the bytes already produced by the Generating step (`application/pdf` or
`.xlsx` MIME type depending on format chosen).

---

### Endpoint coverage

**Used:**
- `POST api/teacherstudent` — create student
- `GET api/teacherstudent/students` — list (roster, filters, "Assign new students" screen, select-all/export bulk fetches)
- `GET api/teacherstudent/students/{studentId}` — profile detail (nested `student`/`session`/`sessions` shape)
- `PUT api/teacherstudent/students/{studentId}` — update student
- `DELETE api/teacherstudent/students/{studentId}` — soft-delete (single)
- `POST api/teacherstudent/bulk-delete` — soft-delete (2+ selected)
- `GET api/teacherstudent/counts` — bulk-import capacity check only
- `GET api/teacherstudent/assignment-chips` — "Assign new students" chip strip
- `POST api/teacherstudent/bulk-import/stream` — NDJSON streaming bulk import
- `GET api/teacherstudent/recycle-bin` — recycle bin list
- `POST api/teacherstudent/recycle-bin/{studentId}/restore` — restore (per row)
- `DELETE api/teacherstudent/recycle-bin/{studentId}/permanent` — permanent delete (per row; looped for bulk)
- `GET api/teacherstudent/students/{studentId}/barcode.svg` — inline barcode SVG
- `POST api/teacherstudent/students/barcodes/export` — barcode PDF (single or multi student; despite the name, always PDF)

**Defined in `web_constant.dart` but never called anywhere in the app (dead client-side wiring):**
- `GET api/teacherstudent/students/overview` — `fetchStudentsOverview()` fully implemented end to end (combined counts + first page + session list); no screen calls it
- `POST api/teacherstudent/bulk-import` — non-streaming bulk import (`bulkImport()`); the bulk-add screen exclusively uses the streaming variant above
- `POST api/teacherstudent/bulk-restore` — `bulkRestoreStudents()` wired through the recycle-bin cubit; the recycle bin screen offers Restore only per row, never as a bulk action

Cross-module endpoints named in this chapter but detailed elsewhere: `GET/POST api/session/...`
(assign/reassign/unassign — Sessions chapter), `GET api/Teacher/{teacherId}/configuration` (Settings
chapter), `GET api/teacher/parent-portal/students/{teacherStudentId}/followers` (Parent Portal
chapter), `GET api/Attendance/timeline/students/{studentId}` and `.../month` (Attendance chapter),
`GET api/Payment/students/{teacherStudentId}/payment-view` and `.../history` (Payments chapter).
