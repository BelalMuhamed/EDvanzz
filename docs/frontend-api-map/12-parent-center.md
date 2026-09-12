# 12 — Parent App & Center Account

Covers two account types that live outside the teacher/student pair: **Parent**
(`lib/feature/parent_module/`, ~18 files) and **Center** / **CenterAssistant**
(`lib/feature/center_module/`, ~52 files).

**Parent app — mostly a UI shell today.** Of the screens under `parent_module`,
only two things actually reach the backend: the payment-tracking screen (real,
fully wired) and the language-preference save on the account/settings flow (real,
one field). Everything else — the parent home tab, "Add Child" (QR + manual),
"Manage Children" and its edit-single-child form, and every stat on the
teacher-details screen except the two navigation links — renders **hardcoded
literal data** with no `fromJson`/`toJson` model and no call site. This is called
out explicitly per screen below rather than assumed; do not infer a parent-facing
contract from any of those screens. There is **no parent attendance screen or
call anywhere in the app** (the backend's `ParentAttendanceController` mirror
exists per the backend's own CLAUDE.md, but the Flutter client never calls it —
see "Endpoint coverage"). The legacy 3-credential `TeacherCode`/`StudentCode`/
`HashedToken` link flow (still used by the *actual* Parent-Method-B linking on the
backend) is not implemented on the parent side of this client either — the
"Add Child" dialog's manual form only asks for name/DOB/grade and its submit
callback is never wired to anything.

**Center account — fully wired.** A Center owns several teachers and "acts as"
one at a time by setting `X-Acting-Teacher-Id` on outgoing requests
(`lib/core/network_services/acting_teacher_header_interceptor.dart`). Once acting,
the app pushes the **existing, unmodified `teacher_module` shell**
(`TeacherNavBarView`, wrapped by `CenterActingTeacherShellView`) — every
attendance/payment/video/exam call made from inside that shell is the identical
teacher-module call documented in the other chapters, just carrying the extra
header so the backend authorizes it against the acting teacher instead of a
teacher's own JWT. This chapter documents the **center's own resource endpoints**
(`api/center/*`) plus the exact hand-off points into the teacher shell; it does
not re-document teacher_module's internals.

All calls use `ApiService.client(requireAuth: true)` and the standard
`{success, message, data}` envelope (`ApiEnvelope.fromJson` — note: no `code`
field is parsed anywhere in `api_envelope.dart`; failures surface only as
`message`). **No screen in either module branches on a specific envelope error
code** — every failure path in `center_module`/`parent_module` just shows
`failure.erorrMsg` as a generic toast. The one special-cased failure signal is
NOT an envelope code at all: `ActingTeacherUnavailableInterceptor` watches for a
plain **HTTP 403** on any request that carried `X-Acting-Teacher-Id` (debounced
3s) and, on match, clears the acting-teacher selection and bounces the operator
back to Center Home with a message — this is how the app notices the acting
teacher was deactivated/removed mid-session.

