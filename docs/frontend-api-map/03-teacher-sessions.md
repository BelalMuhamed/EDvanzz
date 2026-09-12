# 03 — Teacher: Sessions

Source: `lib/feature/teacher_module/sessions/**` (~100 files). Endpoint constants:
`lib/core/network_services/web_constant.dart` (`webPathSession*` / `webPathTeacherSession*`,
lines ~98–137). Data flow: `TeacherSessionRemoteDataSource` (Dio) → `TeacherSessionRepositoryImpl`
→ cubits → views. All responses are read through the standard envelope
`{success, code, message, data}`; list endpoints return the standard paginated shape
(`data`/`items`, `page`, `pageSize`, `totalCount`, `totalPages`) per
`lib/core/network_services/paginated_api_response.dart` — referenced below, not re-explained.

The folder also calls two endpoints that belong to other chapters — named here, detailed there:
`api/teacherstudent/students` / `api/teacherstudent/assignment-chips` (chapter 02 — Students),
`api/Attendance/sessions/{id}/absences` (Attendance chapter) and `api/Payment/unpaid` (Payments
chapter).

## Wire conventions used throughout this chapter

- **`selectedDays`** — `List<int>`, indices **0–6 where 0 = Saturday … 6 = Friday**
  (`formatAppDayLocaleKeys` order: Sat, Sun, Mon, Tue, Wed, Thu, Fri —
  `lib/core/utils/app_day_labels.dart`). Only sent/read for `occurrenceType: "Weekly"`; null/omitted
  for `"Monthly"`.
- **`occurrenceType`** — string enum `"Weekly"` \| `"BiWeekly"` \| `"Monthly"`. The create/edit
  screens only expose a Weekly/Monthly toggle — `BiWeekly` is a valid wire/domain value (parsed on
  read) but is **not currently selectable** from either form.
- **`paymentType`** — string enum `"Monthly"` \| `"PerSession"`. UI toggle ids are `monthly` /
  `per_session` internally, translated to the wire strings by `_paymentTypeToApi`.
- **`monthlyDayOfMonth`** — `int` (1–31), only meaningful/sent when `occurrenceType: "Monthly"`.
- **`startTime`** — the app **sends** `"HH:mm"` (24-hour, zero-padded, no seconds) on both create
  and update (`_formatTimeForApi` on create; `_normalizeStartTimeForApi` truncates whatever the
  server returned to two `":"`-separated parts on update). The app **reads** either `"HH:mm:ss"` or
  `"HH:mm"` back (`TimeService._parseApiTimeOfDay` tries both patterns, then a raw split) — so
  expect the server's response format may carry seconds even though the client never sends them.
- **`startDate`/`endDate`** — full ISO-8601 datetime strings (`DateTime.toIso8601String()`), read
  back as calendar days (`parseApiCalendarDate`, throws on an unparseable value — a session with no
  usable start/end date is treated as a broken payload, not defaulted).
- **`sessionAmount`** — `double`.
- **`durationMinutes`** — plain `int`; free-typed by the teacher (no preset chips despite the
  "duration chip" widget name), default `30` on create.
- **`sessionGroupId`** — `int?`. On create it's included only when non-null; on update it is
  **always** sent (including explicit `null` to clear the group).
- **`priceReconcile`** — present only on an **update** response whose `sessionAmount` actually
  changed (and only on a backend new enough to compute it): `{repriced, keptPaid, keptManual,
  studentsAffected, earliestMonth}` (`earliestMonth` a calendar day, first-of-month, or absent when
  nothing was repriced). The edit screen turns this into a toast (see below) — never re-derive it
  from a diff of before/after amounts.
