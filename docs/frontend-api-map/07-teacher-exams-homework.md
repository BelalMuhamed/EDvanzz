# 07 — Teacher: Exams & Homework

Source: `lib/feature/teacher_module/exams/**` (~84 files) covers BOTH **offline/paper exams**
(`api/exams/*`) and **online exams** (`api/online-exams/*`); `lib/feature/teacher_module/homework/**`
(~12 files) is a separate feature folder that visually clones the online-exam screens but is **not wired
to any backend** — see the dedicated section at the end. Endpoint constants:
`lib/core/network_services/web_constant.dart` — `webPathExams*` (lines ~170–181),
`webPathOnlineExam*` (~184–216), `webPathTeacherSubjects` (~185), `webPathUpload` (~505).

Data flow (offline exams): `TeacherExamRemoteDataSource` (Dio) → `TeacherExamRepositoryImpl` → cubits →
views, all through the standard envelope `{success, code, message, data}`.
Data flow (online exams): `TeacherOnlineExamRemoteDataSource` → `TeacherOnlineExamRepositoryImpl` →
cubits → views. Responses on this side are parsed **defensively** (comment in the DTO file: "response
schemas undocumented") — most fields are read via 2–3 alternate key names (`json['x'] ?? json['y']`).
Only the request shapes below (built by the app itself) are exact; response field names marked "or" are
the app's own defensive fallbacks, not confirmed alternates from the server.

Two concurrency-token conventions coexist in this chapter:
- **Offline exams** — a per-row **`rowVersion`** (opaque string) on every `ExamStudentRowApiDto`. Grade
  and attendance batch saves echo the token back per item so a retry never reuses a stale one.
- **Online exams** — a single **`rowVersion`** on the exam itself (`List<int>` on the wire, but the app
  round-trips it as `String.fromCharCodes(...)` when sending it back — treat it as an opaque blob, never
  parse it). Every status PATCH returns a **fresh** `rowVersion`; the app always chains the latest one
  into the next status call.

Dates: the offline-exam `examDate` (SeparateTime) and `GET session-dates` `date` are calendar dates,
sent/received as `"yyyy-MM-dd"` with no time component. Online-exam `startDateTime`/`endDateTime` are
full UTC instants (`toUtc().toIso8601String()`, i.e. `...Z`).

---

## Offline Exams Home
_Dart file: `lib/feature/teacher_module/exams/view/teacher_exam_home_view.dart`_
_Cubit: `TeacherExamHomeCubit`_
**Reached from:** teacher side-menu drawer → "Activity" (expandable) → "Offline Exams".

Two independent paginated lists (Upcoming / Past) on one screen; each has its own page cursor and
"load more" on near-bottom scroll. There is no status/date filter sheet on this screen (a
`TeacherOfflineExamFilterSheet` widget exists in the codebase but is never invoked from anywhere — dead
UI). Cards are the backend's one-row-per-exam-per-session rows, **grouped client-side** into one card
per exam (`groupExamHomeCards`) — stats are summed across the exam's session rows.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load / pull-to-refresh | `GET api/exams/home` | Query `upcomingPage=1`, `pastPage=1`, `pageSize=10` (clamped 1–10) | `upcoming`/`past`, each `{data:[...], page, pageSize, totalCount, totalPages}` (older bare-array shape also tolerated). Row: `examId`, `occurrenceId`, `name`, `deliveryType` (`"DuringSession"`\|`"SeparateTime"`), `sessionId`, `sessionName`, `date`, `assignedCount`, `totalStudents`, `attendedCount`, `missedCount`, `pendingCount`, `isPast`, `assignedSessions[]` (length only, used as the exam's total session count) |
| Scroll near bottom | same, next page | `upcomingPage`/`pastPage` advanced independently | appends to the relevant list |
| FAB "+" | *(navigation only)* | — | opens **Create / Edit Offline Exam** below |
| Card tap | *(navigation only)* | — | opens **Offline Exam Detail** below |
| Card ⋮ → Edit | *(navigation only)* | — | opens **Create / Edit Offline Exam** in edit mode |
| Card ⋮ → Delete | `DELETE api/exams/{examId}` | Query `confirm=true` (comment: "DeletionConfirmationRequired unless confirm=true" — the app always sends it, so the confirm-required variant of the error is never actually surfaced) | 200 → row removed (optimistic — removed from state before the call, restored on failure) + success toast |

## Create / Edit Offline Exam
_Dart file: `lib/feature/teacher_module/exams/view/teacher_offline_exam_create_view.dart`_
_Cubit: `TeacherOfflineExamCreateCubit`_
**Reached from:** Offline Exams Home → FAB "+" (create) or card ⋮ → Edit.

