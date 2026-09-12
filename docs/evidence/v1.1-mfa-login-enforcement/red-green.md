# v1.1 — MFA login-flow enforcement (B11-3) — red/green evidence

Per TESTING_STANDARDS.md §2.1. `MfaLoginFlowTests` written first, against the pre-existing
`Login.razor`/`AuthSessionService` (no MFA gate yet).

## RED

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~MfaLoginFlowTests"
...
Failed VSoftSol.Syslog.IntegrationTests.Auth.MfaLoginFlowTests.Login_WithMfaEnabled_DoesNotSignInAfterPasswordAlone
  Expected the enum to be HttpStatusCode.OK (the MFA code step) but found HttpStatusCode.Found (302) — password alone signed the account straight in, exactly the gap B11-3 named.
Failed VSoftSol.Syslog.IntegrationTests.Auth.MfaLoginFlowTests.Login_WithMfaEnabled_CorrectCode_SignsIn
  Expected HttpStatusCode.Found but found HttpStatusCode.BadRequest — no "login-mfa" named form existed yet for the POST to dispatch to.
Failed VSoftSol.Syslog.IntegrationTests.Auth.MfaLoginFlowTests.Login_WithMfaEnabled_WrongCode_StaysUnauthenticated
Failed VSoftSol.Syslog.IntegrationTests.Auth.MfaLoginFlowTests.Login_WithMfaEnabled_TooManyWrongCodes_DiscardsTheChallenge

Failed! - Failed: 4, Passed: 1, Skipped: 0, Total: 5
```
(The one pass, `Login_WithoutMfaEnabled_StillSignsInDirectly`, is the regression check — it
was already true before any of this work started, correctly green from the first run.)

## Implementation

- Migration `010_mfa_login_challenges.sql` — a short-lived, single-use challenge table.
- `SqliteMfaLoginChallengeStore` (Data) — dumb CRUD (create/find/increment attempts/delete).
- `WebAuthOptions.MfaChallengeValidity` (default 5 min) / `MfaMaxAttempts` (default 5).
- `AuthSessionService`: `PasswordSignInAsync` now returns `PasswordSignInResult` (a status
  plus, only for the new `SignInStatus.MfaRequired`, a challenge token) instead of a bare
  `SignInStatus`; a new `CompleteMfaSignInAsync` verifies the second factor
  (`MfaSelfServiceService.VerifyLoginCodeAsync`, already built and tested in Phase 11) and,
  only on success, runs the same session-creation/cookie/audit tail every sign-in path now
  shares (`FinalizeSignInAsync`, extracted from the pre-existing direct-success path).
- `Login.razor`: a second named form (`login-mfa`), shown when a challenge token is
  present, carrying that token forward as a hidden field between the two static-SSR
  requests — same pattern as the Phase 12 setup wizard's multi-step forms, one token
  instead of many fields since login (unlike setup) is not a single-occurrence flow and so
  cannot use the wizard's server-memory-singleton shortcut.

## GREEN

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~MfaLoginFlowTests"
Passed! - Failed: 0, Passed: 5, Skipped: 0, Total: 5, Duration: 2 s

$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~SqliteMfaLoginChallengeStoreTests"
Passed! - Failed: 0, Passed: 5, Skipped: 0, Total: 5, Duration: 719 ms
```