- Error handling in this chapter is generic: every mutating call surfaces `result.failureMessage`
  (the envelope's localized `message`) via a toast. No screen in this folder branches on a specific
  numeric/string error `code` — the two "conflict" flows (session-in-use delete, student-in-another-
  session assign) are modeled as **normal success responses carrying extra data**
  (`assignedStudentCount`/`affectedLinkedSessions` on delete-confirmation; `warnings[]` on
  assign-students), not as error codes the client inspects.

---

## Sessions (list + groups tab)
_Dart file: `lib/feature/teacher_module/sessions/view/teacher_sessions_view.dart`_
_Cubit: `teacher_sessions_cubit.dart`_
**Reached from:** bottom nav / side menu → "Sessions" tab.

Two segments (Sessions / Groups) sharing one cubit and one search box. The FAB opens "Create
session" or "Create group".

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen enters / pull-to-refresh / search (350ms debounce) / filter sheet applied | `GET api/session/{teacherId}/sessions` | Query: `page`, `pageSize` (10), `search?` (trimmed), `groupId?`, `occurrenceType?` (`Weekly`\|`Monthly`, from the filter sheet), `activeOnly?`/`expiredOnly?` (bool, sent only when true), `sortBy` (`DateAdded`\|`SessionName`\|`StartDate`\|`StudentCount`, default `DateAdded`), `sortDirection` (`Asc`\|`Desc`, default `Desc`) | Paginated `id, teacherId, sessionName, occurrenceType, paymentType, sessionAmount, startDate, endDate, startTime, durationMinutes, studentCount, isExpired, selectedDays, monthlyDayOfMonth, sessionGroupId, sessionGroupName, linkedSessions[{id, sessionName}]` per row → `page`/`hasMore` drive infinite scroll |
| Switch to "Groups" segment (first time / pull-to-refresh) | `GET api/session/{teacherId}/groups` | — | array of `{id, teacherId, groupName, sessionCount, students_number\|studentsNumber\|studentCount}` |
| Tap a session row | *(navigation only)* | — | opens **Session Overview** below |
| Tap a group row | *(navigation only)* | — | opens **Group Detail** below |
| Session row → link icon | *(navigation only)* | — | opens **Session Membership Linked** below |
| Select rows → Duplicate | `POST api/session/{teacherId}/sessions/{sessionId}/duplicate` — **once per selected session, sequentially, no request body** | — | full session object of the new duplicate (discarded here — list is reloaded after) |
| Select 1 session → Delete | `GET api/session/{teacherId}/sessions/{sessionId}/delete-confirmation` then, on confirm, `DELETE api/session/{teacherId}/sessions/{sessionId}` | — | confirmation GET: `{sessionId, sessionName, assignedStudentCount, affectedLinkedSessions[{id,sessionName}]}` → builds the dialog body naming the student count and any linked sessions that will be affected |
| Select 2+ sessions → Delete | `DELETE api/session/{teacherId}/sessions/{sessionId}` — once per selected id, sequentially (no bulk delete endpoint; no delete-confirmation call for the bulk path — a generic "sessions will be permanently deleted" copy is shown instead) | — | — |
| Select 2+ groups → Delete | `DELETE api/session/{teacherId}/groups/{groupId}` — once per selected id | — | — |
| Export selected (sessions or groups) | *(no call — local CSV/PDF build from already-loaded rows)* | — | — |
| FAB → "Create session" / "Create group" | *(navigation only)* | — | opens **Create Session** / **Create Group** below |

## Create Session
_Dart file: `lib/feature/teacher_module/sessions/view/teacher_create_session_view.dart`_
_Cubit: `teacher_create_session_cubit.dart`_
**Reached from:** Sessions tab FAB → "Create session"; also Group Detail's own add-session menu
(passes `sessionGroupId` so the new session is born into that group).

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen opens | `GET` teacher configuration (settings module, outside this chapter) via `TeacherSettingsRepository.fetchConfiguration` | — | `sessionNameMode` — when the teacher's config is set to "manual" naming, the Session Name field is shown and required; otherwise the field is hidden and `sessionName` is sent as `null` (server auto-names it) |
| "Create" button | `POST api/session` | See JSON below | full created session object (`TeacherSessionApiDto.fromJson`) — screen just checks for an error message, then pops `true` and routes to the success screen |

Request body (weekly, manual name on):
```json
{
  "teacherId": 42,
  "sessionName": "Grade 10 Physics",
  "occurrenceType": "Weekly",
  "selectedDays": [3],
  "paymentType": "Monthly",
  "sessionAmount": 300,
  "startDate": "2026-09-13T00:00:00.000",
  "endDate": "2026-12-20T00:00:00.000",
  "startTime": "17:30",
  "durationMinutes": 60,
  "sessionGroupId": 7
}
```
Notes: `selectedDays: [3]` = Tuesday (index 0=Sat). `sessionName` is omitted (sent as `null`) when
the teacher's naming mode is auto. `monthlyDayOfMonth` replaces `selectedDays` (and `selectedDays`
is omitted) when `occurrenceType` is `"Monthly"`; its value is the day-of-month picked in the date
field (e.g. a monthly session anchored on the 15th sends `"monthlyDayOfMonth": 15`).
`sessionGroupId` is included only if the screen was opened from a group's "add session" action.