Recipients are **sessions XOR groups** (radio-like: picking one clears the other, with a confirm dialog
if the opposite already has a selection) via the shared **Target Scope Picker** (below). Delivery type
is **DuringSession** (per-session/-group-member a specific already-scheduled class occurrence must be
picked) or **SeparateTime** (one exam date for the whole recipient set). Editing an exam pre-loads via
`GET api/exams/{examId}` and always resolves `recipientType: sessions` (an edited exam's original
groups-vs-sessions distinction is not reconstructed — `sessionIds` sent on save are simply every session
in the loaded detail).

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Groups / Sessions tile | *(navigation only)* | — | opens Target Scope Picker; result list feeds `applyGroupSelection`/`applySessionSelection` |
| Calendar icon on a session/group row (DuringSession) | `GET api/exams/session-dates` (one call per underlying session; for a group, unioned across its member sessions) | Query `sessionId`, `year`, `month` (calendar month currently shown in the date-picker dialog) | `[{sessionOccurrenceId, date, status}]` — only presence of a date in this list makes that calendar day tappable; `status` is not otherwise read by the picker |
| Date-picker "Continue" | *(local state only)* | — | stores `{sessionId → sessionOccurrenceId, sessionId → date}` per session (never fans out to sibling sessions) |
| Exam date field (SeparateTime) | *(local state only)* | — | plain date picker, no API |
| "Create" / "Save" | `POST api/exams` (create) or `PUT api/exams/{examId}` (edit) | see JSON below | `examId` (create) used to navigate to the success screen; edit treats envelope success as sufficient even if `data` is null/partial (PUT may return an incomplete body) |

Create/update request body (`CreateExamRequestApiDto.toJson`) — **SeparateTime + sessions**:
```json
{
  "name": "Midterm - Algebra",
  "notes": "Bring calculators",
  "deliveryType": "SeparateTime",
  "maxGrade": 100,
  "successScore": 50,
  "sessionIds": [228, 229],
  "examDate": "2026-09-20"
}
```
- `notes` omitted entirely when blank.
- `examDate` is date-only (`yyyy-MM-dd`) — **never** sent together with `sessionOccurrences`.
- `sessionIds` XOR `groupIds`: only whichever recipient type is active is included (empty/absent
  otherwise).

**DuringSession + groups** — every resolved MEMBER session of the selected group(s) gets its own picked
occurrence, per CLAUDE.md §7.5 ("during-session dates are per session"):
```json
{
  "name": "Pop quiz - Chapter 3",
  "deliveryType": "DuringSession",
  "maxGrade": 20,
  "successScore": 10,
  "groupIds": [14],
  "sessionOccurrences": [
    { "sessionId": 228, "sessionOccurrenceId": 5231 },
    { "sessionId": 231, "sessionOccurrenceId": 5390 }
  ]
}
```
- `sessionOccurrences[]` entries must cover the resolved session set exactly (one pick per session,
  including group members) — this is enforced client-side before submit (`canSubmit` requires every
  resolved session id to have an occurrence picked).
- A picked occurrence may be in the past (edit mode allows browsing past calendar months).

**Errors:** the app does not special-case any offline-exam create/update error code (no `switch` on
`code` for this endpoint) — any failure just surfaces the server's localized `message` as a toast. A
structural-edit conflict on an exam that already has results (documented backend-side as
`ExamHasResultsCannotRestructure`, CLAUDE.md §7.5) would therefore render as a generic error banner, not
a dedicated message.

## Target Scope Picker (shared)
_Dart file: `lib/feature/teacher_module/exams/view/teacher_exam_target_scope_view.dart`_
_Cubit: `TeacherExamTargetScopeCubit`_
**Reached from:** Create/Edit Offline Exam and Create/Edit Online Exam recipient tiles ("Groups" /
"Sessions"). Also usable in a `students` mode, but neither exam create flow opens it that way (both
call `_openTargetScope` only with `groups`/`sessions`).

This screen has **no exam-specific endpoint of its own** — it reuses the Sessions/Groups/Students list
endpoints documented in the Sessions and Students chapters:

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Groups mode, screen load | Session-groups list endpoint (`TeacherSessionRepository.fetchGroups`) | `teacherId` (resolved from the stored session, never from a route param) | `id`, `groupName`, `sessionCount`, `studentCount` per row |
| Sessions mode, screen load / search (350ms debounce) / infinite scroll | Session list endpoint (`TeacherSessionRepository.fetchSessions`) | `page`, `pageSize=10`, `activeOnly=true`, `search` | `id`, `sessionName`, `studentCount`, `sessionGroupName` per row |
| Students mode (unused by either exam flow today) | Student list endpoint (`TeacherStudentRepository.fetchStudents`) | `page`, `pageSize=10`, `search` | `id`, `studentName`, `studentCode` per row |
| "Continue" | *(local only)* | — | pops the selected `TeacherExamTargetScopeItem[]` back to the caller |

## Offline Exam Detail (session list)
_Dart file: `lib/feature/teacher_module/exams/view/teacher_offline_exam_detail_view.dart`_
_Cubit: `TeacherOfflineExamDetailCubit`_
**Reached from:** Offline Exams Home → card tap.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load / pull-to-refresh | `GET api/exams/{examId}` | — | `examId`, `name`, `deliveryType`, `maxGrade`, `successScore`, `distinctStudentCount`, `globalStats` (`totalStudents`, `gradedCount`, `average`, `highest`, `lowest`, `attendedCount`, `missedCount`, `pendingCount`, `belowPassingCount` — rendered in the exam-level stats grid), `sessions[]` (`sessionId`, `sessionName`, `occurrenceId`, `date`, `stats`, `attendanceTaken`, `gradesTaken`) |
| Search box (client-side filter over the already-loaded session list) | — | — | — |
| Session card tap | *(navigation only, passes the already-loaded session block so the next screen skips a re-fetch)* | — | opens **Session Roster & Actions** below |

