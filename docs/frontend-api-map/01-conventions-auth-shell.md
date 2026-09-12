# 01 — Conventions, Auth & App Shell

This chapter documents how the Edvanz Flutter mobile app (`edvanz-mobile-app`) talks to the
backend at the plumbing level (envelope shape, pagination, headers, error handling, token
refresh), plus every screen in the Auth flow and the App Shell (bottom navigation, teacher
home dashboard, side menu, notifications, app-version gate, help manifest, support contact,
file upload, push notifications).

Source of truth for every path in this document: `lib/core/network_services/web_constant.dart`
(constants/functions named `webPath*`). Request bodies come from `toJson()` / inline `Map`
literals in the data-source files; response parsing comes from each DTO's `fromJson()`. All
network calls go through `ApiService.client(...)` (a configured `Dio` instance).

**Reachability note.** A number of screens exist in the codebase, are fully wired to the
backend, and are simply not reachable from any current navigation path (no button, deep link,
or route calls them) — or are compiled out by a feature flag. Each is called out explicitly
below so a backend developer doesn't waste time chasing traffic that will never arrive from a
shipped build. The single biggest one: `AppConfig.isMvp = true`
(`lib/core/constants/app_config.dart`) currently sets `showNotifications`, `showMessaging`,
and `showHomework` all to `false` app-wide. The Notifications bell, the unread-count poll, and
the Messaging tab are fully implemented and documented below, but **do not fire from the
current shipped build** unless/until `isMvp` flips to `false`.

---

## Conventions

### Base URL & environment

`lib/core/network_services/web_constant.dart` / `api_base_url.dart`:

- Production: `webBaseUrl = 'https://api.edvanz.io/'`.
- `webIsDev` is a compile-time constant (currently `false` in the checked-in source). When
  `true`, the resolved base URL is (in order): a `--dart-define=API_BASE_URL=...` override, else
  `http://10.0.2.2:5000/` on the Android emulator, else `http://localhost:5000/` (web / iOS
  simulator / desktop).
- All `webPath*` constants are **relative** (`api/auth/login`, no leading slash) and are joined
  to the base URL by `Dio`'s `baseUrl` option.

### Standard response envelope

`lib/core/network_services/api_envelope.dart` — `ApiEnvelope<T>.fromJson`:

```json
{
  "success": true,
  "message": "Optional human-readable message",
  "data": { "...": "..." }
}
```

- `success` — the app reads `json['success']`, and if absent falls back to `json['isSuccess']`
  (comment: "Prod Swagger `Result*` uses `isSuccess`; some modules still send `success`") —
  defaults to `false` if neither key exists.
- `message` — plain string, defaults to `''`.
- `data` — arbitrary payload, mapped by a caller-supplied function or passed through raw.
- `ensureApiSuccess(envelope)` throws `ServerFailure.fromApiMessage(envelope.message)` when
  `success` is `false`, regardless of HTTP status — a 200 with `success: false` is treated as a
  business failure.
- **`code` is not part of `ApiEnvelope`.** It is read directly off the *raw* response map by
  `api_service_failure.dart`'s private `_responseCode()` helper (case-insensitive key lookup),
  independently of whether the envelope parsed. This is how the app distinguishes stable,
  language-independent business codes from the localized `message` — see "Error handling"
  below. So in practice many endpoints' error bodies look like:

```json
{
  "success": false,
  "code": "DeviceRegistrationRequired",
  "message": "Localized message text",
  "data": null
}
```

### Paginated list shape

`lib/core/network_services/paginated_api_response.dart` — `PaginatedApiResponse<T>.fromJson`.
A paginated GET's envelope `data` is itself an object with its own `data` array — i.e. the key
`data` appears twice, once for the envelope and once for the page:

```json
{
  "success": true,
  "message": "",
  "data": {
    "data": [ { "...": "..." }, { "...": "..." } ],
    "totalCount": 42,
    "page": 1,
    "pageSize": 10,
    "totalPages": 5
  }
}
```

- Every field defaults defensively if absent: `totalCount` → `items.length`, `page` → `1`,
  `pageSize` → `items.length`, `totalPages` → `1`.
- `hasMore` is computed client-side: `page * pageSize < totalCount` when both are positive,
  else `page < totalPages`.
- Request query parameters are conventionally `page` (1-based) and `pageSize`.
- `fetchAllPaginatedItems` / `fetchAllPaginatedEitherItems` (`paginated_fetch.dart`,
  `api_either.dart`) walk every page client-side (capped at 100 pages) when a screen needs the
  full set (e.g. day filters computed client-side).

### Error handling

`lib/core/network_services/api_service_failure.dart` maps every thrown value to a typed
`Failures` subclass, then to localized copy (`api_user_message.dart`):

- **Message extraction priority** (`_extractFirstErrorMessage`): (1) `data` as a `List<String>`
  → first string (e.g. `{"data": ["name should not be empty"]}`); (2) RFC 9110 validation
  problem details `{"errors": {"field": ["..."]}}`  → first nested string; (3) `message` as a
  string or list of strings; (4) generic fallback ("An error occurred").