## Edit Session
_Dart file: `lib/feature/teacher_module/sessions/view/teacher_edit_session_view.dart`_
_Cubit: `teacher_edit_session_cubit.dart`_
**Reached from:** Session Overview → menu (⋮) → "Edit".

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen opens | `GET api/session/{teacherId}/sessions/{sessionId}` | — | full session object, pre-fills every field |
| "Save" | `PUT api/session/{teacherId}/sessions/{sessionId}` | See JSON below | updated session object; if `sessionAmount` changed, `priceReconcile` drives a toast on top of the plain "session updated" one |

Request body — same shape as create minus `teacherId`, plus `sessionGroupId` **always present**
(never omitted, so sending `null` explicitly clears the session's group):
```json
{
  "sessionName": "Grade 10 Physics",
  "occurrenceType": "Weekly",
  "selectedDays": [3],
  "paymentType": "Monthly",
  "sessionAmount": 350,
  "startDate": "2026-09-13T00:00:00.000",
  "endDate": "2026-12-20T00:00:00.000",
  "startTime": "17:30",
  "durationMinutes": 60,
  "sessionGroupId": null
}
```
Reprice toast composition when `priceReconcile` comes back non-empty (clauses joined with " · ",
each appearing only when its count is non-zero): "N bills updated (from <month>)" +
"M kept — already paid" + "K kept — set by hand" (`keptManual` reports a hand-set joining month
that a price change deliberately skipped — see repo `CLAUDE.md` §7.4 proration rules).

## Session Overview (Sessions tab hub)
_Dart file: `lib/feature/teacher_module/sessions/view/teacher_session_overview_view.dart`_
_Cubit: `teacher_session_detail_cubit.dart`_
**Reached from:** Sessions tab → tap a session row; Group Detail → tap a session card.

Two tabs: **Overview** (schedule/payment/membership summary, purely a re-render of the session GET
— no separate endpoint) and **Students** (session roster, chapter 02's student-list endpoint
scoped by `sessionId`).

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen opens | `GET api/session/{teacherId}/sessions/{sessionId}` | — | full session object → mapped to `TeacherSessionOverview` (recurrence label, day label via day-index → locale key, duration, group name or "Unassigned", 12-hour start/end time, payment type label, formatted fee, linked-membership title) |
| Switch to "Students" tab (first time) / search box (350ms debounce) / infinite scroll | `GET api/teacherstudent/students` (chapter 02) | Query: `sessionId`, `page`, `pageSize:10`, `search?` | roster rows rendered via `StudentListItem` |
| Menu (⋮) → "Edit" | *(navigation only)* | — | opens **Edit Session**; on return, session GET is re-fetched (`force: true`) |
| Menu (⋮) → "Delete" | `GET .../delete-confirmation` then `DELETE api/session/{teacherId}/sessions/{sessionId}` | — | same confirmation shape as the list screen; on success pops `true` to the caller |
| Menu (⋮) → "Export QR code" | *(no call here — pre-fetches export rows)* `GET api/teacherstudent/students` (all pages, chapter 02) | Query: `sessionId`, `pageSize:10` (looped) | builds CSV/PDF rows, then navigates to the export-format screen (separate chapter) |
| "Assign" button (top-right) | *(navigation only)* | — | opens **Assign Students to Session** below; on return, session GET + (if the Students tab was ever opened) the roster GET are both re-fetched |
| Student row → export icon | same export GET as above, filtered to `singleStudentId` | — | single-row export |
| Student row → transfer icon | *(opens the single-student transfer bottom sheet — see **Transfer Student (quick)** below)* | — | — |
| Overview tab → "Membership Linked" card | *(navigation only)* | — | opens **Session Membership Linked** below |

## Session Hub (from Home)
_Dart file: `lib/feature/teacher_module/sessions/view/teacher_session_from_view.dart`_ (`TeacherSessionFromView`, internally called "Figma `from session`")
**Reached from:** Home tab → a session card (`AppRoute.goToTeacherSessionFrom`) — a **different**
route than the Sessions-tab overview above, but backed by the same `TeacherSessionDetailCubit`
plus two extra cubits for inline violation previews.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen opens | `GET api/session/{teacherId}/sessions/{sessionId}` then (chained) `GET api/teacherstudent/students?sessionId=…` | — | session summary header + enrolled-student roster in one load |
| Screen opens (in parallel) | `GET api/Payment/unpaid` (Payments chapter) | Query: `SessionId`, `PaymentType:"Monthly"`, `MinConsecutiveUnpaid:1`, `AsOfMonth` (`"YYYY-MM"`), `Page`, `PageSize:10` | inline "Unpaid students" preview card + count |
| Screen opens (in parallel) | `GET api/Attendance/sessions/{sessionId}/absences` (Attendance chapter) | Query: `MinConsecutiveAbsences:1`, `Page`, `PageSize:20`, `Search?` | inline "Absent students" preview card + count |
| Pull-to-refresh | re-runs both preview queries above | — | — |
| "Take attendance" | *(navigation only, other module)* | — | `AppRoute.goToTeacherTakeAttendance` |
| "Collect payment" | *(navigation only, other module)* | — | `AppRoute.goToTeacherCollectPayment` |
| Unpaid/Absent card → "View all" | *(navigation only)* | — | opens **Violation Students List** below, pre-scoped to `kind: unpaid` / `kind: absent` |
| "Manage" / header tap | *(navigation only)* | — | opens the Sessions-tab **Session Overview** for the same session |
| Roster row → profile | *(navigation only, chapter 02)* | — | student profile |
| Export | same student-export GET as the other overview screen | — | — |

## Create Group
_Dart file: `lib/feature/teacher_module/sessions/view/teacher_create_group_view.dart`_
_Cubit: reuses `TeacherSessionsCubit.createGroup(...)`_
**Reached from:** Sessions tab FAB → "Create group".

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen opens | `GET api/session/{teacherId}/sessions?groupId=-1&pageSize=10` (looped across pages) | Query `groupId=-1` is the client-side sentinel for "sessions not in any group" | list of ungrouped sessions offered as optional "add now" picks |
| "Create" | `POST api/session/groups` | `{teacherId, groupName, description?}` (description omitted if blank after trim) | new group `{id, teacherId, groupName, sessionCount, studentCount?}` |
| …then, per session ticked in "Add session now" | `GET api/session/{teacherId}/sessions/{sessionId}` then `PUT api/session/{teacherId}/sessions/{sessionId}` | PUT body = the full session unchanged except `sessionGroupId` set to the new group's id | — (loop continues even if a mid-loop step fails, surfacing that step's error and aborting) |