## Session Roster & Actions
_Dart file: `lib/feature/teacher_module/exams/view/teacher_offline_exam_session_view.dart`_
_Cubit: `TeacherOfflineExamSessionCubit`_
_Actions sheet: `lib/feature/teacher_module/exams/view/widgets/teacher_offline_exam_session_actions_bottom_sheet.dart`_
**Reached from:** Offline Exam Detail → a session row.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load (no seeded session/meta, or forced refresh) | `GET api/exams/{examId}` | — | resolves this session's block from `sessions[]`, then falls through to the roster call below |
| Roster load / search (350ms debounce) / infinite scroll | `GET api/exams/{examId}/sessions/{sessionId}` | Query `page`, `pageSize=30`, `search` (no `graded` filter on this screen) | `examId`, `sessionId`, `sessionName`, `occurrenceId`, `date`, `maxGrade`, `successScore`, `students` page (`data[]`/`page`/`pageSize`/`totalCount`/`totalPages` — a bare array is also tolerated). Per student: `obligationId`, `teacherStudentId`, `studentName`, `studentCode`, `status` (`Pending`\|`Attended`\|`AttendedWithGrade`\|`DidNotAttend`), `attended`, `grade`, `isGradeEntered`, `isBelowPassing`, `rowVersion` |
| ⋮ menu → "Take attendance" / "Edit attendance" | *(navigation only)* — opens the shared class-attendance screen, `AppRoute.goToTeacherTakeAttendance` | Passes `examId` **only when the exam is SeparateTime** (that screen then writes through `api/exams/attendance*`, see below); for a **DuringSession** exam, `examId` is omitted and `occurrenceDate` is passed instead — attendance for that class day is taken through the **ordinary class-attendance endpoints** (documented in the Attendance chapter), because the exam's own attendance state there is just a reflection of the class register, not a separate write path | screen pop → session refreshed |
| ⋮ menu → "Add grades" / "Edit grades" | *(navigation only)* | — | opens **Grades Entry** below |

## Grades Entry
_Dart file: `lib/feature/teacher_module/exams/view/teacher_offline_exam_grades_view.dart`_
_Cubit: `TeacherOfflineExamGradesCubit`_
**Reached from:** Session Roster ⋮ menu → "Add grades"/"Edit grades".

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load | `GET api/exams/{examId}` then `GET api/exams/{examId}/sessions/{sessionId}` | roster query `page=1`, `pageSize=30`, `graded` per the active chip | same roster shape as above; per-row `rowVersion` is cached and echoed back on save |
| Search box / All-Graded-Not graded chips | re-fetch page 1 | `graded`: chip "All" → omitted, "Graded" → `true`, "Not graded" → `false` (**server-side** filter — the roster pages 30 at a time, so a client-side filter would hide rows past page 1) | — |
| Typing a grade box, "Next" on keyboard | *(local draft only)* | — | walks focus to the next gradable row |
| "Submit (N) students" | `PUT api/exams/grades` | see JSON below | see batch result shape below |
| Failed-rows banner → "Try again" | re-fetches every already-loaded page (to get fresh `rowVersion`s) then re-submits only the still-failed rows | same shape as Submit | same |
| A blocked row's "Fix attendance" link | *(navigation only, same shared attendance screen as above)* | — | `refreshAfterAttendanceChange()` re-pulls the roster afterward |

Request (`BatchGradeRequestApiDto`) — clearing a grade is `grade: null`, only sent after a confirm
dialog naming the affected student(s):
```json
{
  "examId": 501,
  "items": [
    { "teacherStudentId": 3312, "grade": 85, "rowVersion": "AAAAAAAAB9E=" },
    { "teacherStudentId": 3313, "grade": null, "rowVersion": "AAAAAAAAB9F=" }
  ]
}
```
`rowVersion` is copied verbatim from the roster row the app just loaded — never parsed or constructed
client-side.

Response (`BatchGradeResultApiDto`):
```json
{
  "updatedCount": 1,
  "allSucceeded": false,
  "items": [
    { "obligationId": 9001, "teacherStudentId": 3312, "success": true, "status": "AttendedWithGrade", "grade": 85, "rowVersion": "AAAAAAAAB9G=" },
    { "obligationId": 9002, "teacherStudentId": 3313, "success": false, "code": "ObligationConcurrencyConflict", "status": "DidNotAttend", "rowVersion": "AAAAAAAAB9H=" }
  ]
}
```

**`code` values the app special-cases per row** (`_itemFailureMessage`):

