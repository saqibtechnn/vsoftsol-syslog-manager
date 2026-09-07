using System.Net;
using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using VSoftSol.Syslog.Web.Security;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Auth;

/// <summary>
/// PHASE_04 "Exhaustive authorization matrix": generated from route discovery, so a new
/// page added without a policy fails this test (a hand-maintained list would rot). Also
/// runs the 4 roles × every protected route access check against the live app.
/// </summary>
public sealed class AuthorizationMatrixTests : IClassFixture<SyslogWebApplicationFactory>
{
    private static readonly string[] AnonymousAllowList = ["/login", "/Error"];

    private readonly SyslogWebApplicationFactory _factory;

    public AuthorizationMatrixTests(SyslogWebApplicationFactory factory) => _factory = factory;

    public sealed record RouteInfo(string Template, string? Policy, bool AllowAnonymous, bool AuthenticatedOnly);

    public static IReadOnlyList<RouteInfo> DiscoverRoutes()
    {
        Assembly web = typeof(PrincipalFactory).Assembly;
        var routes = new List<RouteInfo>();

        foreach (Type type in web.GetTypes().Where(t => typeof(IComponent).IsAssignableFrom(t)))
        {
            IEnumerable<RouteAttribute> routeAttrs = type.GetCustomAttributes<RouteAttribute>();
            if (!routeAttrs.Any())
            {
                continue;
            }

            bool allowAnon = type.GetCustomAttribute<AllowAnonymousAttribute>() is not null;
            AuthorizeAttribute? authorize = type.GetCustomAttribute<AuthorizeAttribute>();

            foreach (RouteAttribute route in routeAttrs)
            {
                routes.Add(new RouteInfo(
                    route.Template,
                    authorize?.Policy,
                    allowAnon,
                    authorize is not null && authorize.Policy is null));
            }
        }

        return routes;
    }

    [Fact]
    public void EveryRoutableComponent_EitherAllowsAnonymousOrRequiresAKnownPolicy()
    {
        var offenders = new List<string>();
        HashSet<string> known = [.. AuthPolicies.All, "Authenticated"];

        foreach (RouteInfo route in DiscoverRoutes())
        {
            if (route.AllowAnonymous)
            {
                AnonymousAllowList.Should().Contain(route.Template,
                    $"anonymous access to '{route.Template}' must be an explicit, reviewed exception");
                continue;
            }

            if (route.AuthenticatedOnly)
            {
                continue; // [Authorize] with no policy — authenticated users only
            }

            if (route.Policy is null || !known.Contains(route.Policy))
            {
                offenders.Add($"{route.Template} → policy '{route.Policy ?? "(none)"}'");
            }
        }

        offenders.Should().BeEmpty("every protected route must carry [Authorize(Policy = <known>)] or [Authorize]");
    }

    [Fact]
    public void DiscoveredRoutes_CoverEveryPrimaryNavDestination()
    {
        IEnumerable<string> templates = DiscoverRoutes().Select(r => r.Template);
        foreach (var item in VSoftSol.Syslog.Web.Components.Layout.NavItems.Primary)
        {
            templates.Should().Contain(item.Href, $"nav points at {item.Href}");
        }
    }

    [Theory]
    [MemberData(nameof(RoleRouteCombinations))]
    public async Task Role_GetsExpectedAccessTo_Route(string roleName, string template, bool shouldAllow)
    {
        var role = Enum.Parse<Role>(roleName);
        string route = template.Replace("{", string.Empty, StringComparison.Ordinal).Replace("}", string.Empty, StringComparison.Ordinal);

        var auth = new WebAuthClient(_factory);
        string user = $"{roleName}-{Math.Abs(route.GetHashCode()) % 100000}";
        await auth.SeedUserAsync(user, "matrix-Test-Password-1", role);
        await auth.LoginAsync(user, "matrix-Test-Password-1");

        HttpResponseMessage response = await auth.Client.GetAsync(route);

        if (shouldAllow)
        {
            response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Redirect);
            if (response.StatusCode == HttpStatusCode.Redirect)
            {
                response.Headers.Location!.ToString().Should().NotContain("/denied").And.NotContain("/login");
            }
        }
        else
        {
            // Denied: either 403, or a redirect to the access-denied page.
            if (response.StatusCode == HttpStatusCode.Redirect)
            {
                response.Headers.Location!.ToString().Should().Contain("denied");
            }
            else
            {
                response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            }
        }
    }

    public static IEnumerable<object?[]> RoleRouteCombinations()
    {
        foreach (RouteInfo route in DiscoverRoutes())
        {
            if (route.AllowAnonymous || route.Template.Contains('{', StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Role role in Enum.GetValues<Role>())
            {
                bool allow = route.Policy is null
                    ? true // authenticated-only
                    : AuthPolicies.RolesFor.TryGetValue(route.Policy, out IReadOnlyList<Role>? roles) && roles.Contains(role);

                yield return [role.ToString(), route.Template, allow];
            }
        }
    }
}