- **By HTTP status** (`ServerFailure.fromResponse`):
  - A `code` of `DeviceRegistrationRequired` → `DeviceLockFailure(kind: registrationRequired)`.
  - A `code` of `DeviceMismatch` or `DeviceIdMissing` → `DeviceLockFailure(kind: mismatch)`.
  - `401` → generic "Unauthorized" (the token-refresh flow usually intercepts 401s before this
    is reached — see below).
  - `409` → `ConflictFailure` (still uses the extracted message).
  - `404` → `NotFoundFailure`, preferring the envelope's specific message (e.g.
    `SessionOccurrenceNotFoundForDate`) over a generic "not found".
  - `500` → prefers a specific envelope message when present (some business rules deliberately
    return 500 with a friendly message) before falling back to a generic "Internal server
    error".
  - Everything else (`400`/`402`/`403`/`405`/`422`/`429`/...) → the envelope's specific message
    is always surfaced; nothing is flattened to a generic string.
- **Transport-level** (`ServerFailure.fromDioException`): connection timeout / bad certificate /
  cancel / DNS-ish `unknown` errors are mapped to localized copy; `connectionError` and a
  `SocketException` inside `unknown` become `NetworkFailure` (distinct from `ServerFailure` so
  offline-sync code can tell "never reached the server" from "server rejected it").
- **`resolveApiUserMessage`** (`api_user_message.dart`) re-localizes a handful of known English
  server phrases into the active locale via exact-match and heuristic (substring) lookup tables
  (e.g. "this phone number is already registered", "invalid username or password"); Arabic text
  is passed through unchanged; anything else is shown verbatim.
- **`SessionExpiredFailure`** is a sentinel with no message — the UI never toasts it; `MyApp`
  shows a single global "session expired" message and routes to login once, application-wide.

### Standard headers

Set in `ApiService.client()` (`api_service.dart`) plus two dedicated interceptors:

| Header | When sent | Value |
|---|---|---|
| `Authorization` | Whenever `client(requireAuth: true)` or an explicit `bearerToken` is used (`TokenInterceptor.onRequest`) | `Bearer <accessToken>` (from secure storage, or the token passed explicitly for one-off calls like the Google/complete-profile token) |
| `Accept-Language` | Every request | `en` or `ar` (`LocalStorage.getLocaleLanguage()` — normalizes `en_US`/`ar_EG` down to the 2-letter code); this is how the server localizes `message`, help/version/support-contact copy, etc. |
| `Accept` | Every request | `application/json, application/geo+json, application/gpx+xml, img/png; charset=utf-8` (static) |
| `Content-Type` | Every request | `application/json` by default; a `_FormDataRequestInterceptor` rewrites it to `multipart/form-data; boundary=<boundary>` whenever the request body is a Dio `FormData` (fixes ASP.NET model binding, which otherwise rejects a boundary-less `multipart/form-data`) |
| `X-Device-Id` | Every request | Stable per-phone id for the student "device lock" feature — Android: salted SHA-256 of `Settings.Secure.ANDROID_ID`; iOS: a random id in the Keychain with `first_unlock_this_device`, excluded from iCloud backup. Survives reinstall/clear-storage; resets only on factory reset (Android) or a genuinely new device (iOS). |
| `X-Device-Id-Previous` | Only while migrating off the old (pre-device-id) per-install uuid, for up to a 90-day window | The legacy uuid, so the backend can re-point an existing device-lock binding once, then this header disappears for good |
| `X-Acting-Teacher-Id` | Only when the signed-in account is `Center`/`CenterAssistant` **and** a teacher is currently selected to act as (`ActingTeacherHeaderInterceptor`) | The numeric `teacherId` the center is currently operating; deliberately gated on account type so a stale stored id from an earlier center session, or a center-scope request fired while "acting as", never rides along by accident |

Two additional interceptors don't add headers but react to them:
- `ActingTeacherUnavailableInterceptor` — if a request carried `X-Acting-Teacher-Id` and gets a
  403 back, it (debounced 3s) fires a callback that clears the acting-teacher selection and
  bounces the operator to Center Home (the acting teacher was deactivated/removed mid-session).
- `LogInterceptor` — request/response logging in debug builds only.

### Token refresh flow

`lib/core/network_services/token_interceptor.dart` — a `QueuedInterceptor` (so no two requests
race the retry) with **static** re-entrancy guards (`_isRefreshing`, `_refreshCompleter`) shared
across every `Dio` instance the app creates, i.e. refresh is single-flight app-wide:

1. A `401` on any authenticated request is intercepted, **except** on `auth/login`,
   `auth/admin-login`, `auth/refresh`, and `auth/logout` themselves (those must fail as-is, not
   recurse into a refresh).
2. If a refresh is already in flight, the caller just awaits the shared `Completer` instead of
   starting a second one.