| `code` | UI behavior |
|---|---|
| `ObligationConcurrencyConflict` | "This grade was changed elsewhere — reload and try again." The row's `rowVersion`/`status`/`grade` ARE still adopted from this response (the server's authoritative current view), so a bare retry (without a manual re-fetch) can succeed next time. |
| `CannotGradeAbsentStudent` | "This student was marked absent" — the grade box is disabled with this reason; "Fix attendance" link shown. |
| `AttendanceNotRecordedForExam` | "Attendance hasn't been recorded yet" — same disabled-box treatment. |
| `GradeExceedsMax` / `GradeOutOfRange` | Generic "grade exceeds the maximum" message (the app also validates this client-side before sending). |
| `ObligationNotFound` / `AmbiguousStudentInExam` / anything else | Generic "grade could not be saved." |

Only a `success` row or one carrying `ObligationConcurrencyConflict` is treated as reflecting the
server's current state (row `status`/`grade`/`rowVersion` adopted); every other failure code leaves the
row exactly as the teacher had it, so retry resends the same value.

## Take Attendance (SeparateTime exam) + QR Scan
_Cubit: `TeacherExamTakeAttendanceCubit`_ (extends the shared `TeacherTakeAttendanceCubit` used by the
plain class-attendance screen; selected automatically when `examId` is passed to
`AppRoute.goToTeacherTakeAttendance`)
**Reached from:** Session Roster ⋮ → "Take/Edit attendance", or the Grades screen's "Fix attendance"
link — both only for a **SeparateTime** exam.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load / search / infinite scroll | `GET api/exams/{examId}/sessions/{sessionId}` | `page`, `pageSize=10`, `search` | roster rows mapped to Present/Absent/unmarked (`Pending`+`attended:false` = unmarked; `attended`/`AttendedWithGrade` = present; `DidNotAttend` = absent) |
| Manual code lookup (barcode fallback) | same roster call | `search = <code>` | exact case-insensitive `studentCode` match only (never `.first` on a fuzzy match) |
| Tap a student → mark Present/Absent | `PUT api/exams/attendance` | `{occurrenceId, items:[{teacherStudentId, present}]}` — **exam attendance is Present/Absent only**; a Held mark from the shared attendance UI is mapped to `present:false` | on success, roster reloaded |
| Bulk mark (multi-select) | same endpoint | `items[]` with one entry per selected student | same |
| QR scan | `POST api/exams/attendance/scan` | `{occurrenceId, code}` | `{obligationId, teacherStudentId, studentName, studentCode, status, alreadyProcessed}` — `alreadyProcessed:true` still returns success (idempotent re-scan) |

Request/response shapes:
```json
{ "occurrenceId": 5231, "items": [{ "teacherStudentId": 3312, "present": true }] }
```
```json
{ "updatedCount": 1, "allSucceeded": true, "items": [{ "obligationId": 9001, "success": true, "status": "Attended", "rowVersion": "AAAAAAAAB9I=" }] }
```

**`code` values the app special-cases** (`_examAttendanceItemCodeMessageKey`):

| `code` | UI behavior |
|---|---|
| `ObligationNotFound` | "Could not find this student's exam record." |
| `AttendanceReadOnlyForDuringSession` | "Attendance for this exam is read-only here" — defensive; the app never routes a DuringSession exam through this endpoint, so this should not normally fire. |
| `NotAnExam` / `AttendanceNotRecordedForExam` | Generic "attendance could not be saved." |
| anything else | Generic "attendance could not be saved." |

A whole-batch failure (`allSucceeded:false` and `updatedCount:0`) surfaces the first per-item message it
finds; a partial success (`updatedCount>0`) is treated as success and the roster is reloaded.

## Offline Exam Created (success)
_Dart file: `lib/feature/teacher_module/exams/view/teacher_offline_exam_success_view.dart`_
**Reached from:** Create Offline Exam → successful submit. No API of its own — "Go to Exam" replaces
the route with **Offline Exam Detail**.

---

