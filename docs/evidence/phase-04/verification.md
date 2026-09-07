# Phase 4 — verification (phase prompt "Verification" section)

## `dotnet test --filter "Authorization|Audit|Scope"`

```
Authorization  -> AuthorizationMatrixTests (62 cases: 4 roles x every discovered route +
                  route-discovery + nav-coverage) .................... PASS
Audit          -> AuditLogTests (7) + AuditDiffTests (5) ............. PASS
Scope          -> ScopedEventReaderTests (7) + UserScopeTests (5) +
                  ScopeChokepointArchitectureTests (2) ............... PASS
```
Full run: `docs/evidence/phase-04/test-output.txt` — 589 passed, 0 failed, 0 skipped.

## `dotnet run --project src/VSoftSol.Syslog.Web` (Production environment)

Live host, verified with curl against `https://localhost:5443`:

```
GET /        -> 302  Location: https://localhost:5443/login?returnUrl=%2F   (HTTPS redirect works;
                                                                              unauthenticated -> login, not 500)
GET /login   -> 200  (sign-in form renders from the design system)
Content-Security-Policy: default-src 'self'; base-uri 'self'; object-src 'none';
  frame-ancestors 'none'; form-action 'self'; img-src 'self' data:; font-src 'self';
  connect-src 'self'; style-src 'self' 'nonce-...'; script-src 'self' 'nonce-...'
  -> no 'unsafe-inline', fresh nonce
```

## Manual confirmations (via the integration suite over the real pipeline)

| Item | How verified | Result |
|---|---|---|
| HTTPS redirect works | `curl` above; `app.UseHttpsRedirection()` | ✓ |
| Self-signed certificate generated | ASP.NET Core dev-cert in dev; the **installer** provisions/binds the cert for a real install (Phase 12, ADR 0006). Not a Phase 4 code path. | deferred to Phase 12 (installer) |
| Login succeeds | `AuthFlowTests.Login_WithGoodCredentials_SetsCookieAndRedirects` | ✓ |
| Forced password change fires | `AuthFlowTests.SeededAdmin_MustChangePassword_IsSentToTheChangeScreen`; `ChangingPassword_ClearsMustChangeFlag_AndKeepsUserSignedIn` | ✓ |
| Unauthenticated request to a protected page redirects rather than 500s | `WebHostSmokeTests.UnauthenticatedRequest_ToAProtectedPage_RedirectsToLogin`; `SecurityHeadersTests.ProtectedPage_WithoutAuth_RedirectsRatherThan500` | ✓ |