3. Otherwise it calls `AuthRepository.refreshSession()` → `POST api/auth/refresh` with body
   `{ "token": "<refreshToken>" }`. The response is parsed with the **same envelope shape as
   login** (`accessToken`, `refreshToken`, `userAccountData`).
4. On success, the original failed request is replayed once with the new access token
   (`Authorization: Bearer <new accessToken>`); a **second** 401 on the retry is treated as a
   real session expiry (not a fresh refresh attempt).
5. On failure: a `NetworkFailure` (refresh never reached the server) is surfaced as a network
   error and the **stored session is kept** — offline-aware callers can retry later. Any other
   failure (a real rejection) clears the stored session and emits `SessionExpiredFailure`,
   which `AuthCubit.handleSessionExpired()` turns into a single global "please sign in again"
   state (idempotent against a burst of concurrent 401s).
6. `logout` (`POST api/auth/logout`, body `{ "token": "<refreshToken>", "logoutAllSessions":
   false }`) revokes the current device's refresh token server-side; the local session is
   cleared regardless of whether that call succeeds (best-effort, `unawaited`).

---

## Login

_Dart files: `lib/feature/auth/view/login_view.dart` →
`lib/feature/auth/view/widgets/login_view_body.dart` →
`lib/feature/auth/view/widgets/login_teacher_panel_widget.dart`_
**Reached from:** app launch (unauthenticated) / logout / session expiry / "Register" back-link.

Only the **teacher/assistant credential panel** is live. `LoginViewBody` also contains a role
switcher and an assistant-specific panel, but both are commented out in the current source
(`// LoginRoleSwitcherWidget`, `// if (_selectedRoleIndex != 0) const LoginAssistantPanelWidget()`)
— so `LoginAssistantPanelWidget`, `LoginRoleSwitcherWidget`, and `LoginSocialButtonWidget` are
dead code today: fully implemented, never rendered. Likewise the Google/Apple social buttons on
`LoginTeacherPanelWidget` are commented out, so `signInWithGoogle()` / `AuthAccountExistsDialog`
never fire from this screen either (see the "Google Sign-Up" note further down).

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| On open | none | — | — (biometric availability check is local-only via `local_auth`) |
| "Login" button | `POST api/auth/login` | body: `{ "userName": "...", "password": "..." }` | `data.accessToken`, `data.refreshToken`, `data.userAccountData.{accountId, userName, fullName, accountType, models, permissions, teacherIds, centerId, centerName}` — stored as the session; a `Teacher` account additionally triggers `GET api/Teacher/{teacherId}/profile` (see below) to enrich the session before it's saved |
| Biometric button (shown once biometrics are enrolled) | none | — | Re-plays the last-saved username/password through the same `login` call above; no separate endpoint |
| "Register" link | none (navigation only) | — | → `RegisterView` |

`AuthCubit.login()` → `AuthRepositoryImpl.login()` → `AuthRemoteDataSource.login()` posts to
`webPathAuthLogin = 'api/auth/login'`, then (only for `AuthAccountType.teacher`) calls
`GET api/Teacher/{teacherId}/profile` (`webPathTeacherProfile`) with the just-issued access
token, reading `profile.id`, `profile.fullName`, `profile.userName` to backfill `teacherIds`/
`fullName`/`userName` on the stored `AuthUser` — a network failure here falls back to any
previously cached teacher profile rather than failing the whole login.

```json
// POST api/auth/login
{ "userName": "mohamedomar", "password": "••••••••" }
```

Error handling: any non-success envelope surfaces `result.failureMessage` verbatim as an error
toast (no special-cased codes on this screen).

Locally cached / stored on success: access token, refresh token, and the serialized `AuthUser`
(`LocalStorage.setUserToken/setRefreshToken/setAuthUserJson`); the last-used username
(`rememberLoginUserName`) for the biometric-login replay; biometric credentials are (re-)seeded
if biometric login was already enabled or pending enrollment.

---

## Register (Sign Up)

_Dart file: `lib/feature/auth/view/register_view.dart`_
**Reached from:** "Register" link on the Login screen.

Role choice is `Teacher` or `Student` only (parent registration is hidden — comment: "Parent
registration is hidden for now"). Teacher additionally requires at least one subject (from
`TeacherSubjectSelectionWidget`, backed by `GET api/Teacher/subjects`) or a free-text custom
subject.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Subject chips (teacher role only) | `GET api/Teacher/subjects` | — | `data[].{id, ...}` (anonymous-safe; also used on the Google-profile-completion screen) |
| "Register" button | `POST api/auth/sign-up` (**multipart/form-data**, not JSON) | see fields below | `data` is the OTP code string, shown in an info toast, then the app navigates to OTP verification. If the sign-up response has no OTP inline, the app immediately calls `POST api/auth/generate-otp` as a fallback. |

`AuthRemoteDataSource.signUp()` builds a `FormData` with plain string fields (no file parts):