**`X-Acting-Teacher-Id` — when it is and isn't attached.** The header is added by
`ActingTeacherHeaderInterceptor` to *every* outgoing request whenever (a) the
signed-in account is `Center`/`CenterAssistant` AND (b) `LocalStorage` has a
stored acting-teacher id. `CenterContextCubit.clearActingTeacher()` runs every
time navigation returns to Center Home (`AppRoute.goToCenterHomeForAccountType`)
or the front-desk scan screen via its scan-mode "back to Center" path
(`goToCenterHomeOrScan`) — so by the time the operator is back on any
`api/center/*` management screen (Teachers, Assistants, Revenue, Subscription,
Settings, Front-desk scan's own resolve/today/schedule calls) the stored id is
already cleared and **none of the `api/center/*` calls documented in this chapter
carry the header**. The header is only present on calls made **while inside the
acting-as teacher shell** — i.e., ordinary teacher_module endpoints, not
`api/center/*` — which is why it is called out per-flow below (student profile
open, attendance mark, payment collect) rather than on the `api/center/*` table
rows.

---

## Parent Home
_Dart file: `lib/feature/parent_module/home/view/parent_home_tab_view.dart`_
**Reached from:** the Home tab of the parent's bottom nav (`NavBarView(isParentMode: true)`, the shell a `UserRole.parent` login lands on via `AppRoute.goToHomeForAuthUser` → `goToNavBarScreen`).

**No API calls at all.** The header name (`"Dina Helal"`), the children chips
(`ChildChipData(name: 'Essam Ayman', ...)`, `'Khaled Ezz'`), the `hasChildren = true`
constant, the "3 enrolled" count, and the three `TeacherCardData` rows (with
`imageUrl: ''`) are all literal values in the widget tree. Tapping a teacher card
does navigate for real — `onTeacherTap` → `AppRoute.goToTeacherHome(teacherName:
data.name)` → pushes **Teacher details (parent view)** below — but the card data
itself is fake, so in practice this always opens the details screen for
`"Mr. Ahmed El-hady"` regardless of what a real backend would return. The "+" add
button opens the **Add Child / Add Teacher menu** (below); `ParentHomeEmptyStateWidget`
(the "no children yet" illustration + CTA) exists but is unreachable since
`hasChildren` is hardcoded `true`.

---

## Add Child / Add Teacher menu
_Dart files: `lib/feature/parent_module/home/view/widgets/{add_menu_items_list,add_child_modal,add_child_dialog_content,add_child_manual_form,add_child_qr_scanner}.dart`_
**Reached from:** the "+" circle on **Parent Home**'s children-selector row (`ParentHomeChildrenSelectorWidget`, via a `popover`).

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| "Add teacher" menu item | *(no call)* | — | opens the student-module `AddTeacherDialogContent` (teacherCode/studentCode/hash form) but its `onSubmit` callback body is a bare comment (`// Handle the submission logic here, e.g., call a Cubit method`) — nothing is ever sent |
| "Add child" menu item | *(no call)* | — | opens `AddChildDialogContent`: a QR scanner view by default, with a "add manually instead" link into `AddChildManualForm` |
| QR scanner tab — code scanned | *(no call)* | — | `onCodeScanned` shows a "Coming soon" toast (`LocaleKeys.comingSoonGeneric`) |
| Manual form — Name / Date of birth / Grade fields → submit | *(no call)* | — | `showAddChildDialog(context)` is invoked with no `onSubmit` argument anywhere in the app, so `AddChildManualForm`'s optional `onSubmit` callback is always `null`; pressing submit just pops the dialog |

No wire contract exists for this flow — there is no `ChildLinkParams`-style model,
no `toJson`, and no repository method anywhere under `parent_module`. If a real
"link child to teacher" flow is needed, it must be built from scratch (the backend
equivalent is presumably the same request/approval linking pattern used for
students, or the legacy 3-credential Parent Method B flow the backend's own
CLAUDE.md still documents as unmigrated — neither is called from here today).

---

## Manage Children
_Dart file: `lib/feature/parent_module/account/view/parent_edit_child_info_view.dart`_
**Reached from:** Account tab → "Manage Children" (parent-only row, `AppRoute.goToParentEditChildInfo`) and Account tab → "Edit Info" → "Edit Child Info" tile.

**No API calls.** The two children shown (`ChildInfoData(name: 'Essam Ayman', age:
'11', grade: '7', dateOfBirth: '2014-05-12', ...)` and `'Khaled Ezz'`) are a
`const` list literal in `build()`. Tapping a card pushes **Edit single child**
with those literal values passed as constructor params.

---

## Edit single child
_Dart file: `lib/feature/parent_module/account/view/parent_edit_single_child_info_view.dart`_
**Reached from:** tapping a card on **Manage Children**.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Name / Date of birth / Grade fields (DOB and Grade are `readOnly: true` with a no-op `onTap`) | *(no call)* | — | fields are pre-filled from the constructor args only |
| "Update Profile" button | *(no call — mocked)* | — | `_submit()` does `await Future<void>.delayed(const Duration(milliseconds: 400))`, then unconditionally shows a success toast and pops. No request is ever sent, and no failure path exists. |

---

## Teacher details (parent view)
_Dart file: `lib/feature/parent_module/teacher/view/widgets/parent_teacher_details_view_body.dart`_
**Reached from:** tapping a teacher card on **Parent Home**. Rendered by the shared `student_module` screen `teacher_details_view.dart`, which branches to this parent body when `NavBarView.runtimeIsParentMode == true` (and to the real student teacher-details body otherwise).

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| "Learning Plan" video stats (Total/Seen/Unseen videos) | *(no call)* | — | literal values (`48`, `40`, `15`) |
| "View Unseen Videos" link | *(navigation only, real screen)* | — | if `teacherId` is non-null: `AppRoute.goToVideoUnitsList(teacherId, teacherName)` opens the **same `VideoUnitsListScreen` the student module uses** (`GET api/videos/student/teachers/{teacherId}/units` — see chapter 10). It takes no `childId`/parent parameter, so it authorizes/scopes purely off the signed-in JWT exactly as it does for a student account; there is no visible mechanism here that scopes it to a specific child. If `teacherId` is null, shows a "Coming soon" toast instead. |
| Exams grid (Average grade / Total exams / Highest / Lowest) | *(no call)* | — | literal values (`86%`, `20`, `48`, `35`) |
| Homework grid (Total / Upcoming / Not done / Done) | *(no call)* | — | literal values (`8`, `3`, `6`, `2`) |
| "Payment Tracking" link | *(navigation only, real screen)* | — | if BOTH `teacherId` and `childId` are non-null: `AppRoute.goToPayment(teacherId, childId, isParentViewer: true)` → **Parent payment tracking** below. If either is null: "Coming soon" toast. |

---

## Parent payment tracking
_Dart file: `lib/feature/student_module/payment/view/student_payment_tracking_view.dart`_ (shared with the student module; driven by `StudentPaymentTrackingCubit(isParentViewer: true)`)
**Reached from:** "Payment Tracking" on **Teacher details (parent view)**, only when the caller supplied both a `teacherId` and `childId`.

This is the one fully real, non-trivial parent-facing contract in the app.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen open / pull-to-refresh | `GET api/payment/parent/children/{childId}/teachers/{teacherId}/tracking` | path params only — no query string | `{teacherId, currentSessionId?, currentSessionName?, paidProgressRatio, upcomingPayment?, paidSection, overdueSection}` — session name banner, an "upcoming" row, an expandable **Paid** section (progress bar + `periods[]`) and an expandable **Overdue** section (progress bar + `periods[]`) |

If `childId` is missing, the cubit never calls the endpoint at all and the screen
renders `LocaleKeys.studentPaymentParentIdsMissing` instead (`state.missingRequiredIds`).

```json
// GET api/payment/parent/children/847/teachers/14/tracking → data
{
  "teacherId": 14,
  "currentSessionId": 228,
  "currentSessionName": "Grade 9 — Sunday/Tuesday",
  "paidProgressRatio": 0.75,
  "upcomingPayment": {
    "periodId": 5501,
    "periodStartDate": "2026-10-01",
    "sessionName": "Grade 9 — Sunday/Tuesday",
    "periodType": "Monthly",
    "status": "Unpaid",
    "amountDue": 300,
    "amountPaid": 0,
    "outstandingAmount": 300,
    "isProRated": false,
    "isCarriedForward": false
  },
  "paidSection": {
    "totalAmount": 900,
    "periodCount": 3,
    "periods": [
      {
        "periodId": 5498,
        "periodStartDate": "2026-09-01",
        "sessionName": "Grade 9 — Sunday/Tuesday",
        "periodType": "Monthly",
        "status": "Paid",
        "amountDue": 300,
        "amountPaid": 300,
        "outstandingAmount": 0,
        "paidOnDate": "2026-09-03T00:00:00",
        "isProRated": false,
        "isCarriedForward": false
      }
    ]
  },
  "overdueSection": {
    "totalAmount": 300,
    "periodCount": 1,
    "periods": [
      {
        "periodId": 5490,
        "periodStartDate": "2026-08-01",
        "sessionName": "Grade 9 — Sunday/Tuesday",
        "periodType": "Monthly",
        "status": "Unpaid",
        "amountDue": 300,
        "amountPaid": 0,
        "outstandingAmount": 300,
        "monthsOverdue": 1,
        "isProRated": false,
        "isCarriedForward": false
      }
    ]
  }
}
```

`periodType` ∈ `Monthly`/`PerSession` (else parsed to an internal `unknown`);
`status` ∈ `Unpaid`/`PartiallyPaid`/`Paid`/`Overpaid`. `periodStartDate` is a
calendar day (`parseApiCalendarDate`); `paidOnDate` is read as the teacher's LOCAL
wall-clock (`parseApiLocalDateTime` — this is `PaymentTransaction.LocalCollectedAt`,
never re-localized). The identical DTO/parsing is shared with the student's own
`GET api/payment/student/teachers/{teacherId}/tracking` (chapter 10-adjacent) —
`StudentPaymentTrackingApiDto.fromJson` has no branch on which endpoint produced it.

---

## Parent account settings (language + profile)
_Dart files: `lib/core/shared_widgets/account/view/{account_tab_view,edit_profile_view,edit_info_view}.dart`_ (shared with student; parent-specific behavior noted)
**Reached from:** Account tab (bottom nav) → "Edit Info" (`AppRoute.goToEditProfile(isParentMode: true)`) → "Edit Profile" tile → `EditInfoView`.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Full name field | *(no call for a parent)* | — | `EditInfoView._canEditName` is `true` **only** for a teacher (non-assistant) account. For a parent the name field renders but is not editable/saveable — the save button's underlying repository call (`AccountLanguageRepository.updateTeacherFullName`) hard-throws for any non-teacher account type, so this path never fires for a parent. |
| Language preference change (via the shared language picker reached from Settings) | `PUT api/ParentUser/{parentUserId}/profile` | `{"languagePreference": "ar"}` — `parentUserId` is `user.accountId` from the signed-in JWT-derived `AuthUser`, never a route/typed value | void (envelope success only; on failure the toast shows `failure.erorrMsg`) |

There is **no GET** of the parent's own profile anywhere in the client — only this
one PUT, and it always sends `languagePreference` alone (no `fullName` or other
field). `AccountTabView`'s "Manage Children" row (parent-only) and "Edit Info" →
"Edit Child Info" tile both point at the dummy **Manage Children** screen above.

```json
// PUT api/ParentUser/193/profile
{ "languagePreference": "ar" }
```

---

## Center Home (owner)
_Dart file: `lib/feature/center_module/home/view/center_home_view.dart`_ (`CenterHomeCubit`)
**Reached from:** login landing screen for a `Center` account (`AppRoute.goToHomeForAuthUser` → `goToCenterHomeForAccountType`, which also clears any stored acting-teacher id first).

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen open / pull-to-refresh | `GET api/center/overview` **and** `GET api/center/teachers` (fired together; overview/teacher failures block the screen), plus `GET api/center/revenue?month=YYYY-MM` (best-effort — a failure here never blocks the rest) | overview/teachers: no params; revenue: `month` = current calendar month, `yyyy-MM` | hero card (`name`, `teacherCount`, `studentCount`, `hasActiveSubscription`), plan-count chips (`fullTeacherCount`/`managerialTeacherCount`/`managerialPlusTeacherCount`), quota meters (`teacherSlotsTotal` vs `teacherCount`, `studentCapacityTotal` vs `studentCount`), revenue strip (`totalCollected`, `totalCutOnCollected`) when the revenue call succeeded |
| Teacher search box (shown only past 6 teachers) | *(client-side filter, no call)* | — | filters the already-fetched `teachers` list by `fullName`/`teacherCode` (Arabic-normalized) |
| Tap a teacher card | *(no call — sets local acting-teacher state)* | — | `centerContextCubit.setActingTeacher(teacherId, teacherName, teacherCode)` then pushes **Center Acting-Teacher Shell**; blocked client-side with a toast if `!teacher.isActive` |
| "Add teacher" (empty-state CTA) | *(navigation only)* | — | opens **Center Teachers — create form** directly, then reloads Home on return |
| Quick links row: Teachers / Revenue / Subscription / Assistants / Scan / Attendance / Payment | *(navigation only)* | — | Teachers → **Center Teachers management**; Revenue → **Center Revenue report**; Subscription → **Center Subscription**; Assistants → **Center Assistants management**; Scan/Attendance/Payment → **Center Front-desk scan** (last two pre-select the `attendance`/`payment` intent) |
| Settings gear (app bar) | *(navigation only)* | — | opens **Center Settings** |

```json
// GET api/center/overview → data
{
  "centerId": 7,
  "name": "Nour Learning Center",
  "centerCode": "NLC-007",
  "defaultRevenueSharePercent": 20,
  "teacherCount": 5,
  "fullTeacherCount": 3,
  "managerialTeacherCount": 1,
  "managerialPlusTeacherCount": 1,
  "studentCount": 240,
  "hasActiveSubscription": true,
  "fullTeacherSlots": 5,
  "managerialTeacherSlots": 2,
  "managerialPlusTeacherSlots": 1,
  "studentCapacityTotal": 300,
  "studentCapacityUnderFull": 200,
  "studentCapacityUnderManagerial": 60,
  "studentCapacityUnderManagerialPlus": 40,
  "subscriptionEndDate": "2026-12-01"
}
```

```json
// one row of GET api/center/teachers → data[]
{
  "teacherId": 14,
  "fullName": "Ahmed El-Hady",
  "teacherCode": "AEH-014",
  "planType": "Full",
  "studentCapacity": 100,
  "effectiveRevenueSharePercent": 20,
  "revenueSharePercentOverride": null,
  "effectiveStudentCodeMode": "Auto",
  "studentCodeModeOverride": null,
  "accountStatus": "Active",
  "studentCount": 63,
  "loginEnabled": true,
  "loginUsername": "ahmed.center"
}
```

---

## Center Home (assistant)
_Dart file: `lib/feature/center_module/home/view/center_assistant_home_view.dart`_ (reuses `CenterTeachersCubit`)
**Reached from:** login landing screen for a `CenterAssistant` account.

Deliberately narrower than the owner's Home — a center assistant has no owner
permissions and the backend 403s `overview`/`revenue`/teacher-CRUD/subscription/
settings for this account type, so this screen never calls any of those.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen open / pull-to-refresh | `GET api/center/teachers` | — | same `CenterTeacherListItem[]` shape as above, but each card is rendered with `showFinancials: false` (no revenue-share/plan figures shown to an assistant) |
| Search box | *(client-side filter, no call)* | — | filters by name/code |
| Tap a teacher card | *(no call)* | — | same "acts as" hand-off as the owner's Home; a deactivated teacher shows a toast instead (assistants cannot reactivate) |
| Scan banner + Attendance/Payment chips | *(navigation only)* | — | all three open **Center Front-desk scan** with the matching intent |

---

## Center Front-desk scan
_Dart file: `lib/feature/center_module/scan/view/center_front_desk_scan_view.dart`_ (`CenterScanCubit`, `CenterScheduleSessionsCubit`)
**Reached from:** Center Home / Assistant Home quick links or scan banner (`AppRoute.goToCenterFrontDeskScan`), optionally pre-selecting an intent.

A 3-way segmented control — **Find student** / **Attendance** / **Payment** —
switches mode without leaving the screen or dropping the camera.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| **Find student** / **Payment** mode: QR scan or manual code + Submit | `GET api/center/students/resolve` | query `code` (trimmed scanned/typed text) | array of matches: `{teacherId, teacherName, teacherCode, teacherStudentId, studentName, studentCode, studentPhoneNumber?, sessionId?, sessionName?, todaySessionOccurrenceId?}` — 0 matches → inline "not found"; 1 → straight to hand-off; 2+ → `CenterScanMatchPickerSheet` disambiguation (each row shows the owning teacher's name) |
| **Find student** — after resolve | *(sets acting teacher locally, then real navigation)* | — | `centerContextCubit.setActingTeacher(...)`, shows a toast naming the owning teacher, pushes **Center Acting-Teacher Shell** (fire-and-forget) then `AppRoute.goToTeacherStudentProfile(studentId: teacherStudentId, ...)` — the profile screen itself is the teacher_module `TeacherStudentProfileScreen` (out of this chapter's scope; likely `GET api/teacherstudent/{id}`), and **this profile-open request carries `X-Acting-Teacher-Id`** since it fires after the acting teacher is set |
| **Payment** mode — after resolve | *(confirm sheet, then real navigation)* | — | `CenterScanStudentConfirmSheet` shows student name/code + owning teacher; on confirm, `launchSingleStudentCollect(studentCode: match.studentCode)` runs the teacher module's own single-student collect flow (`TeacherCollectPaymentCubit.lookupStudent` → `GET/POST api/v1/collect/lookup` / `api/v1/collect/submit`, documented in the payment chapter) — **these calls carry `X-Acting-Teacher-Id`** |
| **Attendance** mode — day/class picker | `GET api/center/sessions/schedules` | — (fetched once per screen open) | every ACTIVE session's recurrence across the center's active teachers: `{teacherId, teacherName, teacherCode, sessionId, sessionName, occurrenceType, startDate, endDate, startTime, durationMinutes, studentCount, isExpired, selectedDays?, monthlyDayOfMonth?}`. The app renders a teacher-home-style week strip (same recurrence mapper as the teacher's own home) and a teacher-name filter, then lists classes occurring on the selected day, grouped per teacher. |
| Tap a class card in Attendance mode | *(sets acting teacher, then real navigation)* | — | acts-as that class's teacher, then pushes the **existing** `TeacherAttendanceQrScanView(sessionId, sessionTitle)` (teacher module) for continuous scanning — **every mark from here on carries `X-Acting-Teacher-Id`** and is the ordinary `POST api/Attendance/mark` / `mark-bulk` flow documented in the attendance chapter |
| A scanned code the session roster can't resolve, while in Attendance mode | `GET api/center/students/resolve` (same endpoint, called again via the roster-miss hook) | query `code` | 0 matches → a plain "not found" dialog; 1+ matches → `CenterScanNotInClassSheet` lists every candidate (tagged "another teacher" or "another class" as appropriate), all rows disabled — informational only, physical attendance for another teacher's/session's student is never taken from here |

**Dead code note:** `CenterTodaySessionsCubit` / `GET api/center/sessions/today`
(`webPathCenterTodaySessions`) is fully implemented in the data layer
(`CenterRemoteDataSource.fetchTodaySessions`, `CenterRepository.fetchTodaySessions`,
`CenterTodaySessionsState`) but **no view ever instantiates the cubit** — the
Attendance-mode picker actually built into the screen uses
`CenterScheduleSessionsCubit` → `GET api/center/sessions/schedules` instead (see
above). Treat `sessions/today` as unused from the client's side.

```json
// GET api/center/students/resolve?code=A12 → data[]  (one match)
[
  {
    "teacherId": 14,
    "teacherName": "Ahmed El-Hady",
    "teacherCode": "AEH-014",
    "teacherStudentId": 5821,
    "studentName": "Malak Al-Araqy",
    "studentCode": "A12",
    "studentPhoneNumber": "01012345678",
    "sessionId": 228,
    "sessionName": "Grade 9 — Sunday/Tuesday",
    "todaySessionOccurrenceId": 90441
  }
]
```

```json
// one row of GET api/center/sessions/schedules → data[]
{
  "teacherId": 14,
  "teacherName": "Ahmed El-Hady",
  "teacherCode": "AEH-014",
  "sessionId": 228,
  "sessionName": "Grade 9 — Sunday/Tuesday",
  "occurrenceType": "Weekly",
  "startDate": "2026-09-01",
  "endDate": "2027-06-01",
  "startTime": "16:00:00",
  "durationMinutes": 90,
  "studentCount": 24,
  "isExpired": false,
  "selectedDays": [0, 2],
  "monthlyDayOfMonth": null
}
```

---

## Center Acting-Teacher Shell
_Dart files: `lib/feature/center_module/shell/view/{center_acting_teacher_shell_view,widgets/center_acting_teacher_switcher_sheet}.dart`_
**Reached from:** tapping a teacher card anywhere (Home, Teachers list, scan hand-off).

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Shell body | *(no call of its own)* | — | wraps the existing `TeacherNavBarView`, keyed by the acting teacher's id so switching teacher forces a full remount (every teacher_module cubit re-fetches fresh) |
| "Acting as: [Teacher ▾]" bar tap → switcher sheet | `GET api/center/teachers` (fetched fresh every time the sheet opens) | — | same `CenterTeacherListItem[]`; searchable by name/code; a `CenterAssistant` viewer never sees financial figures (`showFinancials` gated off the stored account type) |
| Pick a different teacher in the switcher | *(no call — local state only)* | — | `centerContextCubit.setActingTeacher(...)`; picking the already-current or an inactive teacher is disabled client-side |
| "Back to Center" / breadcrumb tap | *(no call — clears local state)* | — | `centerContextCubit.clearActingTeacher()`, then either pops to Center Home or (if this shell was opened from the scan screen) back down to the scan screen, staying in scan mode |

---

## Center Teachers management
_Dart files: `lib/feature/center_module/teachers/view/{center_teachers_list_view,center_teacher_form_view,widgets/center_teacher_login_dialogs}.dart`_
**Reached from:** Center Home quick link "Teachers", or the Assistants/Home teacher list itself.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen open / pull-to-refresh | `GET api/center/teachers` | — | list as above |
| Search box | *(client-side filter)* | — | — |
| "+" (add teacher) → form → Save | `POST api/center/teachers` | see JSON below | created `CenterTeacherListItem`, merged into the list live |
| Row ⋮ → "Edit" → form → Save | `PUT api/center/teachers/{teacherId}` | see JSON below | updated `CenterTeacherListItem` |
| Row ⋮ → "Deactivate" (confirm dialog) | `POST api/center/teachers/{teacherId}/deactivate` | — (no body) | void; row's `accountStatus` flips to `Inactive` locally; if this teacher was the current acting teacher, `CenterContextCubit.markTeacherUnavailableIfActing` also clears the acting selection |
| Row ⋮ → "Reactivate" (confirm dialog, inactive rows only) | `POST api/center/teachers/{teacherId}/activate` | — | void; row flips to `Active` |
| Row ⋮ → "Enable login" (active, no login yet) | `POST api/center/teachers/{teacherId}/enable-login` | `{"username": "...", "password": "..."}` (username ≥4 chars, password ≥8 chars, validated client-side) | updated `CenterTeacherListItem` with `loginEnabled: true`, `loginUsername` |
| Row ⋮ → "Reset password" (active, login enabled) | `POST api/center/teachers/{teacherId}/reset-password` | `{"newPassword": "...", "confirmPassword": "..."}` | void |
| Row ⋮ → "Disable login" (active, login enabled, confirm dialog) | `POST api/center/teachers/{teacherId}/disable-login` | — | void |
| Row tap (not the ⋮ menu) | *(no call — local state only)* | — | acts-as this teacher, same as Center Home |

```json
// POST api/center/teachers
{
  "fullName": "Sara Kamal",
  "subjectIds": [3, 7],
  "customSubject": "Advanced Chemistry",
  "languagePreference": "ar",
  "planType": "Managerial",
  "studentCapacity": 80,
  "revenueSharePercentOverride": 25,
  "studentCodeModeOverride": "Manual"
}
```
`customSubject`/`languagePreference`/`revenueSharePercentOverride`/
`studentCodeModeOverride` are omitted entirely when blank/unset (not sent as
`null`) — this is a create-only body, so "omitted" and "inherit the center
default" mean the same thing here.

```json
// PUT api/center/teachers/14
{
  "fullName": "Ahmed El-Hady",
  "planType": "Full",
  "studentCapacity": 100,
  "revenueSharePercentOverride": null,
  "studentCodeModeOverride": null
}
```
Unlike create, the two override fields are **always present**, including as an
explicit JSON `null` — that is the only way the UI can clear an override back to
"inherit the center default"; `subjectIds`/`customSubject`/`languagePreference`
are not part of this edit body at all (`fullName`/`planType`/`studentCapacity`
always have a definite value from the form, so nothing here is conditional).

`planType` wire values: `"Full"` / `"Managerial"` / `"ManagerialPlus"`.
`studentCodeModeOverride`/`effectiveStudentCodeMode` wire values: `"Auto"` /
`"Manual"`.

---

## Center Assistants management
_Dart files: `lib/feature/center_module/assistants/view/{center_assistants_list_view,center_assistant_form_view}.dart`_
**Reached from:** Center Home quick link "Assistants" (owner only — an assistant account never reaches this screen).

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen open / pull-to-refresh | `GET api/center/assistants` | — | `{centerAssistantId (→ id), fullName, username, email?, phoneNumber?, languagePreference?, isActive, accountStatus, createdAt}[]` |
| "+" → form → "Save" / "Save & add another" | `POST api/center/assistants` | see JSON below | void — list is reloaded (`force: true`) rather than merging the created row locally |
| Active/Inactive switch on a row (deactivate confirms first) | `POST api/center/assistants/{assistantId}/activate` or `.../deactivate` | — | void; toggled locally on success |
| Row ⋮ → "Reset password" (active only) | `POST api/center/assistants/{assistantId}/reset-password` | `{"newPassword": "...", "confirmPassword": "..."}` (same dialog/shape as the teacher reset) | void |

```json
// POST api/center/assistants
{
  "fullName": "Mona Farid",
  "username": "mona.frontdesk",
  "password": "Str0ngPass!",
  "phoneNumber": "01098765432",
  "email": "mona@example.com",
  "languagePreference": "ar"
}
```
`phoneNumber`/`email`/`languagePreference` are omitted when the field is blank.

---

## Center Revenue report
_Dart file: `lib/feature/center_module/revenue/view/center_revenue_view.dart`_
**Reached from:** Center Home quick link "Revenue".

| UI element | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen open + month prev/next arrows | `GET api/center/revenue` | query `month` = `yyyy-MM` of the selected month | `{month, defaultSharePercent, totalCollected, totalExpected, totalCutOnCollected, totalCutOnExpected, teachers[]}`; each teacher row: `{teacherId, teacherName, teacherCode, planType?, sharePercent, collected, expected, cutOnCollected, cutOnExpected}` |

```json
// GET api/center/revenue?month=2026-09 → data
{
  "month": "2026-09-01",
  "defaultSharePercent": 20,
  "totalCollected": 45000,
  "totalExpected": 52000,
  "totalCutOnCollected": 9000,
  "totalCutOnExpected": 10400,
  "teachers": [
    {
      "teacherId": 14,
      "teacherName": "Ahmed El-Hady",
      "teacherCode": "AEH-014",
      "planType": "Full",
      "sharePercent": 20,
      "collected": 18000,
      "expected": 19000,
      "cutOnCollected": 3600,
      "cutOnExpected": 3800
    }
  ]
}
```

---

## Center Subscription
_Dart file: `lib/feature/center_module/subscription/view/center_subscription_view.dart`_
**Reached from:** Center Home quick link "Subscription".

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen open / pull-to-refresh | `GET api/center/subscription` | — | status card (`hasSubscription`, `status` ∈ `Active`/`ExpiringSoon`/`Expired`/null, `startDate`, `endDate`, `daysRemaining`), usage rows (`used*`/`*Slots`/`studentCapacity*` for Full/Managerial/ManagerialPlus), `hasPendingRequest` + nested `pendingRequest`, `pendingRequestAmountEGP`, and `latestRequest` (most recent request in ANY status, so an approved/rejected outcome stays visible after leaving Pending) |
| 5-quota stepper form (Full/Managerial/ManagerialPlus teacher slots, total/under-Full/under-Managerial/under-ManagerialPlus student capacity) + note → "Submit request" (only shown when there's no pending request) | `POST api/center/subscription/request` | see JSON below | void; screen reloads to show the new `pendingRequest` |
| "Cancel request" (on a pending request, confirm dialog) | `DELETE api/center/subscription/request` | — (no body) | void; screen reloads |

```json
// POST api/center/subscription/request
{
  "fullTeacherSlots": 3,
  "managerialTeacherSlots": 1,
  "managerialPlusTeacherSlots": 1,
  "studentCapacityTotal": 250,
  "studentCapacityUnderFull": 150,
  "studentCapacityUnderManagerial": 60,
  "studentCapacityUnderManagerialPlus": 40,
  "note": "Growing to 5 teachers next term"
}
```
`note` is omitted when blank. The 5 capacity/slot numbers are NOT server-validated
client-side beyond a non-submittable "all slots zero" guard and a non-blocking
mismatch banner when the three per-plan capacities don't sum to the total — the
request still submits either way.

---

## Center Settings
_Dart file: `lib/feature/center_module/settings/view/center_settings_view.dart`_
**Reached from:** Center Home settings gear (owner only — `CenterSettingsView` is reached only for `accountType == Center`; a `CenterAssistant` falls through to the generic `SettingsView` instead, per the file's own doc comment).

Two independently-saved regions on one screen, both `PUT api/center/settings`
with a **partial body** (only the changed subset is ever sent — the endpoint is
additive-merge per field):

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen open / pull-to-refresh | `GET api/center/settings` | — | `{name, defaultRevenueSharePercent, studentCodeGenerationMode, configuration}` |
| Center name / Default revenue share % / Student-code mode card (Auto/Manual) → "Save" | `PUT api/center/settings` | `{"name": "...", "defaultRevenueSharePercent": 20, "studentCodeGenerationMode": "Auto"}` (revenue share validated client-side to 0–100 first) | updated `CenterSettings`; success toast |
| Any teacher-defaults toggle/radio/slider (session-name mode, QR display mode, prorated payment on/off + tier %, device lock, attendance-screen payment/absence-history toggles) | `PUT api/center/settings` (autosaved per change, optimistic — reverts locally on failure) | `{"configuration": { ...full TeacherConfiguration block... }}` — see keys below | updated `CenterSettings.configuration`; no success toast (silent autosave, unlike the business-section Save) |
| "Apply to all teachers" (confirm dialog) | `POST api/center/settings/apply-to-all-teachers` | — (no body) | `{"updatedTeacherCount": N}` → success toast "N teachers updated" |
| "Change password" / "Logout" rows | *(navigation to shared screens, out of scope)* | — | — |

`configuration` block keys (shared `TeacherConfigurationApiDto`, identical to the
teacher's own settings screen): `studentCapacityPackageId`, `studentCodeGenerationMode`
("Auto"/"Manual" — mirrors the top-level field but is not re-edited from this
sub-block in the UI), `sessionNameMode` ("Auto"/"Manual"), `isProratedPaymentEnabled`,
`prorationMethod`, `proratedTiers[]` (`{tierNumber, thresholdDayStart, thresholdDayEnd,
fractionRate}`), `consecutiveAbsenceThreshold`, `consecutiveUnpaidThreshold`,
`barcodeDisplayMode` ("InApp"/"HardCopyOnly" — wire values `inApp`/`hardCopyOnly`
mapped through `BarcodeDisplayModeApi`), `studentVisibilityAttendance/Payment/
Homework/ExamDefault`, `parentVisibilityAttendance/Payment/Homework/ExamDefault/
OnlineExamDefault`, `isDeviceLockEnabled`, `showPaymentInfoOnAttendanceScreen`,
`showAttendanceHistoryOnAttendanceScreen`, `parentPortalEnabled`. (`billingStartDate`/
`billingStartLocked` are read-only on this DTO and never sent back in `toJson`.)

```json
// PUT api/center/settings  (business section save)
{
  "name": "Nour Learning Center",
  "defaultRevenueSharePercent": 22.5,
  "studentCodeGenerationMode": "Auto"
}
```

```json
// PUT api/center/settings  (one autosaved teacher-defaults toggle)
{
  "configuration": {
    "studentCodeGenerationMode": "Auto",
    "sessionNameMode": "Auto",
    "isProratedPaymentEnabled": true,
    "prorationMethod": "ByPercentage",
    "proratedTiers": [
      { "tierNumber": 1, "thresholdDayStart": 1, "thresholdDayEnd": 10, "fractionRate": 1.0 },
      { "tierNumber": 2, "thresholdDayStart": 11, "thresholdDayEnd": 20, "fractionRate": 0.6667 },
      { "tierNumber": 3, "thresholdDayStart": 21, "thresholdDayEnd": 31, "fractionRate": 0.3333 }
    ],
    "consecutiveAbsenceThreshold": 3,
    "consecutiveUnpaidThreshold": 3,
    "barcodeDisplayMode": "inApp",
    "studentVisibilityAttendance": true,
    "studentVisibilityPayment": true,
    "studentVisibilityHomework": true,
    "studentVisibilityExamDefault": true,
    "parentVisibilityAttendance": true,
    "parentVisibilityPayment": true,
    "parentVisibilityHomework": true,
    "parentVisibilityExamDefault": true,
    "parentVisibilityOnlineExamDefault": false,
    "isDeviceLockEnabled": false,
    "showPaymentInfoOnAttendanceScreen": true,
    "showAttendanceHistoryOnAttendanceScreen": true,
    "parentPortalEnabled": false
  }
}
```

The "New teacher" form's subject picker (`TeacherSubjectSelectionWidget`, shared
with teacher_module) independently calls the anonymous `GET api/Teacher/subjects`
— not a center-specific endpoint, listed here only because it feeds
`CreateCenterTeacherParams.subjectIds`.

---

### Endpoint coverage

```
PUT    api/ParentUser/{parentUserId}/profile
GET    api/payment/parent/children/{childId}/teachers/{teacherId}/tracking

GET    api/center/overview
GET    api/center/teachers
POST   api/center/teachers
PUT    api/center/teachers/{teacherId}
POST   api/center/teachers/{teacherId}/deactivate
POST   api/center/teachers/{teacherId}/activate
POST   api/center/teachers/{teacherId}/enable-login
POST   api/center/teachers/{teacherId}/reset-password
POST   api/center/teachers/{teacherId}/disable-login
GET    api/center/students/resolve
GET    api/center/sessions/schedules
GET    api/center/revenue
GET    api/center/subscription
POST   api/center/subscription/request
DELETE api/center/subscription/request
GET    api/center/assistants
POST   api/center/assistants
POST   api/center/assistants/{assistantId}/activate
POST   api/center/assistants/{assistantId}/deactivate
POST   api/center/assistants/{assistantId}/reset-password
GET    api/center/settings
PUT    api/center/settings
POST   api/center/settings/apply-to-all-teachers
```

**Defined in `web_constant.dart` / wired in the data source, but never called from
any cubit/view:**
- `GET api/center/sessions/today` (`webPathCenterTodaySessions`) — `CenterRemoteDataSource.fetchTodaySessions`, `CenterRepository.fetchTodaySessions`, and `CenterTodaySessionsCubit`/`CenterTodaySessionsState` all exist, but no screen builds that cubit. The front-desk scan's Attendance mode uses `GET api/center/sessions/schedules` (`CenterScheduleSessionsCubit`) instead.

**Reused teacher_module / student_module endpoints, hit only via the acting-as
hand-off or a shared screen (documented in their own chapters, not re-detailed
here):** `GET api/teacherstudent/{id}`-style student profile fetch (opened from
Find-student scan), `POST api/Attendance/mark` / `mark-bulk` (opened from
Attendance scan), `GET/POST api/v1/collect/lookup` / `api/v1/collect/submit`
(opened from Payment scan), `GET api/videos/student/teachers/{teacherId}/units`
(parent's "View Unseen Videos"), `GET api/payment/student/teachers/{teacherId}/tracking`
(same DTO as parent tracking, different caller), `GET api/Teacher/subjects`
(anonymous, feeds the center's create-teacher form).

**Not called at all, anywhere in the client, despite existing on the backend per
the backend's own CLAUDE.md:** any `ParentAttendanceController`-equivalent route —
there is no parent attendance screen, cubit, or repository method in this codebase.
The legacy 3-credential `TeacherCode`/`StudentCode`/`HashedToken` link-child flow
is likewise not implemented on the parent side of this client (only referenced,
unwired, on the "Add teacher" stub reused from student_module).
