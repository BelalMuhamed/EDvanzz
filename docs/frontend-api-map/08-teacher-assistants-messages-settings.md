# 08 — Teacher: Assistants, Messages, Settings, Audit & Independence

Source: `lib/feature/teacher_module/assistant/**` (~32 files), `lib/feature/teacher_module/messages/**`
(~35 files), `lib/feature/teacher_module/settings/**` (~23 files),
`lib/feature/teacher_module/audit_trail/**` (~17 files), `lib/feature/teacher_module/independence/**`.
Endpoint constants: `lib/core/network_services/web_constant.dart` (assistants ~lines 139–155, audit
trail ~157–159, teacher configuration/profile/capacity-packages ~379–388, independence ~622–625).
Data flow: `*RemoteDataSource` (Dio) → `*RepositoryImpl` → cubits → views, same as every other
chapter. Responses are read through the standard envelope `{success, message, data}` — **this
family of modules has no `code` field in the envelope at all** (see `ApiEnvelope.fromJson`, which
only reads `success`/`isSuccess`, `message`, `data`); every failure surfaces as the backend's
already-localized `message` string in a generic error toast. List endpoints return the standard
paginated shape unless noted.

**Headline finding — the entire Messages module (`lib/feature/teacher_module/messages/**`) is
mock-only.** `TeacherMessagesRepository` is bound in DI to `TeacherMessagesRepositoryMock`
(`service_locator.dart`), which returns canned data from `TeacherMessagesMockData` with no network
calls whatsoever — confirmed by a repo-wide grep for `ApiService`/`Dio`/`webPath` inside that folder
returning zero hits. The enums file even says so in its own doc comment: *"Teacher Messages domain
enums (mock-first; no OpenAPI yet)."* Every screen in that section below is documented as UI/data
shape only — there is nothing for a backend developer to reconcile against today, only a contract to
build toward. The whole module is also gated behind `AppConfig.showMessaging` (`!isMvp`) — on an MVP
build the side-menu entry and every `goToTeacherMessage*` route no-op.

**Second finding — the Audit Trail screen is currently unreachable from any navigation point.**
`AppRoute.goToTeacherAuditTrail()` and `TeacherAuditTrailView` exist, are fully wired to real
endpoints, and work — but a repo-wide grep found no caller of `goToTeacherAuditTrail` anywhere
outside `app_route.dart` itself (no side-menu row, no settings row, nothing). The screen and its
export are real and functional; they are just not currently linked from anywhere a teacher can tap.

---

## Assistants (`lib/feature/teacher_module/assistant/`)

Repository: `TeacherAssistantRepositoryImpl` → `TeacherAssistantRemoteDataSource`. All eight methods
on `TeacherAssistantRepository` map 1:1 to the endpoints below — there is no hidden ninth call.

### Assistant Accounts (list)
_Dart file: `lib/feature/teacher_module/assistant/view/teacher_assistant_accounts_view.dart`_
_Cubit: `TeacherAssistantAccountsCubit`_
**Reached from:** side menu → `AppRoute.goToTeacherAssistantAccounts`.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load / pull-to-refresh / infinite scroll | `GET api/assistant` | Query (`TeacherAssistantListQuery.toQueryParameters`): `teacherId`, `sortBy` (`"fullName"` \| `"CreatedAt"`, default `CreatedAt`), `sortDirection` (`"Asc"` \| `"Desc"`, default `Desc`), `Page`, `PageSize` (10). The model also supports `isAcitve` *(sic — wire spelling, not a typo to fix)*, `fullName`, `username` filters, but **no UI in this screen sets them** — they are always omitted | Per item (`TeacherAssistantApiDto.fromJsonList`): `id`, `fullName`, `username`, `teacherId`, `teacherName`, `isActive`, `accountStatus` (`"Active"`\|`"Inactive"`\|`"Suspended"`, default `Active` if absent), `createdAt`; paginated envelope `totalCount`, `hasMore` |
| Tap a row (opens Assistant Detail) | *(navigates; detail screen re-fetches)* | — | — |
| ⋮ / row action → "Activate" | `PATCH api/assistant/{assistantId}/activate` | — (no body) | void envelope — row optimistically flips to Active before the call, rolled back on failure |
| ⋮ / row action → "Deactivate" → confirm dialog | `PATCH api/assistant/{assistantId}/deactivate` | — (no body) | void envelope — row optimistically flips to Inactive, rolled back on failure |
| ⋮ / row action → "Delete" → confirm dialog | `PATCH api/assistant/{assistantId}/delete` | — (no body) | void envelope — row is optimistically removed from the list and `totalCount` decremented, rolled back on failure. Note the HTTP verb: this is a **PATCH to a `/delete` sub-path**, not `DELETE api/assistant/{id}` — the app calls it "suspend" internally (`suspendAssistant`) |
| "Create another assistant account" / "Create assistant account" (empty state) button | *(navigates to Create Assistant)* | — | on return, list is force-reloaded |