```
userType=1                       // int: Teacher=1, Assistant=2, Student=4, Parent=5
fullName=Ahmed Ali
username=ahmedali
password=••••••••
confirmedPassword=••••••••
phoneNumber=01012345678          // omitted entirely if null
email=ahmed@example.com          // omitted entirely if null
languagePreference=en            // omitted entirely if null
studentCapacity=50               // omitted entirely if null (never sent from Register — see note)
customSubject=Physics tutoring   // omitted entirely if null
subjectIds=3                     // one repeated form field per selected id, e.g. subjectIds=3, subjectIds=7
```

Note: `studentCapacity` is deliberately never populated from the Register screen — the comment
in `register_view.dart` explains capacity is chosen later, at subscription-request time, not at
sign-up.

Errors surface via the generic toast; the app-side heuristic message mapper additionally
special-cases "this phone number is already registered" and duplicate-username-shaped messages
into localized copy (see Conventions → error handling).

---

## OTP Verification

_Dart file: `lib/feature/auth/view/otp_view.dart`_
**Reached from:** Register (purpose `signUp`) — the only reachable path today. Also reachable
in principle from `ForgetPasswordView` (purpose `forgotPassword`) and `EnterPhoneView`, but
both of those screens are themselves unreachable (see next section).

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| 6-digit boxes auto-fill | none | — | Pre-fills from the OTP the sign-up/generate-otp call already returned inline (`initialCode`) — **the backend currently returns the OTP value in the API response body rather than delivering it by SMS** (see doc comment in `otp_view.dart`) |
| "Verify code" button | `POST api/auth/verify-otp` | body: `{ "phoneNumber": "01012345678", "otp": "123456" }` | On success with purpose `signUp`: success toast, navigate to Login. On success with purpose `forgotPassword`: shows **"Password reset is not available yet. Sign in and use change password if you still have access to your account."** and navigates to Login — i.e. even if this path were reached, the app deliberately dead-ends it (see next section). Failure surfaces `result.failureMessage`. |
| "Resend" link | `POST api/auth/generate-otp` | query param: `phoneNumber` | `data` is the new OTP string; re-shown in an info toast |

Verifying an OTP for the `signUp` purpose also fires a best-effort Meta/Facebook "completed
registration" analytics event (`method: 'phone'`) — not a backend call.

---

## Forgot Password, Enter Phone, Confirm Password (unreachable — documented for completeness)

_Dart files: `lib/feature/auth/view/forget_password_view.dart`,
`lib/feature/auth/view/enter_phone_view.dart`,
`lib/feature/auth/view/confirm_password_view.dart`_

**These three screens are dead code in the current build.** Their navigation entry points
(`AppRoute.goToForgetPasswordScreen`, `goToEnterPhoneScreen`, `goToConfirmPasswordScreen`) have
**zero call sites** anywhere in the app. The assistant-login "Forgot password?" link (itself
unreachable, since the assistant login panel is commented out — see Login above) shows a plain
"contact your teacher" dialog instead of navigating here. The endpoints below are real and
implemented client-side, but nothing in the shipped app currently triggers them:

| Screen | Endpoint | Sends | Notes |
|---|---|---|---|
| `ForgetPasswordView` | `POST api/auth/generate-otp` | query: `phoneNumber` | Then navigates to `OtpView(purpose: forgotPassword)`, which (per above) always ends in "password reset not available" + back to Login — so `webPathForgotPassword` (`api/auth/forgot-password`) and `webPathResetPassword` (`api/auth/reset-password`) constants exist in `web_constant.dart` but **are never called from anywhere in the app.** |
| `EnterPhoneView` | `POST api/auth/generate-otp` | query: `phoneNumber` | Then navigates to `OtpView(purpose: signUp)` — this looks like an alternate phone-first sign-up entry, but nothing links to it |
| `ConfirmPasswordView` | `POST api/auth/sign-up` (multipart, same shape as Register) | `userType=1` (hardcoded Teacher), `fullName`, `username`, `password`, `confirmedPassword`, `phoneNumber` (from the previous screen), `languagePreference`, `subjectIds`/`customSubject` | Teacher-only variant of Register, reached (in code, not in the UI) after `EnterPhoneView` |

---

## Complete Google Profile / Google Sign-In (unreachable)

_Dart files: `lib/feature/auth/view/complete_google_profile_view.dart`,
`lib/feature/auth/data/google_sign_in_service.dart`_