## Online Exams Home
_Dart file: `lib/feature/teacher_module/exams/view/teacher_online_exam_home_view.dart`_
_Cubit: `TeacherOnlineExamHomeCubit`_
**Reached from:** teacher side-menu drawer → "Activity" → "Online Exams".

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load / pull-to-refresh / search (debounced) / infinite scroll / filter sheet (All/Published/Draft/Solved) | `GET api/online-exams` | Query **`Status`** (capitalized; `"Published"`\|`"Draft"`\|`"Closed"`, omitted for "All"), **`Search`**, **`Page`**, **`PageSize`** (all capitalized — different convention from the offline-exam home query) | Either a bare list or `{data:[...], page, pageSize, totalCount, totalPages}`. Per row: `id`/`onlineExamId`, `title`/`name`, `status`, `startDateTime`/`startAt`/`date`, `endDateTime`/`endAt`, `subject`/`subjectName`/`teacherSubjectName`, `questionCount`/`questionsCount`, `assignedCount`/`assignedStudentsCount`/`totalStudents`/`studentCount`, `unsolvedCount`, `solvedCount`, `blockedCount` |
| FAB "+" | *(navigation only)* | — | opens **Create/Edit Online Exam** below |
| Card tap, status = Draft | *(navigation only)* | — | opens **Online Exam Detail**; a `String` pop result (the deleted exam's id) removes that card locally instead of a full reload |
| Card tap, status = Published/Solved | *(navigation only)* | — | opens **Results — Groups/Scope Overview** (read-only, no list reload on return) |
| Card ⋮ → Edit | *(navigation only)* | — | opens **Create/Edit Online Exam** in edit mode |
| Card ⋮ → Delete | `DELETE api/online-exams/{id}` | — | row removed + success toast |
| Card "Add question" shortcut (draft cards with 0 questions) | *(navigation only)* | — | opens the standalone **Question Editor** |
| Card "Show results" shortcut | *(navigation only)* | — | opens **Results — Groups/Scope Overview** |

## Create / Edit Online Exam — Details step
_Dart file: `lib/feature/teacher_module/exams/view/teacher_online_exam_form_view.dart`_
_Cubit: `TeacherOnlineExamCreateCubit`_
**Reached from:** Online Exams Home FAB / card ⋮ → Edit; also reused verbatim by the Homework module
(see below) via `TeacherOnlineExamFormView(repository: <homework repo>, isHomeworkFlow: true)`.

Two-step wizard (`TeacherOnlineExamCreateStepper`): Details → Questions. Nothing is written to the
server on Details alone **except** that "Next" immediately calls create/update — i.e. a Draft row is
created on the server the moment the teacher first reaches the Questions step, even before any question
exists. Recipients are groups/sessions only here (no "students" mode).

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen open, editing | `GET api/online-exams/{id}` then `GET api/online-exams/{id}/questions` | — | hydrates every field below; `questions[]` used only to recompute `examScore` (sum of `degree`) since the detail GET omits questions |
| Groups / Sessions tile | *(navigation only)* | — | Target Scope Picker (shared, see above) |
| Date / Start time / End time pickers | *(local only)* | — | duration is derived (`end - start`), never sent as a separate field beyond what the timestamps imply |
| "All questions have equal score" toggle + score field | *(local only)* | — | when off, exam score is computed later from the sum of each question's own score |
| "Block student if they leave..." toggle + violations stepper (1–5) | *(local only)* | — | `blockOnViolation`, `maxViolations` |
| "Publish exam" toggle | *(local only, applied later)* | — | create always posts `status:"Draft"` regardless of this toggle; publishing happens via a separate PATCH once questions are saved (see Details tab note below) |
| "Next" | `POST api/online-exams` (first time) or `PUT api/online-exams/{id}` (already has a server draft) | see JSON below | `id`/`onlineExamId`, `rowVersion`, `teacherSubjectId`, `visibility` — advances to the Questions step |
| Back / Cancel with unsaved local edits (no server draft yet) | *(local only)* | — | confirm dialog, no request |
| Back / Cancel with an existing server draft (create flow only) | "Discard draft" → `DELETE api/online-exams/{id}`; "Keep draft" → leaves it | — | draft removed or left as-is |

Create body (`onlineExamCreateBody`):
```json
{
  "teacherSubjectId": 4,
  "title": "Unit 2 Quiz",
  "description": "Covers chapters 4-6",
  "instructions": "Covers chapters 4-6",
  "startDateTime": "2026-09-20T08:00:00.000Z",
  "endDateTime": "2026-09-20T09:00:00.000Z",
  "passPercentage": 50,
  "visibility": true,
  "status": "Draft",
  "blockOnViolation": true,
  "maxViolations": 2,
  "scopes": [
    { "scopeType": "Session", "sessionId": 228, "sessionGroupId": null }
  ]
}
```
Notes:
- `instructions` is always a duplicate of `description` — the app has only one text box for both.
- `status` is **always** `"Draft"` on create; the app never creates an exam pre-published.
- `teacherSubjectId`: the app has **no subject picker UI** in this flow at all — it silently resolves to
  the first subject id in the teacher's cached profile (`LocalStorage.getTeacher()?.subjects`), falling
  back to `0` if none is cached. `GET api/Teacher/subjects` is never called from this screen (see
  "Endpoint coverage" for the dead-code detail).
- `scopeType` is `"Session"` or `"SessionGroup"`; the sibling id key is `null`.

Update body (`onlineExamUpdateBody`) — **note it never sends `scopes`**:
```json
{
  "teacherSubjectId": 4,
  "title": "Unit 2 Quiz",
  "description": "Covers chapters 4-6",
  "instructions": "Covers chapters 4-6",
  "startDateTime": "2026-09-20T08:00:00.000Z",
  "endDateTime": "2026-09-20T09:00:00.000Z",
  "passPercentage": 50,
  "visibility": true,
  "blockOnViolation": true,
  "maxViolations": 2,
  "rowVersion": "AAAAAAAAB9I="
}
```
**Gap worth flagging:** the edit screen still shows the Groups/Sessions recipient tile and lets the
teacher change the selection, but `TeacherOnlineExamCreateCubit._toInput()`/`updateExamInfo` never
forwards `selectedGroupIds`/`selectedSessionIds` into the PUT body — `scopes` is a create-only field on
the wire as far as this app is concerned. Changing recipients on an existing exam appears to work in the
UI but is silently discarded on save.

## Create / Edit Online Exam — Questions step
_Widget: `lib/feature/teacher_module/exams/view/widgets/teacher_online_exam_questions_step_body.dart`_
_Cubit: `TeacherOnlineExamQuestionCubit` (`isStandaloneEditor: false`)_
**Reached from:** Details step → "Next".

A "quiz structure" section lets the teacher set a total question count and a MCQ/Multiple-answer split
before typing individual questions; each draft question is the same block used by the standalone editor
below (including the image-upload button).

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Delete a question block (already-persisted question) | `PUT api/online-exams/{id}/questions` | `{questions: [...every remaining question, mapped from domain...]}` | — |
| "Create" / "Save changes" (finish wizard) | 1) `PATCH api/online-exams/{id}/status` `{status:"Draft"}` **only if currently Published** (so the questions PUT below is not rejected by `QuestionsEditableOnlyInDraft`), then 2) `PUT api/online-exams/{id}/questions` with **every** drafted question, then 3) if the Details step's "Publish exam" toggle is on and the exam is not already published, `PATCH api/online-exams/{id}/status` `{status:"Published", rowVersion}` | see bodies below | success → **Online Exam Success** screen (create) or pop with `didMutate:true` (edit) |