### Create Assistant
_Dart file: `lib/feature/teacher_module/assistant/view/teacher_create_assistant_view.dart`_
_Cubit: `TeacherCreateAssistantCubit`_
**Reached from:** Assistant Accounts list → "Create assistant account" button.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load (permission catalogue) | `GET api/Permission/teacher` | — | Array of modules: `id` → `moduleId`, `ModuleName`/`moduleName` (both keys checked, `ModuleName` wins) → `moduleName`, `permissions[]` each `{permissionId, permissionName, isRestricted}` (`isRestricted` preferred over a fallback `isSensitive` key) |
| Full Name, Nickname (username), Password fields + permission picker → "Submit" | `POST api/assistant` | `{teacherId, fullName, username, password, email?, phoneNumber?, permissionProfileIds?: [int], permissionIds: [int]}` — `email`/`phoneNumber` omitted if blank; `permissionProfileIds` omitted if empty (no UI sets it in this screen — always empty); `permissionIds` is the flat set of selected permission ids across every module (client-validated non-empty before the call — the app also treats an empty selection as `teacherAssistantPermissionsRequired` without ever calling the API) | void envelope — success pops the screen with `true`, triggering a list reload |

Non-trivial request body:
```json
{
  "teacherId": 42,
  "fullName": "Sara Ahmed",
  "username": "sara.a",
  "password": "Str0ngPass!",
  "permissionIds": [101, 102, 118, 130]
}
```

