using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace VSoftSol.Syslog.IntegrationTests.TestSupport;

/// <summary>
/// Renders a self-contained Razor component to an HTML string with no browser and no
/// AngleSharp (bunit is banned here — see the dev-vm-constraints note). Uses the ASP.NET
/// shared framework's <see cref="HtmlRenderer"/>. For pure presentational components only —
/// components that inject services need those registered in <paramref name="configure"/>.
/// </summary>
public static class RazorRenderer
{
    public static async Task<string> RenderAsync<TComponent>(
        Dictionary<string, object?> parameters, Action<IServiceCollection>? configure = null)
        where TComponent : IComponent
    {
        var services = new ServiceCollection();
        services.AddLogging();
        configure?.Invoke(services);
        await using ServiceProvider provider = services.BuildServiceProvider();

        await using var renderer = new HtmlRenderer(provider, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<TComponent>(ParameterView.FromDictionary(parameters));
            return root.ToHtmlString();
        });
    }
}