`PUT .../questions` body — full replace, not a diff:
```json
{
  "questions": [
    {
      "questionText": "What is 2 + 2?",
      "questionType": "SingleChoice",
      "degree": 5,
      "imageFileId": "5f6e7a1b-...-uuid",
      "options": [
        { "optionText": "3", "isCorrect": false },
        { "optionText": "4", "isCorrect": true }
      ]
    }
  ]
}
```
- `imageFileId` key is **omitted entirely** when the question has no image (never sent as `null`).
- `questionType` on the wire is `"SingleChoice"` or `"MultipleChoice"` (note: **not** `"OneChoice"` —
  the app's own internal enum name `oneChoice` maps to the wire value `SingleChoice`).

Status PATCH body:
```json
{ "status": "Published", "rowVersion": "AAAAAAAAB9I=" }
```
`rowVersion` is omitted only when the app has none cached yet; every call's response `rowVersion` is
chained into the next one to avoid a stale-token conflict.

## Add / Edit Question (standalone editor)
_Dart file: `lib/feature/teacher_module/exams/view/teacher_online_exam_question_editor_view.dart`_
_Cubit: `TeacherOnlineExamQuestionCubit` (`isStandaloneEditor: true`)_
**Reached from:** Online Exam Detail → Questions tab → "+" / a question row; also Online Exams Home
card shortcut "Add question" for a 0-question draft.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| "Add photo" on a question | `POST api/upload` (multipart) | field `files` = one compressed JPEG (1080px/85%, or a picked file), field `category` = `"OnlineExamQuestionImage"` | `data[0]` = `{fileId, url, originalName, size, mimeType}` — `fileId` held as `imageFileId` on the draft, `url` used to preview it immediately |
| Remove photo | *(local only)* | — | clears `imageFileId`/`imageUrl` from the draft (the uploaded file itself is never explicitly deleted via `DELETE api/upload`) |
| "Submit" (new question) | `POST api/online-exams/{id}/questions` | one question body (see shape above, singular) | envelope success only — the response may be `data:true` or a question object; the app doesn't rely on the payload beyond envelope success |
| "Submit" (editing an existing question) | `PUT api/online-exams/{id}/questions` | **every** existing question with the edited one substituted in place (full replace) | — |
| Delete a question block (multi-question mode only) | `PUT api/online-exams/{id}/questions` | remaining questions, edited one removed | — |

## Online Exam Created (success)
_Dart file: `lib/feature/teacher_module/exams/view/teacher_online_exam_success_view.dart`_
**Reached from:** Questions step → finish wizard (create only). No API of its own — "Go to Exam" pops
back to Home with `didMutate:true` (so the list reloads) then pushes **Online Exam Detail**.

## Online Exam Detail
_Dart file: `lib/feature/teacher_module/exams/view/teacher_online_exam_detail_view.dart`_
_Cubit: `TeacherOnlineExamDetailCubit`_
**Reached from:** Online Exams Home → card tap (Draft exams only — Published/Solved go straight to
Results).

Three tabs: Information (read-only), Questions, Settings.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load | `GET api/online-exams/{id}` then `GET api/online-exams/{id}/questions` | — | Information tab fields; question list feeds the Questions tab and re-derives `examScore`/`scorePerQuestion` |
| Questions tab → "+" / edit / delete a question | *(navigation to standalone editor, or)* `PUT api/online-exams/{id}/questions` (delete) | — | tab list refreshed; `markMutated()` sets the pop result to `true` |
| Settings tab → "Visibility" switch | `PATCH api/online-exams/{id}/status` | `{status: "Published"|"Draft", rowVersion}` | optimistic toggle, rolled back on failure |
| Settings tab → "Edit Exam information" | *(navigation only, questions ensured-loaded first so the edit form's score fields are correct)* | — | opens Details step in edit mode; a successful save pops the WHOLE detail screen (not just back to this tab) |
| Settings tab → "Edit or add questions" | *(local tab switch)* | — | jumps to Questions tab |
| Settings tab → "Delete Exam" | `DELETE api/online-exams/{id}` | — | pops with the deleted exam's id (`String`) so Home can remove the row without a full reload |

## Results — Groups/Scope Overview
_Dart file: `lib/feature/teacher_module/exams/view/teacher_online_exam_groups_results_view.dart`_
_Cubit: `TeacherOnlineExamResultsCubit`_
**Reached from:** Online Exams Home → card tap (Published/Solved), or card "Show results" shortcut.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load / pull-to-refresh | `GET api/online-exams/{id}` (`withResults:true` → also calls `GET .../overview` then, if that has no per-student roster, `GET .../scope-analysis`) | — | aggregate stats (`averageGradesPercent`, `totalExams` = assigned count, `highestGrade`, `lowestGrade`, `passedCount`, `failedCount`, `blockedCount`, `missedCount` — counts are recomputed client-side from the roster when one is available, else taken from the aggregate as-is) + `scopes[]` → one card per session/group (`name`, `sessionsCount`/`studentsCount`) |
| Search / status filter (All/Passed/Failed) | *(client-side over the already-loaded roster)* | — | — |
| A scope card tap | *(navigation only)* | — | opens **Results — Student list**, narrowed to that scope's students (only when the roster rows actually carry a `sessionId`/`sessionGroupId` tag — otherwise every scope shows the full roster, since there's nothing to narrow by) |

## Results — Student list (one group/session)
_Dart file: `lib/feature/teacher_module/exams/view/teacher_online_exam_results_view.dart`_
_Cubit: `TeacherOnlineExamResultsCubit(groupId:, groupName:)`_
**Reached from:** Groups/Scope Overview → a scope card.

| UI element | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load | same as above | — | same summary, `students[]` filtered to the passed `groupId` |
| Student row tap (any status except "not attended") | *(navigation only)* | — | opens **Student Answer Sheet**; a never-attempted student's row is inert (no answer sheet to show — the API would 404) |
| "Unblock" button (blocked students only) | `PATCH api/online-exams/{id}/students/{teacherStudentId}/status` | `{status: "InProgress"}` — `"InProgress"` is the app's chosen unblock transition; there is no separate "Active" value in the domain enum | success → full `load(force:true)` (never a local patch — the server recomputes the report) |
| "View question analysis" tile | *(navigation only)* | — | opens **Question Analysis** |

**Unblock error handling:** a 409 (a student who already submitted their attempt cannot be reopened) is
surfaced verbatim as the server's message — the button is simply never shown for a student whose status
isn't `Blocked`, so this should only occur on a race.

## Question Analysis
_Dart file: `lib/feature/teacher_module/exams/view/teacher_exam_analysis_view.dart`_
_Cubit: `TeacherExamAnalysisCubit`_
**Reached from:** Student list (Results) → "View question analysis".

| UI element | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load / pull-to-refresh | `GET api/online-exams/{id}/question-analysis` | — | `finalizedAttempts` (shown as "Based on N finalized attempts"), `questions[]`: `questionId`, `questionText`, `degree`, `order`, `attemptedCount`, `correctCount`, `skippedCount`, `correctPercentage` (nullable — "nobody attempted it" is deliberately distinct from "0% correct"), `topWrongOptionText`, `topWrongOptionCount` |

No writes on this screen. A question with `correctPercentage == null` renders "Untouched" instead of a
0% badge.

## Student Answer Sheet
_Dart file: `lib/feature/teacher_module/exams/view/teacher_exam_answer_sheet_view.dart`_
_Cubit: `TeacherExamAnswerSheetCubit`_
**Reached from:** Results (student list) → a student row.

| UI element | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load / pull-to-refresh | `GET api/online-exams/{id}/students/{teacherStudentId}/answers` | — | reuses the STUDENT-side review parser byte-for-byte (`StudentOnlineExamReviewApiDto`): `examId`, `examName`, `finalized`, `score`, `percentage`, `reportStatus`, `questions[]` (`questionId`, `questionText`, `questionType`, `degree`, `awardedDegree`, `imageUrl`, `options[]`: `id`/`optionId`, `optionText`/`label`/`text`, `isSelected`/`selected`, `isCorrect`/`correct`) |

`finalized:false` renders "Not finalized yet" instead of a score (an in-progress attempt's running total
is not shown as if it were a final grade).

---

## Homework — UI only, not wired to any backend
_Dart files: `lib/feature/teacher_module/homework/view/*.dart`_ (home, form, detail, question editor,
success) _+ `lib/feature/teacher_module/homework/data/teacher_homework_repository_impl.dart`_
**Reached from:** teacher side-menu drawer → "Activity" → "Homework" — but this entire menu row is
gated by `AppConfig.showHomework` (`= !AppConfig.isMvp`), and **`isMvp` is currently `true`**, so the
Homework menu entry is **not shown** in the current build at all.

Every homework screen is a thin wrapper reusing the online-exam views
(`TeacherHomeworkFormView` → `TeacherOnlineExamFormView`, `TeacherHomeworkDetailView` →
`TeacherOnlineExamDetailView`, `TeacherHomeworkQuestionEditorView` →
`TeacherOnlineExamQuestionEditorView`) but pointed at `TeacherHomeworkRepositoryImpl`, which is a
**pure in-memory mock store** (`List<TeacherOnlineExamDetail>` seeded from
`teacherHomeworkMockDetails()`, an incrementing local id counter) — registered as the real DI singleton
for `TeacherHomeworkRepository`, so this isn't a placeholder swapped out at build time; it's what ships.
**No HTTP call is made for any homework CRUD, publish, question, or results action** — create/update/
delete/publish/add-question/delete-question/results all just mutate the in-memory list and return
immediately. `fetchQuestionAnalysis` and `fetchStudentAnswerSheet` and `unblockStudent` explicitly return
a failure ("Homework analysis/answer sheets are not available" / "Homework students are never blocked"),
so the app never shows those entry points for homework.

**One real network call still fires despite this:** the question editor's "Add photo" button calls the
same shared `UploaderService`, so picking an image for a homework question really does
`POST api/upload` (category `OnlineExamQuestionImage` — homework has no category of its own) and gets a
real `fileId`/`url` back — it is simply attached to a question object that is never persisted anywhere
but local memory and is discarded when the app restarts. A backend developer inspecting upload traffic
may see `OnlineExamQuestionImage` uploads that never show up attached to any real exam; this is why.

Screen list (all present, none call a homework-specific endpoint):
- **Homework Home** (`teacher_homework_home_view.dart`, `TeacherHomeworkHomeCubit`) — clones the Online
  Exams Home layout (Published/Draft/Past sections, filter sheet, FAB).
- **Create/Edit Homework** (`teacher_homework_form_view.dart`) — wraps `TeacherOnlineExamFormView`.
- **Homework Detail** (`teacher_homework_detail_view.dart`) — wraps `TeacherOnlineExamDetailView`.
- **Add/Edit Homework Question** (`teacher_homework_question_editor_view.dart`) — wraps
  `TeacherOnlineExamQuestionEditorView`.
- **Homework Created (success)** (`teacher_homework_success_view.dart`) — same success-screen pattern,
  navigation only.

### Endpoint coverage

`GET api/exams/home`
`POST api/exams`
`GET api/exams/{examId}`
`PUT api/exams/{examId}`
`DELETE api/exams/{examId}`
`GET api/exams/session-dates`
`GET api/exams/{examId}/sessions/{sessionId}`
`PUT api/exams/grades`
`PUT api/exams/attendance`
`POST api/exams/attendance/scan`
`GET api/online-exams`
`POST api/online-exams`
`GET api/online-exams/{onlineExamId}`
`PUT api/online-exams/{onlineExamId}`
`DELETE api/online-exams/{onlineExamId}`
`GET api/online-exams/{onlineExamId}/overview`
`GET api/online-exams/{onlineExamId}/scope-analysis`
`GET api/online-exams/{onlineExamId}/questions`
`PUT api/online-exams/{onlineExamId}/questions`
`POST api/online-exams/{onlineExamId}/questions`
`PATCH api/online-exams/{onlineExamId}/status`
`GET api/online-exams/{onlineExamId}/question-analysis`
`GET api/online-exams/{onlineExamId}/students/{teacherStudentId}/answers`
`PATCH api/online-exams/{onlineExamId}/students/{teacherStudentId}/status`
`POST api/upload` (category `OnlineExamQuestionImage`, shared handshake — see Videos chapter §"upload →
fileId → attach" for the general contract)

**Defined but never called from this chapter's code:**
- `webPathOnlineExamQuestionsBulk` → `POST api/online-exams/{id}/questions/bulk`. A wrapper method
  (`bulkCreateQuestions`) exists on `TeacherOnlineExamRemoteDataSource`, but nothing in the repository or
  any cubit calls it — question creation always goes through the single-question POST or the full-replace
  PUT.
- `webPathOnlineExamQuestionsOverview` → `GET api/online-exams/{id}/questions/overview`. The constant
  exists with no wrapper method and no caller anywhere in the app.
- `webPathTeacherSubjects` → `GET api/Teacher/subjects`. The exams module's own
  `TeacherOnlineExamRemoteDataSource.fetchSubjects()`/`TeacherOnlineExamRepositoryImpl.fetchSubjects()`
  implement this end-to-end but are **never invoked** by any exams-module cubit or view — the create form
  silently resolves `teacherSubjectId` from the cached teacher profile instead (see the Create/Edit
  Details-step note above). The same URL constant IS called live, but from the unrelated Auth module
  (`lib/feature/auth/data/auth_remote_data_source.dart`, subject selection during signup) — out of scope
  for this chapter.
