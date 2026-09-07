using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace VSoftSol.Syslog.Web.Security;

/// <summary>
/// Response security headers, asserted by <c>SecurityHeadersTests</c> (PHASE_04 "Security
/// headers — asserted by test, not by inspection"). The CSP has no <c>'unsafe-inline'</c>
/// (closes P0-3): a fresh per-response nonce is issued for the few framework inline
/// <c>&lt;script&gt;</c> / <c>&lt;style&gt;</c> blocks and exposed to the page via
/// <see cref="HttpContext.Items"/> / <see cref="NonceAccessor"/>.
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    internal const string NonceItemKey = "csp-nonce";

    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext context)
    {
        string nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        context.Items[NonceItemKey] = nonce;

        IHeaderDictionary headers = context.Response.Headers;
        headers["Content-Security-Policy"] =
            "default-src 'self'; " +
            "base-uri 'self'; " +
            "object-src 'none'; " +
            "frame-ancestors 'none'; " +
            "form-action 'self'; " +
            "img-src 'self' data:; " +
            "font-src 'self'; " +
            "connect-src 'self'; " +
            $"style-src 'self' 'nonce-{nonce}'; " +
            $"script-src 'self' 'nonce-{nonce}'";
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["X-Frame-Options"] = "DENY";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
        headers.Remove("X-Powered-By");
        headers.Remove("Server");
        return _next(context);
    }
}

/// <summary>Exposes the current request's CSP nonce to Razor components.</summary>
public sealed class NonceAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public NonceAccessor(IHttpContextAccessor httpContextAccessor) => _httpContextAccessor = httpContextAccessor;

    public string Value =>
        _httpContextAccessor.HttpContext?.Items.TryGetValue(SecurityHeadersMiddleware.NonceItemKey, out object? v) == true
        && v is string nonce
            ? nonce
            : string.Empty;
}

internal static class SecurityHeadersMiddlewareExtensions
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.UseMiddleware<SecurityHeadersMiddleware>();
}