The Google/Apple buttons on the Login screen are commented out
(`login_teacher_panel_widget.dart`), so `AuthCubit.signInWithGoogle()` and this screen are
never reached from the current UI. Documented because the full server contract is implemented
and could be re-enabled by uncommenting a few lines:

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| (disabled) "Continue with Google" | `POST api/auth/sigup-by-google` *(note the backend's own typo: `sigup`, not `signup`)* | body: `{ "clientDeviceToken": "<Google ID token>" }` | If `data.refreshToken`/`data.userAccountData` are present → full session (same shape as login). If **absent** → treated as "profile completion required": `data.accessToken` is stashed as a short-lived `completeProfileToken` and the app navigates to `CompleteGoogleProfileView`. |
| `CompleteGoogleProfileView` "Submit" | `POST api/auth/complete-profile` (bearer = the stashed `completeProfileToken`, not the normal session token) | body: `{ "userType": 1, "fullName": "...", "username": "...", "phoneNumber": "...", "languagePreference": "en", "subjectIds": [3,7], "customSubject": "..." }` | Full session envelope, same shape as login; also triggers the teacher-profile enrichment call and a "completed registration" (`method: 'google'`) analytics event |

`AuthAccountExistsDialog` (`lib/feature/auth/view/widgets/auth_account_exists_dialog.dart`) is
defined but has no call site anywhere — dead code, presumably a leftover for a Google-sign-in
edge case that was never wired up.

---

## Change Password

_Dart file: `lib/core/shared_widgets/account/view/change_password_view.dart`_ (lives under the
shared Account UI, not `lib/feature/auth`, but is driven entirely by `AuthCubit`/`AuthRepository`)
**Reached from:** Side menu → Settings → Change password (teacher); equivalent entry on the
student/parent Account tab.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| "Submit" button | `POST api/auth/change-password` | body below | `message` shown as a success toast; no `data` is consumed |

```json
{
  "oldPassword": "OldPass1!",
  "newPassword": "NewPass1!",
  "confirmPassword": "NewPass1!",
  "logOutFromAllDevices": false,
  "currentRefreshToken": "<this device's refresh token>"
}
```

`currentRefreshToken` is **this** device's refresh token, sent so the backend can identify
which session survives — the documented server behavior (per `CLAUDE.md` §5.4/password-change
handling) is that changing the password signs out every *other* device while this one
transparently keeps working. `logOutFromAllDevices` is a separate explicit toggle the UI
currently never sets to `true` (no checkbox for it on this screen) — when `true`, the local
session is torn down immediately after the call succeeds instead of staying signed in.

---

## Delete Account

_Dart file: `lib/core/shared_widgets/account/view/widgets/delete_account_bottom_sheet.dart`_
**Reached from:** Settings → Delete account (self-service account deletion, required for Apple
App Store guideline 5.1.1(v) / Google Play).

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| "Delete Account" confirm button | `DELETE api/auth/delete-account` | — (no body; identity resolved server-side from the bearer token) | `message` shown as a success toast (`"Your account deletion request has been received and your account is now disabled."`). On success the app clears push registration (OneSignal logout), clears the local session and biometric credentials, and returns to Login. |

---

## Teacher Bottom Navigation Shell

_Dart file: `lib/feature/teacher_module/home/view/teacher_nav_bar_view.dart`_
**Reached from:** `AppRoute.goToHomeForAuthUser` after a Teacher/Assistant login (or straight
from restored-session routing).

Calls no API of its own — it composes five `IndexedStack` tabs (Home, Students, Sessions,
Attendance, Payment) plus the side-menu `Drawer`. The only backend-relevant side effect: opening
the drawer force-refreshes two badge counts (see Side Menu Drawer below). Assistants get
`AssistantPaymentView` instead of `TeacherPaymentView` on the Payment tab (a purely local role
check: `AuthUser.isAssistant`, cached from login).

## Teacher Home Tab

_Dart file: `lib/feature/teacher_module/home/view/teacher_home_tab_view.dart` +
`lib/feature/teacher_module/home/manager/teacher_home_cubit/teacher_home_cubit.dart`_
**Reached from:** Teacher bottom nav, tab 0 (default landing tab after login).

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| App bar — menu icon (with red dot if pending link requests) | `GET api/teacher/student-links/requests` | query: `page=1&pageSize=1` (just to read the total) | `data.totalCount` → red-dot badge only; the actual request list belongs to the Student-Linking chapter |
| App bar — notification bell (with red dot if unread) *(hidden while `AppConfig.showNotifications == false`, i.e. the current MVP build)* | `GET api/notifications/unread-count` | — | `data` (bare int, or `{count}`/`{unreadCount}`) → badge dot |
| Subscription attention banner | `GET api/subscription/status` | — | `data.{attentionLevel, ctaType, message, isCritical}` (see Notifications/Subscription note below) — only rendered when `attentionLevel != "none"` |
| Offline unsent-records banner | none | — | Purely local — reads the offline outbox, no network call |
| Date strip (week view) | none | — | Derived client-side from the session list already fetched (see next row); no separate endpoint |
| Today's sessions section | `GET api/session/{teacherId}/sessions` | query: `activeOnly=true&sortBy=...&sortDirection=Asc&page=1&pageSize=10` (paginated — the app walks every page) | Session list → grouped into calendar days client-side |
| Today's exam banner | `GET api/Attendance/dashboard` | query: `date=YYYY-MM-DD` (teacher-local selected day; omitted for "today") | `data.{sessionCards[], examsToday[]}` → `examsToday[].{examId, name, sessionId, sessionName, assignedCount}` feeds the "take attendance for today's exam" prompt |
| Pull-to-refresh | re-runs the three calls above, plus (if enabled) unread-count and `subscriptionStatusCubit.loadStatus()` | — | — |
| "+" FAB (create) | none | — | Opens a local action menu (create session/student/exam/etc.); those destinations belong to their own module chapters |

`GET api/subscription/status` response shape (`SubscriptionStatusInfo.fromJson`):

```json
{
  "hasSubscription": true,
  "planType": "Full",
  "status": "ExpiringSoon",
  "daysRemaining": 4,
  "endDate": "2026-09-20",
  "renewalAmountEGP": 850.0,
  "attentionLevel": "warning",
  "ctaType": "renew",
  "message": "Your subscription expires in 4 days.",
  "hasPendingRequest": false,
  "whatsAppNumber": "+201234567890",
  "features": { "studentAccountsAllowed": true, "parentFollowUpAllowed": false }
}
```

## Teacher Side Menu Drawer

_Dart file: `lib/feature/teacher_module/home/view/widgets/teacher_side_menu_drawer.dart`_
**Reached from:** menu icon in the Teacher Home app bar (or any teacher-shell tab's own menu
button).

Almost every row is pure navigation into another module's screens (Sessions, Students, Exams,
Videos, Messaging, Assistants, Settings, Recycle Bin, Privacy, Help/FAQ, etc.) — those endpoints
belong to their own chapters. The rows with API-driven content that belong here:

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| On drawer open | `GET api/teacher/student-links/requests` (forced refresh) | `page=1&pageSize=1` | `data.totalCount` → "Link Requests" row badge |
| On drawer open | `GET api/teacher/parent-portal/summary` (forced refresh) | — | `data.{pendingCount, followedStudentsCount, studentsMissingParentPhone, portalAllowed}` → "Parent requests" row badge (`pendingCount`, only shown when `portalAllowed`); row is hidden entirely if the caller lacks permission or an older backend 404s the whole parent-portal feature |
| Profile header (name, phone/username, plan badge) | none | — | Name/username from the cached `AuthUser`; plan/days-remaining reuses whatever `GET api/subscription/status` already returned to the Home tab (not re-fetched here) |
| "Logout" | `POST api/auth/logout` | body: `{ "token": "<refreshToken>", "logoutAllSessions": false }` | Best-effort (`unawaited`) — the local session is torn down immediately regardless of the server call's outcome; also clears OneSignal's push registration (`OneSignal.logout()`) |