There is no "create group with sessions" endpoint — the picked sessions are attached with the same
**GET-then-PUT-with-`sessionGroupId`** pattern the Edit Session screen uses, run once per session
after the group POST succeeds.

## Group Detail
_Dart file: `lib/feature/teacher_module/sessions/view/teacher_group_detail_view.dart`_
_Cubit: `teacher_group_detail_cubit.dart`_
**Reached from:** Sessions tab → Groups segment → tap a group.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen opens / pull-to-refresh | `GET api/session/{teacherId}/sessions?groupId={groupId}&pageSize=10` (looped) | — | session cards: title, schedule line, price label, duration, enrolled count |
| Header menu → "Edit" (rename) | `PUT api/session/{teacherId}/groups/{groupId}` | `{"groupName": "<trimmed>"}` | renamed group `{id, teacherId, groupName, sessionCount, studentCount?}` |
| Header menu → "Delete" | `DELETE api/session/{teacherId}/groups/{groupId}` | — | pops `true` to the caller (list screen removes the group row) |
| Header menu → "Export QR code" | `GET api/teacherstudent/students?sessionId=…&pageSize=10` (chapter 02, looped per session in the group, de-duplicated by student id) | — | builds one combined export across every session in the group |
| "+ Add session" → "Add existing sessions" | *(navigation only)* | — | opens **Add Existing Sessions to Group** below |
| "+ Add session" → "Create new session" | *(navigation only)* | — | opens **Create Session** with `sessionGroupId` pre-filled |
| Session card → tap | *(navigation only)* | — | opens **Session Overview** |
| Session card → "Remove from group" | `GET api/session/{teacherId}/sessions/{sessionId}` then `PUT api/session/{teacherId}/sessions/{sessionId}` with `sessionGroupId: null` | — | card removed optimistically; rolled back on failure |