### Assistant Detail
_Dart file: `lib/feature/teacher_module/assistant/view/teacher_assistant_detail_view.dart`_
_Cubit: `TeacherAssistantDetailCubit`_
**Reached from:** Assistant Accounts list → tap a row.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load | `GET api/assistant/{assistantId}` | — | `id`, `fullName`, `username`, `email`, `phoneNumber`, `teacherId`, `teacherName`, `accountStatus`, `createdAt`, `deletedAt`, `languagePreference`, `assignedPermissionsProfiles[]` (each `{profileId, profileName}`), `userPermissions[]` (same module/permission shape as the catalogue call above — this is what the teacher is actually granted, vs. the catalogue of what's available) |
| "Login Activity" button | *(navigates to Login Activity)* | — | — |
| "Edit" button | *(navigates to Edit Assistant)* | — | on return with `true`, detail is force-reloaded |

### Edit Assistant
_Dart file: `lib/feature/teacher_module/assistant/view/teacher_assistant_edit_view.dart`_
_Cubit: `TeacherAssistantEditCubit`_
**Reached from:** Assistant Detail → "Edit" button.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load | `GET api/assistant/{assistantId}` (same detail call as above) + `GET api/Permission/teacher` (best-effort — a failure here still leaves name/username/password editable, just with an empty permission picker) | — | pre-fills Full Name / Nickname text fields and the permission picker's selected-id set from `userPermissions` |
| Full Name, Nickname, (optional) New Password fields + permission picker → "Submit" | `PUT api/assistant/{assistantId}` | `{fullName, username, newPassword?, phoneNumber?, email?, permissionProfileIds?: [int], permissionIds?: [int]}` — `newPassword` omitted if the field is left blank; client-side guard requires at least one of `permissionIds`/`permissionProfileIds` non-empty before calling (mirrors the backend's own "must keep at least one permission or profile" rule) | void envelope — success pops with `true` |

### Assistant Login Activity
_Dart file: `lib/feature/teacher_module/assistant/view/teacher_assistant_login_activity_view.dart`_
_Cubit: `TeacherAssistantLoginActivityCubit`_
**Reached from:** Assistant Detail → "Login Activity" button.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load / pull-to-refresh | `GET api/assistant/{assistantId}/login-activity` | — | Array (not paginated), each entry: `action`, `occurredAt`/`timestamp` (either key read), `deviceOrBrowser`/`userAgent` (either key), `ipAddress` |

### Permission module/action key structure (`GET api/Permission/teacher`)

The catalogue and the "what this assistant currently has" list share one shape — a flat array of
modules, each carrying its own list of individually-grantable permissions:

```json
[
  {
    "id": 3,
    "ModuleName": "Attendance",
    "permissions": [
      { "permissionId": 101, "permissionName": "ViewHistory", "isRestricted": false },
      { "permissionId": 102, "permissionName": "MarkAttendance", "isRestricted": false },
      { "permissionId": 118, "permissionName": "EditPastRecords", "isRestricted": true }
    ]
  },
  {
    "id": 7,
    "ModuleName": "Payment",
    "permissions": [
      { "permissionId": 130, "permissionName": "Collect", "isRestricted": false },
      { "permissionId": 131, "permissionName": "ViewCollectorSummary", "isRestricted": false }
    ]
  }
]
```

- The module key is `id` + `ModuleName` (capital M; the client also tolerates a lowercase
  `moduleName`, falling back to it only if `ModuleName` is absent).
- Each permission is a leaf `{permissionId, permissionName, isRestricted}` — there is no nested
  action/resource structure beyond module → permission; `permissionName` strings mirror the backend's
  `ModulePermission("Module","Action")` pairs described in the repo's `CLAUDE.md` §7.1/§8 (e.g.
  Messaging's `SendManual`/`ViewHistory`), one flat entry per action.
  `isRestricted` (aliased client-side as `isSensitive`) marks a permission the UI could flag as
  higher-risk, though neither create nor edit screens currently render that distinction visually —
  it's carried through but unused.
- On create/update, the app **never sends the module structure back** — it flattens the selection to
  a bare `permissionIds: [int]` array (plus an always-empty `permissionProfileIds` array reserved for
  a "permission profile" preset feature with no UI surface in this app build).
- `assignedPermissionsProfiles` (on the detail response) is a separate, currently-unused-by-any-editor
  concept: named permission bundles (`{profileId, profileName}`) an assistant can be assigned, distinct
  from the individual `permissionIds`. The edit screen reads it (`extractAssistantProfileIds`) only to
  echo it back unchanged on save — there is no profile picker UI.

---

## Messages (`lib/feature/teacher_module/messages/`) — mock only, no backend calls

Every screen below reads from `TeacherMessagesRepositoryMock` (`teacher_messages_mock_data.dart`)
and, for create/delete, mutates an in-memory list with an artificial `Future.delayed` — there is no
`ApiService`, no `Dio`, no `webPath*` constant anywhere in this folder. The tables below record what
each screen sends/reads from the **mock repository interface** (`TeacherMessagesRepository`), since
that is the closest thing to a contract a backend implementation would need to satisfy.

### Messages Channels Hub
_Dart file: `lib/feature/teacher_module/messages/view/teacher_messages_channels_hub_view.dart`_
_Cubit: `TeacherMessagesChannelsHubCubit`_
**Reached from:** side menu → `AppRoute.goToTeacherMessagesChannels` (no-ops entirely when
`AppConfig.showMessaging` is false).

| UI element / action | "Endpoint" (mock repo call) | Sends | Uses from response |
|---|---|---|---|
| Screen load | `fetchHubStats()` + `fetchChannelStatuses()` (both synchronous, in-memory) | — | `sentToday`, `deliveryRatePercent`, `failedCount`, `pendingCount` stat tiles; channel connection cards (`type`: whatsapp/sms, `status`: connected/disconnected, optional `label`) |
| "Configure" on a channel card | *(navigates to Channel Setup)* | — | — |
| "View History" row | *(navigates to Message History)* | — | — |
| "Existing Templates" row | *(navigates to Templates List)* | — | — |
| "Existing Triggers" row | *(navigates to Triggers List)* | — | — |
| "Open Inbox" row | *(navigates to Inbox)* | — | — |
| ＋ FAB → "Create new template" | *(navigates to Template Create)* | — | — |
| ＋ FAB → "Create sent time" | *(navigates to a schedule-triggers shell, `TeacherMessageSentTimeView`)* | — | reads `fetchTriggers()` (mock) for display only |

### Channel Setup
_Dart file: `lib/feature/teacher_module/messages/view/teacher_message_channel_setup_view.dart`_ —
read-only shell. Tapping any channel row shows a "Coming soon" toast; no call of any kind, mock or
real.

### Message Templates List
_Dart file: `lib/feature/teacher_module/messages/view/teacher_message_templates_list_view.dart`_
_Cubit: `TeacherMessageTemplatesListCubit`_

| UI element / action | "Endpoint" (mock) | Sends | Uses from response |
|---|---|---|---|
| Screen load / refresh | `fetchTemplates()` | — | list of `{id, name, category, channels[], isScheduled, includesParent, bodyHtml, recipientCount}` |
| ⋮ on a card → confirm → delete | `deleteTemplate(id)` | template `id` (string) | mock removes it from the in-memory list after a 400ms delay |

### Create Message Template
_Dart file: `lib/feature/teacher_module/messages/view/teacher_message_template_create_view.dart`_
_Cubit: `TeacherMessageTemplateCreateCubit`_

This screen collects the fullest picture of what a real "send/create template" endpoint will
eventually need to accept: name, category, channels, an optional free-text schedule label,
recipients (Groups or Sessions — reuses the exam module's target-scope picker,
`TeacherExamTargetScopeType.groups`/`.sessions`), a set of insertable variable tokens, and an
HTML-lite body (500 char cap, with a bold/italic/etc. formatting toolbar shared with the exam
question editor).

| UI element / action | "Endpoint" (mock) | Sends | Uses from response |
|---|---|---|---|
| Name / Category dropdown / Channel toggles (WhatsApp, SMS) / Schedule (free text) / Groups-or-Sessions recipient picker / Variable chips / Body text / "Send to N recipients" submit | `sendTemplate(TeacherMessageTemplateFormInput)` | `{name, category, channels: [whatsapp\|sms], bodyHtml, recipientType: groups\|sessions, selectedGroupIds: [String], selectedSessionIds: [String], scheduleLabel?, variables: [studentName\|parentName\|sessionName\|dueDate\|paymentAmount\|customText]}` | mock inserts a new template into the in-memory list (`recipientCount` fabricated as `selectedIds.length * 12`) after an 800ms delay; screen pops `true` on success |

### Existing Triggers / Sent-Time Schedules
_Dart files: `teacher_message_triggers_list_view.dart` (list) and the `TeacherMessageSentTimeView`
class inside `teacher_message_channel_setup_view.dart` (create shell)._ Both simply call
`fetchTriggers()` and render `{id, name, templateName, scheduleLabel, channels[], isActive}` cards;
neither has a working create/edit/delete action — the "Sent Time" screen is a read-only preview list
with an explanatory hint only.

### Message History
_Dart file: `lib/feature/teacher_module/messages/view/teacher_message_history_view.dart`_
_Cubit: `TeacherMessageHistoryCubit`_

| UI element / action | "Endpoint" (mock) | Sends | Uses from response |
|---|---|---|---|
| Screen load / refresh | `fetchHistory()` | — | list of `{id, studentName, recipientNumber, parentNumber?, status: sent\|delivered\|failed\|pending, sentAtLabel, templateName?}` |

### Messages Inbox
_Dart file: `lib/feature/teacher_module/messages/view/teacher_messages_inbox_view.dart`_
_Cubit: `TeacherMessagesInboxCubit`_

| UI element / action | "Endpoint" (mock) | Sends | Uses from response |
|---|---|---|---|
| Screen load + filter chips (All / Students / Parents / Groups) | `fetchInboxThreads(TeacherMessageInboxFilter)` | selected filter enum | list of `MessageThreadItem` (the same shared thread model the app's other 1:1 messaging surfaces use) rendered via the shared `MessageThreadsSectionWidget` |
| Tap a thread | *(navigates to Message Chat)* | thread object (passed by value, not refetched) | — |

### Message Chat
_Dart file: `lib/feature/teacher_module/messages/view/teacher_message_chat_view.dart`_ — thin wrapper
around the shared `MessageConversationView(thread: thread)`; that shared widget also makes no
network call for this thread type (confirmed no `ApiService`/`webPath` references) — it renders
whatever `MessageThreadItem` was handed to it by the Inbox screen.

---

## Settings (`lib/feature/teacher_module/settings/`)

Repository: `TeacherSettingsRepositoryImpl` → `TeacherSettingsRemoteDataSource`, plus a separate
core-level `AccountLanguageRemoteDataSource` (`lib/core/account/data/`) that owns the profile
**write** path (kept out of the feature module deliberately, per its own doc comment, "so
`core/account` never depends on a feature module").

### Teacher Settings (main screen)
_Dart file: `lib/feature/teacher_module/settings/view/teacher_settings_view.dart`_
_Cubit: `TeacherSettingsCubit`_
**Reached from:** bottom nav / side menu → Settings (resolves the acting teacher id first).

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load | `GET api/Teacher/{teacherId}/configuration` (404 → falls back to `TeacherConfiguration.defaults`, not an error) then `GET api/Teacher/{teacherId}/profile` (404 → falls back to whatever is cached locally) | — | full configuration object (see field table below) + `fullName`, `languagePreference`, `studentCapacity`, `subjectIds`/`subjects[].id`, `customSubject` |
| Any single toggle/selector below | `PUT api/Teacher/{teacherId}/configuration` | **The entire current configuration object**, every time — there is no partial-patch path; every setter in `TeacherSettingsCubit` copies the one field it changed into `state.configuration` and immediately re-PUTs the whole thing | void + optional `data.prorationReconcile` / `data.billingStartReconcile` (see below) |
| Student Code: Auto / Manual | (same PUT) | `studentCodeGenerationMode: "Auto"\|"Manual"` | — |
| Session Name: Auto / Manual | (same PUT) | `sessionNameMode: "Auto"\|"Manual"` | — |
| QR Display: Soft (in-app) / Physical (hard copy only) | (same PUT) | `barcodeDisplayMode: "InApp"\|"HardCopyOnly"` | — |
| "Prorated payment" toggle | (same PUT) | `isProratedPaymentEnabled: bool` | — |
| Proration method: By percentage / By classes / Manual | (same PUT) | `prorationMethod: "ByPercentage"\|"ByClasses"\|"Manual"` | — |
| Prorated tier sliders (1st/2nd/3rd 10 days, % shown only when method = By percentage) | (same PUT) | `proratedTiers: [{tierNumber, thresholdDayStart, thresholdDayEnd, fractionRate}]` — `fractionRate` rounded to 4 decimals client-side (e.g. 66.67% → `0.6667`) | — |
| Billing start month picker (date picker → confirm dialog) | `PUT api/Teacher/{teacherId}/configuration` with an **extra** top-level key | `billingStartDate: "YYYY-MM-01"` appended to the body **only on this one save** (never echoed back on ordinary saves — it's a one-time write); screen pre-blocks the tap entirely with an info toast if `billingStartLocked` is already true, so a `BillingStartDateLocked` failure code is never actually exercised client-side | `data.billingStartReconcile: {removedPeriods, backfilledPeriods, keptPaid, keptManual, studentsAffected}` → toast "`removed` removed · `added` added · `keptPaid` kept paid" |
| Device Lock toggle | (same PUT) | `isDeviceLockEnabled: bool` | — |
| "Show payment info on attendance screen" toggle | (same PUT) | `showPaymentInfoOnAttendanceScreen: bool` | — |
| "Show attendance history on attendance screen" toggle | (same PUT) | `showAttendanceHistoryOnAttendanceScreen: bool` | — |
| Parent portal master switch | (same PUT) | `parentPortalEnabled: bool` | — |
| Parent visibility: Attendance / Payment / Grades (Grades writes both exam flags together) | (same PUT) | `parentVisibilityAttendance: bool`, `parentVisibilityPayment: bool`, or **both** `parentVisibilityExamDefault: bool` + `parentVisibilityOnlineExamDefault: bool` together for "Grades" | — |
| Language pill (EN/AR) | `PUT api/Teacher/{teacherId}/profile` (via `AccountLanguageRepository` → `AccountLanguageRemoteDataSource.saveTeacherProfile`, **not** the configuration endpoint) | `{fullName, languagePreference: "en"\|"ar", subjectIds?: [int], customSubject?}` — a **whole-object write**; the data source fetches the current profile snapshot first specifically so this partial UI edit doesn't blank out `subjectIds`/`fullName` | void envelope; on success the local cached profile is updated |
| Notifications toggle | *(no network call in this screen)* — `_pushNotificationService.setEnabled(bool)` (local OS permission request) + local storage flag | — | local-only |
| Biometric quick sign-in toggle | *(no network call)* — device secure storage + local biometric plugin, independent of this cubit | — | local-only |
| Change Password row | *(navigates elsewhere — not this chapter)* | — | — |
| Delete Account row | *(opens a bottom sheet — not this chapter)* | — | — |
| Independence section (only shown when `centerId != null`) → row | *(navigates to Teacher Independence — see below)* | — | — |
| Capacity footer ("Max N students allowed") | *(display only, from `profile.studentCapacity`, default 200 if absent)* | — | — |
| "Increase number" (only when `AppConfig.showSubscription`) | *(navigates to Subscription — not this chapter)* | — | — |

Full configuration PUT body (every key the app sends on **every** save, since it's always a whole-object
write — this example matches the DTO's `toJson()` verbatim, including the keys `null` by default):

```json
{
  "studentCodeGenerationMode": "Auto",
  "sessionNameMode": "Auto",
  "isProratedPaymentEnabled": true,
  "prorationMethod": "ByPercentage",
  "proratedTiers": [
    { "tierNumber": 1, "thresholdDayStart": 1, "thresholdDayEnd": 10, "fractionRate": 1.0 },
    { "tierNumber": 2, "thresholdDayStart": 11, "thresholdDayEnd": 20, "fractionRate": 0.5 },
    { "tierNumber": 3, "thresholdDayStart": 21, "thresholdDayEnd": 31, "fractionRate": 0.2 }
  ],
  "consecutiveAbsenceThreshold": 3,
  "consecutiveUnpaidThreshold": 3,
  "barcodeDisplayMode": "InApp",
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
```
Notes on keys with **no UI control anywhere in this app build** — they round-trip unchanged on every
save at whatever value the last `GET` returned (or the client default if the server omitted them):
`studentCapacityPackageId` (only sent when non-null, i.e. only if the server itself returned one),
`consecutiveAbsenceThreshold`/`consecutiveUnpaidThreshold` (default 3/3),
`studentVisibilityAttendance`/`Payment`/`Homework`/`ExamDefault` (default all `true` — there is a
*parent*-visibility UI but no *student*-visibility UI in this build).

`billingStartDate` is appended **only** on the one explicit billing-start save (see table row above) —
it is never part of the "every save" body.

**Reconcile summaries read back (both additive/null-safe, either may be absent on an older server or a
plain save that didn't trigger a reconcile):**
```json
{
  "prorationReconcile": { "repriced": 12, "keptManual": 3, "keptPaid": 40, "unchanged": 5 },
  "billingStartReconcile": { "removedPeriods": 8, "backfilledPeriods": 2, "keptPaid": 30, "keptManual": 1, "studentsAffected": 9 }
}
```
`prorationReconcile` is read only when the proration settings changed on this save; `billingStartReconcile`
only when `billingStartDate` was part of this save's body. The toast prioritizes billing-start over
proration if somehow both are non-empty (they never co-occur in practice, since billing-start is a
separate, one-time save).

**Error codes:** none are special-cased by name anywhere in this module. `BillingStartDateLocked` is
avoided client-side (the tap is blocked before the call once `billingStartLocked` is true) rather than
handled as a response; `ParentPortalRequiresSubscription` and any other business failure simply surface
as the server's localized `message` in a generic error toast — the envelope this module reads has no
`code` field to switch on in the first place.

### Parent Portal toggle group (embedded in Settings, not its own screen)
_Dart file: `lib/feature/teacher_module/parent_portal/view/widgets/teacher_parent_portal_settings_section_widget.dart`_

This card lives inside the Teacher Settings screen (conditionally, only when a separate summary probe
says the server has parent-portal routes at all) and writes through the **same**
`PUT api/Teacher/{teacherId}/configuration` call documented above — `parentPortalEnabled` and the three
`parentVisibility*` toggles are just more fields on that one object. The card additionally reads a
**different** endpoint family (`api/teacher/parent-portal/summary`, requests, follower management) for
the "N pending requests" / "N students missing a parent phone" nudges and the WhatsApp share action —
that family belongs to the Parent Portal chapter, not this one, and is only referenced here for
context.

### Post-Signup Configuration
_Dart file: `lib/feature/teacher_module/settings/view/teacher_post_signup_config_view.dart`_
_Cubit: `TeacherSettingsCubit`_ (same cubit, fresh instance)
**Reached from:** first-run flow immediately after teacher sign-up.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load | `GET api/Teacher/{teacherId}/configuration` (same as main settings) | — | pre-fills the identification section only (student code / session name / QR mode) |
| "Skip" | `PUT api/Teacher/{teacherId}/configuration` | `TeacherConfiguration.defaults` serialized (i.e. every field reset to its client default, ignoring whatever the teacher had tapped) | on success, marks `isConfigurationCompleted: true` in local storage and continues to the app shell |
| "Continue" | `PUT api/Teacher/{teacherId}/configuration` | Whatever the teacher set on the identification widgets on this screen, serialized the same way as every other save | same as above |

### Capacity Packages — defined but unused
`webPathTeacherCapacityPackages = 'api/Teacher/capacity-packages'` exists in `web_constant.dart` but
has **zero callers** anywhere in the app (confirmed by repo-wide grep). `studentCapacityPackageId` is
carried as a plain nullable field on the configuration DTO (round-tripped, never set by any UI), and
the capacity **number** shown to the teacher (`SettingsCapacityFooterWidget`) comes from
`GET api/Teacher/{teacherId}/profile`'s `studentCapacity` field, not from this packages endpoint. There
is currently no in-app package picker.

---

## Audit Trail (`lib/feature/teacher_module/audit_trail/`)

Repository: `AuditTrailRepository` → `AuditTrailRemoteDataSource`. Not reachable from any menu in this
build (see headline finding above) but fully implemented.

### Audit Trail
_Dart file: `lib/feature/teacher_module/audit_trail/view/teacher_audit_trail_view.dart`_
_Cubit: `TeacherAuditTrailCubit`_
**Reached from:** nowhere currently — `AppRoute.goToTeacherAuditTrail()` has no caller in the codebase
outside its own definition.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load / pull-to-refresh / infinite scroll | `GET api/audittrial` | Query: `teacherID`, `AssistantName?` (trimmed, debounced 400ms as the teacher types), `ActionType?` (`Add`\|`Edit`\|`Deactive`\|`Delete`\|`view` — **note the lowercase `view`**, the only lowercase value in the set), `Module?` (free text), `From?`/`To?` (ISO-8601 UTC instants), `Page`, `PageSize` (10) | Per item — **exact wire keys, including the misspellings**: `id`, `assisstantName` (double-s), `acction` (double-c) parsed client-side into the `actionType` enum via a case-insensitive match, `moduleName`, `desc` (the description text), `createAt` (missing a `d`, parsed as UTC); paginated envelope `totalCount`, `hasMore` |
| Search box (by assistant name) | (same `GET`, `AssistantName` param) | free text, 400ms debounce | — |
| Filter bottom sheet: Action Type dropdown, Module (free text), From date, To date → "Apply filters (N)" | (same `GET`, all filter params) | `ActionType`, `Module`, `From` (local midnight, sent as UTC ISO string), `To` (local 23:59:59, sent as UTC ISO string) | list re-fetched with `Page=1` |
| Filter chip "×" (clear all) | (same `GET`, no filter params) | — | — |
| "Export" button | `GET api/audittrial/export` | Same query params as the list (`teacherID`, `AssistantName?`, `ActionType?`, `Module?`, `From?`, `To?`) **minus** `Page`/`PageSize` — exports the whole filtered set, not just the current page | Raw byte response, `responseType: bytes`. Success: an `.xlsx` file (MIME
`application/vnd.openxmlformats-officedocument.spreadsheetml.sheet`), filename parsed from the
`Content-Disposition` header (default `AuditTrials.xlsx` if absent). If the server instead returns a
JSON error body (detected by a `Content-Type: application/json` header **or** by the first response
byte being `{` / 0x7B, since Dio was told to expect raw bytes), it's decoded and treated as a normal
envelope failure — `ensureApiSuccess` throws with the server's `message` |

Export handling client-side is platform-specific: iOS writes the bytes to a temp file and opens the
native share sheet (no user-accessible Downloads folder in the sandbox); Android saves directly to the
public Downloads folder via `saveBytesToDeviceStorage`.

---

## Independence (`lib/feature/teacher_module/independence/`)

Repository: `TeacherIndependenceRepository` (instantiated inline in the view with `apiService: apiService`
rather than resolved through `getIt` — the only DI inconsistency found in this chapter's scope).

### Teacher Independence Request
_Dart file: `lib/feature/teacher_module/independence/view/teacher_independence_view.dart`_
_Cubit: `TeacherIndependenceCubit`_
**Reached from:** Teacher Settings → "Independence" section (only rendered when the logged-in
teacher's auth user carries a `centerId`, i.e. only for center-owned teachers) → "Request
independence" row.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Screen load | `GET api/teacher/independence-request` | — | `{requestId, status, requestedAt, resolvedAt?, note?, rejectionReason?}`, or `data: null` (treated as "no request on file", not an error) if the teacher has never submitted one |
| Note text field (optional, ≤500 chars) → "Submit request" → confirm dialog | `POST api/teacher/independence-request` | `{note?}` — `note` omitted entirely if left blank | same shape as the GET; becomes the new current request, screen flips to the "pending" card |
| Pending card → "Cancel request" → confirm dialog | `DELETE api/teacher/independence-request` | — (no body) | void envelope — local request state is cleared, screen flips back to the submit form |

`status` wire values (`IndependenceStatus`, global `JsonStringEnumConverter` convention): `Pending`,
`Approved`, `Rejected`, `Cancelled` (anything else parses to a local `unknown` sentinel rather than
throwing). Only a `Rejected` request surfaces `rejectionReason` in the UI, shown above the resubmission
form. No error `code` is special-cased here either — failures are the raw `message` in a toast.

---

### Endpoint coverage

Real HTTP endpoints called by this chapter's modules (Assistants, Settings, Audit Trail, Independence —
Messages has none):

```
GET    api/assistant
GET    api/assistant/{assistantId}
GET    api/Permission/teacher
POST   api/assistant
PUT    api/assistant/{assistantId}
PATCH  api/assistant/{assistantId}/activate
PATCH  api/assistant/{assistantId}/deactivate
PATCH  api/assistant/{assistantId}/delete
GET    api/assistant/{assistantId}/login-activity

GET    api/Teacher/{teacherId}/configuration
PUT    api/Teacher/{teacherId}/configuration
GET    api/Teacher/{teacherId}/profile
PUT    api/Teacher/{teacherId}/profile

GET    api/audittrial
GET    api/audittrial/export

GET    api/teacher/independence-request
POST   api/teacher/independence-request
DELETE api/teacher/independence-request
```

Defined in `web_constant.dart` but **unused** anywhere in the app:

```
api/Teacher/capacity-packages   (webPathTeacherCapacityPackages — no caller found)
```

Not a real endpoint, but worth flagging for a backend developer reading this chapter: every
`webPath*`/wire shape referenced in the **Messages** section above (`sendTemplate`, `fetchTemplates`,
`fetchHistory`, `fetchInboxThreads`, `fetchTriggers`, `fetchHubStats`, `fetchChannelStatuses`,
`deleteTemplate`) is a **Dart-only mock interface method**, not an HTTP call — there is no
corresponding path in `web_constant.dart` and none should be inferred to exist server-side today.