---

## Student / Parent Bottom Navigation Shell

_Dart file: `lib/feature/navbar/view/nav_bar_view.dart`_
**Reached from:** `AppRoute.goToHomeForAuthUser` after Student/Parent login.

Calls no API of its own. Tabs, built from one ordered list so hiding a tab can never desync
indices: Home (`ParentHomeTabView` / `HomeTabView`), Message (`MessageTabView`, **hidden**
while `AppConfig.showMessaging == false`, i.e. the current MVP build), Link (`LinkTapView`,
student-only), Account (`AccountTabView`). Each tab's own endpoints belong to its module's
chapter (Student home / Parent home / Messaging / Student linking / Account settings).

---

## Notifications

_Dart file: `lib/core/shared_widgets/notification/view/notification_tab_view.dart` +
`lib/core/shared_widgets/notification/manager/notification_cubit.dart`_
**Reached from:** the notification bell in the Teacher Home app bar (pushed as its own screen,
not a bottom-nav tab). **Currently unreachable in the shipped MVP build** —
`AppConfig.showNotifications` resolves to `false` (`isMvp = true`), which hides the bell
everywhere it would otherwise appear; the screen and its cubit remain fully wired for whenever
that flag flips.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| On open | `GET api/notifications` | query: `page`, `pageSize` | paginated `data.data[].{id, title, body, createdAt, isRead, avatarUrl, type}` — the DTO tolerantly reads several possible key aliases per field (`body`/`message`/`content`/`description`; `createdAt`/`createdDate`/`sentAt`/`timestamp`; `isRead`/`read`/`isSeen`/`readStatus`; `id`/`notificationId`) |
| On open (refresh only, not "load more") | `GET api/notifications/unread-count` | — | `data` (bare int, or `{count}`/`{unreadCount}`) |
| Scroll to bottom | `GET api/notifications` | query: `page: <next>`, `pageSize` | appends to the list |
| Pull to refresh | both calls above | — | resets to page 1 |
| Tap a notification | `PUT api/notifications/{notificationId}/mark-read` | — (id in the path) | on success, marks that row read locally and decrements the unread counter (no response body consumed beyond `success`) |
| "Mark all as read" | `PUT api/notifications/mark-all-read` | — | on success, zeroes the unread counter and marks every loaded row read locally |

---

## App Version Gate (Force / Optional Update)

_Dart files: `lib/core/app_version/data/app_version_repository.dart`,
`lib/core/app_version/data/app_version_remote_data_source.dart`,
`lib/core/app_version/view/app_force_update_view.dart`_
**Reached from:** app launch, via the splash screen (`SplashView`), before any auth routing.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Splash screen boot | `GET api/app/version-status` (anonymous — `requireAuth: false`) | query: `platform` (`"ios"` or `"android"`), `build` (native build number, int, from `PackageInfo`) | `data.{updateMode, storeUrl, title, message, latestVersion, latestBuild, minSupportedBuild}` |