## Add Existing Sessions to Group
_Dart file: `lib/feature/teacher_module/sessions/view/widgets/add_existing_sessions_to_group_bottom_sheet.dart`_
**Reached from:** Group Detail → "+ Add session" → "Add existing sessions".

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Sheet opens | `GET api/session/{teacherId}/sessions?groupId=-1&pageSize=10` (looped) | `groupId=-1` sentinel = ungrouped sessions | picker rows |
| "Add session" (submit) | per selected session: `GET api/session/{teacherId}/sessions/{sessionId}` then `PUT .../{sessionId}` with `sessionGroupId` set to this group | — | group's session list reloaded via the same `GET api/session/{teacherId}/sessions?groupId={groupId}` Group Detail uses |

## Rename Group (bottom sheet)
_Dart file: `lib/feature/teacher_module/sessions/view/widgets/rename_group_bottom_sheet.dart`_
**Reached from:** Group Detail header menu → "Edit".

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| "Submit" | `PUT api/session/{teacherId}/groups/{groupId}` | `{"groupName": "<trimmed>"}` | renamed group; caller updates the displayed title locally |

## Session Membership Linked
_Dart file: `lib/feature/teacher_module/sessions/view/teacher_membership_linked_view.dart`_
_Cubit: `teacher_membership_linked_cubit.dart`_
**Reached from:** Session Overview (both variants) → "Membership Linked" card.

Session **links** (`api/session/links`) are a distinct concept from **groups**: a link pairs two
sessions (e.g. a recorded/live pair) with no group semantics; `TeacherSession.linkedSessions` is
returned inline on the session GET.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen opens | `GET api/session/{teacherId}/sessions/{sessionId}` then `GET api/session/{teacherId}/sessions?activeOnly=true&pageSize=10` (looped) | — | current session's `linkedSessions[]` (ids/names) used to partition the full active-session list into "available to link" vs. "currently linked" (enriched with full details when the linked session appears in the active list; else a minimal stub built from the link's `id`/`sessionName` alone) |
| "Available to link" row → link icon | `POST api/session/links` | `{"teacherId": 42, "sessionIdA": 228, "sessionIdB": 231}` | boolean success; screen then force-reloads (the `GET .../sessions/{id}` + active-list pair above) |
| "Currently linked" row → unlink icon | `DELETE api/session/{teacherId}/links/{sessionIdA}/{sessionIdB}` | — | boolean success; row moved from "linked" back to "available" optimistically, rolled back on failure |

## Assign Students to Session
_Dart file: `lib/feature/teacher_module/sessions/view/teacher_assigned_students_view.dart`_
_Cubit: `teacher_assigned_students_cubit.dart`_
**Reached from:** Session Overview → "Assign" button.

