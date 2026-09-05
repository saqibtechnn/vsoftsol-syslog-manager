using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace VSoftSol.Syslog.Web.Security;

/// <summary>
/// Baseline response security headers. Phase 4 tightens the CSP (removes
/// <c>'unsafe-inline'</c> from <c>style-src</c>, adds nonces) and asserts every header
/// with a test; this is the Phase 0 floor so nothing ships header-less.
/// </summary>
internal sealed class SecurityHeadersMiddleware
{
    // TODO(phase-4): replace 'unsafe-inline' style-src with per-response nonces and
    // assert the full header set (CSP, HSTS, X-Content-Type-Options, Referrer-Policy,
    // frame-ancestors) with an automated test.
    private const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "img-src 'self' data:; " +
        "font-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "script-src 'self'; " +
        "connect-src 'self'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'";

    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext context)
    {
        IHeaderDictionary headers = context.Response.Headers;
        headers["Content-Security-Policy"] = ContentSecurityPolicy;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["X-Frame-Options"] = "DENY";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers.Remove("X-Powered-By");
        headers.Remove("Server");
        return _next(context);
    }
}

internal static class SecurityHeadersMiddlewareExtensions
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.UseMiddleware<SecurityHeadersMiddleware>();
}