- `updateMode`: `"forced"` → non-dismissable `AppForceUpdateView` replaces the whole navigation
  stack, blocking the app until the store is opened; `"optional"` → a dismissable prompt, then
  normal routing continues; anything else (including a missing/unparseable field) → proceed
  normally.
- `title`/`message` are already localized server-side (`Accept-Language` header) — the app
  displays them verbatim, no client-side copy.
- This call has an 8-second timeout independent of the global `Dio` timeouts, and **every**
  failure (timeout, offline, parse error, non-Android/iOS platform) is swallowed — the version
  gate must never block launch on a flaky network.
- The response is accepted either as a bare object (`{"updateMode": ...}`) or wrapped in the
  standard envelope (`{"data": {"updateMode": ...}}`) — the client checks for `data` first,
  then falls back to treating the whole body as the payload if it recognizes `updateMode` or
  `storeUrl` keys directly on it.

---

## Help & Onboarding Manifest

_Dart files: `lib/core/help/data/help_content_remote_data_source.dart`,
`lib/core/help/model/help_content.dart`_
**Reached from:** app start (prefetched/cached) and every screen wrapped in `HelpTourScope` /
the in-app Help Center and FAQ screens (`lib/core/help/view/help_center_view.dart`). Gated by
`AppConfig.showHelpOnboarding` (currently `true`, i.e. live).

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Manifest fetch (persona-scoped or all) | `GET api/help/manifest` (anonymous) | optional query: `persona` (`teacher`/`student`/`assistant`) — omitted entirely fetches every persona's content in one payload | `data.{version, modules[], faqs[]}` |

```json
// GET api/help/manifest?persona=teacher — response data shape
{
  "version": "3",
  "modules": [
    {
      "key": "dashboard",
      "persona": "teacher",
      "status": "live",
      "order": 1,
      "title": "Home dashboard",
      "tour": [
        { "anchorKey": "dash_week_strip", "order": 1, "title": "Pick a day", "body": "..." }
      ],
      "articles": [
        { "key": "overview", "order": 1, "title": "What is this?", "sections": [
          { "heading": "Overview", "body": "..." }
        ] }
      ]
    }
  ],
  "faqs": [
    { "persona": "teacher", "moduleKey": "dashboard", "order": 1, "question": "...", "answer": "..." }
  ]
}
```

`status: "coming_soon"` (anything other than `"coming_soon"` is treated as `"live"`) suppresses
that module's coach-mark tour and shows a "coming soon" ribbon instead. On any fetch failure the
app falls back to a bundled snapshot (`assets/help/{en,ar}.json`), so this endpoint being briefly
unavailable never blanks the Help Center.

---

## Support Contact (WhatsApp CTA)

_Dart file: `lib/feature/auth/view/widgets/center_contact_cta_widget.dart`_
**Reached from:** a small "Are you a learning center? Contact us on WhatsApp" link pinned at the
bottom of the Login screen, below the teacher panel.

| UI element / action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| On Login screen load | `GET api/support/contact` (anonymous) | — | `data.whatsAppNumber` — if present and non-empty, the CTA fades in; otherwise the widget renders nothing (zero height, no error, no toast) |
| Tap the CTA | none | — | Opens `https://wa.me/<digits-only number>?text=<localized prefill message>` via the OS |

This call is deliberately silent on failure (network error, missing number, unexpected shape) —
by design it must never show an error toast on the login screen; it just stays invisible.

---

## File Upload & Gated Files

_Dart files: `lib/core/services/image/uploader_service.dart`,
`lib/core/services/image/upload_category.dart`, `lib/core/modules/upload_data.dart`,
`lib/core/network_services/authenticated_image_headers.dart`,
`lib/core/shared_widgets/custom_network_image.dart`_

Every file upload across the app (video photos/attachments, exam question images) funnels
through this one service.

| Action | Endpoint | Sends | Uses from response |
|---|---|---|---|
| Upload one or more files | `POST api/upload` (**multipart/form-data**) | fields: `files` (one or more file parts) + `category` (string enum — see below) | `data[].{fileId, url, originalName, size, mimeType}` — the caller-facing feature (e.g. video create) then references `fileId`, never re-uploading bytes |
| Replace an uploaded file in place | `PUT api/upload` (multipart) | fields: `FileId` (old id, string), `File` (new file part) | single object, same shape as one element of the POST response array (the client tolerantly accepts either a bare object or a one-element array for this call) |
| Delete an uploaded file | `DELETE api/upload` | query: `fileId` | envelope success/message only |
| Display a gated file | *(no separate call — the URL returned above, e.g. `https://api.edvanz.io/api/files/{fileId}`, is used directly)* | `Authorization: Bearer <token>` is attached **only** when the image URL's host matches the API base URL's host (`authenticatedImageHeaders`) — never leaked to a third-party image host (placeholders, Google avatar URLs, etc.) | rendered via `CachedNetworkImage` with those headers |