Tap-to-select (no long-press); "All / In session / Not assigned" filter chips. Endpoints named
below with `students`/`assignment-chips` are chapter 02's — cross-referenced here because this
screen's whole flow is driven by them.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen opens / filter chip tap / search (350ms debounce) / infinite scroll | `GET api/teacherstudent/students` (chapter 02) | Query: `page`, `pageSize:10`, `search?`, `sessionId` (only when filter = "In session" — set to **this** session), `missingSession:true` (only when filter = "Not assigned") | roster rows, each classified assigned/unassigned against **this** session |
| Chip counts (after every load and after every mutation) | `GET api/teacherstudent/assignment-chips` (chapter 02) | Query: `search?` | `totalCount` → "All"; per-session assigned count for **this** session → "In session"; `unassignedCount` → "Not assigned". One request answers all three chips instead of three parallel `counts` probes |
| "Select all" (current chip + search scope) | `GET api/teacherstudent/students` with `pageSize:100` (looped until exhausted) | same filters as above | hydrates the full matching set so "Select all" ticks everyone, not just loaded rows |
| Bottom bar → "Assign" (selection = unassigned students) | `POST api/session/assign-students` — chunked, **max 50 student ids per call** | `{"teacherId": 42, "sessionId": 228, "studentIds": [101, 102, ...]}` | `{assignedCount, warnings: [{studentId, studentName, studentCode, currentSessionId, currentSessionName, newSessionId, newSessionName}]}` — any student already in another session comes back as a `warning`, never a hard error |
| …if `warnings` non-empty → confirm dialog → confirm | `POST api/session/{teacherId}/sessions/{sessionId}/confirm-reassign` — chunked, max 50 | **raw JSON array of ints**, e.g. `[101, 102]` (not wrapped in an object) | `data` is a plain **int** — count of students actually reassigned |
| Bottom bar → "Unassign" (selection = assigned students) | `DELETE api/session/{teacherId}/sessions/{sessionId}/students/{studentId}` — one call **per student**, sequential (no batch unassign endpoint) | — | boolean success per call |
| "+ " FAB → add student shortcut | *(navigation only, chapter 02's Add Student flow)*, pre-seeded with this `sessionId` | — | on return, the roster is reloaded preserving already-scrolled pages |

Non-trivial request body — assigning 2 students, one of whom is already elsewhere:
```json
{
  "teacherId": 42,
  "sessionId": 228,
  "studentIds": [5311, 5312]
}
```
Response carrying a reassignment warning:
```json
{
  "assignedCount": 1,
  "warnings": [
    {
      "studentId": 5312,
      "studentName": "Salma Hassan",
      "studentCode": "A17",
      "currentSessionId": 190,
      "currentSessionName": "Grade 9 Chemistry",
      "newSessionId": 228,
      "newSessionName": "Grade 10 Physics"
    }
  ]
}
```
The app never treats this as an HTTP error — `assignedCount` students moved immediately, and the
warned student needs a second call (`confirm-reassign`) after the teacher explicitly confirms the
move in a dialog naming both sessions.

## Transfer Student (quick, single)
_Dart file: `lib/feature/teacher_module/sessions/view/widgets/teacher_transfer_student_bottom_sheet.dart`_
_Cubit: `teacher_transfer_students_cubit.dart`_
**Reached from:** Session Overview → a roster row's transfer icon.

Re-targets **one already-known student** into a different session. Internally this is the exact
same assign/confirm-reassign pair the Assign screen uses — "transfer" is a UI framing, not a
distinct endpoint.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Sheet opens | `GET api/session/{teacherId}/sessions?pageSize=10` (looped, filtered client-side to exclude the source session) | — | destination-session picker list |
| Search box (350ms debounce) | same GET, with `search` | — | — |
| "Confirm transfer" | `POST api/session/assign-students` | `{"teacherId": 42, "sessionId": <target>, "studentIds": [<studentId>]}` | on a `warnings` hit → confirm dialog → `POST .../confirm-reassign` with `[<studentId>]` |

## Transfer Students (bulk pick) + Transfer to New Session
_Dart files: `lib/feature/teacher_module/sessions/view/teacher_transfer_student_view.dart` (pick students) → `lib/feature/teacher_module/sessions/view/teacher_transfer_to_new_session_view.dart` (pick destination)_
_Cubit: `teacher_transfer_students_cubit.dart`_
**Reached from:** (student-picker entry point elsewhere in the app; both screens share the cubit
above with the source session's id).

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Picker screen opens / search (350ms debounce) / infinite scroll | `GET api/teacherstudent/students` (chapter 02) | Query: `sessionId` (source), `page`, `pageSize:10`, `search?` | source-session roster to tick |
| "Continue" | *(navigation only)* | — | opens the destination picker with the ticked student ids |
| Destination screen opens / infinite scroll | `GET api/session/{teacherId}/sessions?pageSize=10` (looped) | — | destination-session list (source session not excluded here, unlike the quick single-transfer sheet) |
| "Transfer" | `POST api/session/assign-students` | `{"teacherId": 42, "sessionId": <target>, "studentIds": [<id1>, <id2>, ...]}` | same `warnings[]` contract; a warned subset is resolved with one `POST .../confirm-reassign` carrying only the warned ids |

## View Violations (entry)
_Dart file: `lib/feature/teacher_module/sessions/view/teacher_view_violations_view.dart`_
**Reached from:** *(no direct action wires to this screen in the current build — `AppRoute.goToTeacherViewViolations` exists but is not called from anywhere in the app; the actual entry point teachers use is the Session Hub's inline "View all" links, which navigate straight to the list below).*

No API of its own — two static section headers ("Absent students" / "Unpaid students"), each with
a "View all" that opens the list below pre-scoped to `kind`.

## Violation Students List
_Dart file: `lib/feature/teacher_module/sessions/view/teacher_violation_students_list_view.dart`_
_Cubits: `teacher_session_absent_students_cubit.dart` (kind = absent) / `teacher_session_unpaid_students_cubit.dart` (kind = unpaid)_
**Reached from:** Session Hub (from Home) → "Absent students" / "Unpaid students" card → "View all".

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| `kind: absent` — screen opens / search box / infinite scroll | `GET api/Attendance/sessions/{sessionId}/absences` (Attendance chapter) | Query: `MinConsecutiveAbsences:1`, `Page`, `PageSize:20`, `Search?` (trimmed) | paginated `{teacherStudentId, studentName, studentCode, consecutiveAbsences}` — server pre-filters to an active absence streak and sorts worst-first |
| `kind: unpaid` — screen opens / month chip tap / infinite scroll | `GET api/Payment/unpaid` (Payments chapter) | Query: `SessionId`, `PaymentType:"Monthly"`, `MinConsecutiveUnpaid:1`, `AsOfMonth` (`"YYYY-MM"` — the tapped month chip), `Page`, `PageSize:10` | paginated student rows: `teacherStudentId, studentName, studentCode, sessionName, sessionId, profileImageUrl, outstandingAmount, consecutiveUnpaidPeriods, totalUnpaidPeriods, isPartialUnpaid` (badge shows `consecutiveUnpaidPeriods` if > 0, else `totalUnpaidPeriods`) |

## Create Session / Create Group success screens
_Dart files: `teacher_session_create_success_view.dart`, `teacher_group_create_success_view.dart`_

No API — static confirmation screens (`ExamSuccessScreen`) with a single "Continue" button that
pops back to the caller.

---

### Endpoint coverage

```
POST   api/session
GET    api/session/{teacherId}/sessions
GET    api/session/{teacherId}/sessions/{sessionId}
PUT    api/session/{teacherId}/sessions/{sessionId}
DELETE api/session/{teacherId}/sessions/{sessionId}
GET    api/session/{teacherId}/sessions/{sessionId}/delete-confirmation
POST   api/session/{teacherId}/sessions/{sessionId}/duplicate
POST   api/session/groups
GET    api/session/{teacherId}/groups
PUT    api/session/{teacherId}/groups/{groupId}
DELETE api/session/{teacherId}/groups/{groupId}
POST   api/session/links
DELETE api/session/{teacherId}/links/{sessionIdA}/{sessionIdB}
POST   api/session/assign-students
POST   api/session/{teacherId}/sessions/{sessionId}/confirm-reassign
DELETE api/session/{teacherId}/sessions/{sessionId}/students/{studentId}
```

Also called from this folder, but documented in other chapters:
```
GET api/teacherstudent/students            (chapter 02)
GET api/teacherstudent/assignment-chips    (chapter 02)
GET api/Attendance/sessions/{id}/absences  (Attendance)
GET api/Payment/unpaid                     (Payments)
```

All `webPathSession*`/`webPathTeacherSession*` constants defined in `web_constant.dart` are used
somewhere in this folder — none are defined-but-unused.