`category` accepts exactly four values (`UploadCategory.apiValue`): `VideoPhoto`,
`VideoAttachment`, `OnlineExamQuestionImage`, `VideoExamQuestionImage`.

```
POST api/upload
Content-Type: multipart/form-data; boundary=...
--boundary
Content-Disposition: form-data; name="category"

VideoPhoto
--boundary
Content-Disposition: form-data; name="files"; filename="cover.jpg"
Content-Type: image/jpeg

<bytes>
--boundary--
```

---

## Push Notification Registration

_Dart file: `lib/core/push_notifications/push_notification_service.dart`_

**There is no Edvanz backend endpoint for push-device registration.** Push delivery is entirely
delegated to the OneSignal SDK (`appId` hardcoded client-side:
`21fe89c7-c321-4d2c-84b2-0f679269049b`); the app's only integration point with its *own*
backend is implicit — whatever server-side OneSignal integration exists (tagging/targeting a
user by external id) is presumably configured on the backend/OneSignal dashboard side, not
called from this client.

| Event | OneSignal call (not an Edvanz API call) | Notes |
|---|---|---|
| Successful login / sign-up / restored session (`navigateForAuthenticatedAuthState`) | `OneSignal.login('<accountId>')` | Ties the device's push subscription to the numeric `AuthUser.accountId` as OneSignal's "external user id" |
| Logout | `OneSignal.logout()` | Called from `LogoutBottomSheet` alongside `AuthCubit.logout()` |
| Delete account | `OneSignal.logout()` | Called from `DeleteAccountBottomSheet` after the delete call succeeds |
| Settings toggle / permission prompt | `OneSignal.Notifications.requestPermission()`, `OneSignal.User.pushSubscription.optIn()/optOut()` | Local device permission state, persisted in `LocalStorage`, not sent to the Edvanz backend directly |

---

### Endpoint coverage

Every endpoint referenced in this chapter, deduplicated (`METHOD path`):

```
POST   api/auth/login
POST   api/auth/logout
POST   api/auth/refresh
POST   api/auth/sign-up
POST   api/auth/generate-otp
POST   api/auth/verify-otp
POST   api/auth/sigup-by-google        (backend's own spelling — not "signup")
POST   api/auth/complete-profile
POST   api/auth/change-password
DELETE api/auth/delete-account
GET    api/auth/forgot-password        (constant defined, never called — see "Forgot Password" section)
GET    api/auth/reset-password         (constant defined, never called — see "Forgot Password" section)
GET    api/Teacher/subjects
GET    api/Teacher/{teacherId}/profile
GET    api/session/{teacherId}/sessions
GET    api/Attendance/dashboard
GET    api/subscription/status
GET    api/teacher/student-links/requests
GET    api/teacher/parent-portal/summary
GET    api/notifications
GET    api/notifications/unread-count
PUT    api/notifications/{notificationId}/mark-read
PUT    api/notifications/mark-all-read
GET    api/app/version-status
GET    api/help/manifest
GET    api/support/contact
POST   api/upload
PUT    api/upload
DELETE api/upload
GET    api/files/{fileId}              (reached only via a server-returned `url`, never path-built client-side)
```

### Legacy / unused constants in `web_constant.dart`

Around line 502 there is a block headed `// ——— Legacy / other (update when modules are wired)
———`:

```dart
const String webPathDepartments = 'departments';
const String webPathCompanies = 'companies';
const String webPathUpload = 'api/upload';
const String webPathEmployees = 'employees';
const String webPathSalaryAdjustments = 'salary/adjustments';
const String webPathRequests = 'requests';
const String webPathDashboard = 'stats/dashboard';
const String webPathSalaryPayroll = 'salary/payroll';
const String webPathSalaryMonths = 'salary/months';
const String webPathSalaryReport = 'salary/report';
const String webPathSalary = 'salary';
const String webPathKpis = 'kpis';
const String webPathSettings = 'settings';
```

Verified by grepping every `.dart` file under `lib/`: `webPathDepartments`, `webPathCompanies`,
`webPathEmployees`, `webPathSalaryAdjustments`, `webPathRequests`, `webPathDashboard`,
`webPathSalaryPayroll`, `webPathSalaryMonths`, `webPathSalaryReport`, `webPathSalary`,
`webPathKpis`, and `webPathSettings` are **never referenced anywhere outside this file** — dead
constants left over from an earlier (non-education) template, none of these paths exist on the
Edvanz API and none should be treated as real. `webPathUpload` sits in the same block but *is*
heavily used (see File Upload section) — it's just misplaced, not legacy.

One more oddity nearby: `webLessonExamQuestions(lessonId) => 'student/videos/lessons/$lessonId/quiz'`
(no `api/` prefix, unlike every other real path) **is** referenced once, in
`lib/feature/student_module/exam/data/exam_repository_impl.dart` — out of scope for this
chapter (student video-exam module) but worth flagging to whoever owns that chapter, since its
shape doesn't match any other convention in this file.
