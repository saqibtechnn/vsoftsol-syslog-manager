using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Security;

/// <summary>
/// PHASE_12 build item 2 — walks the whole 5-step wizard exactly as a browser would (each
/// step is a real HTTP POST with its own antiforgery token, matching the static-SSR form
/// pattern <c>AuthFlowTests</c> already exercises for <c>/login</c>), then confirms the
/// admin ends up authenticated and setup can never be reached again.
/// </summary>
public sealed partial class FirstRunWizardTests
{
    [Fact]
    public async Task CompletingAllFiveSteps_EndsSignedIn_AndNeverShowsSetupAgain()
    {
        await using var factory = new SyslogWebApplicationFactory { SeedCompletedSetup = false };
        HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

        // Step 1 — admin password.
        string page = await client.GetStringAsync("/setup");
        page.Should().Contain("administrator password");
        await PostStepAsync(client, "setup-password", page, new()
        {
            ["PasswordModel.Password"] = "Correct-Horse-Battery-Staple-1",
            ["PasswordModel.ConfirmPassword"] = "Correct-Horse-Battery-Staple-1",
        });

        // Step 2 — listener ports (three distinct, non-default ports).
        page = await client.GetStringAsync("/setup");
        page.Should().Contain("Listener ports");
        await PostStepAsync(client, "setup-ports", page, new()
        {
            ["PortsModel.UdpPort"] = "1514",
            ["PortsModel.TcpPort"] = "1601",
            ["PortsModel.WebHttpsPort"] = "8443",
        });

        // Step 3 — data directory, informational only.
        page = await client.GetStringAsync("/setup");
        page.Should().Contain("Data directory");
        await PostStepAsync(client, "setup-datadir", page, []);

        // Step 4 — retention preset.
        page = await client.GetStringAsync("/setup");
        page.Should().Contain("Retention");
        await PostStepAsync(client, "setup-retention", page, new() { ["RetentionModel.PresetKey"] = "large" });

        // Step 5 — finish, no bundle.
        page = await client.GetStringAsync("/setup");
        page.Should().Contain("Import a vendor configuration");
        string token = TokenRegex().Match(page).Groups["v"].Value;
        var content = new MultipartFormDataContent
        {
            { new StringContent("setup-finish"), "_handler" },
            { new StringContent(token), "__RequestVerificationToken" },
        };
        HttpResponseMessage finish = await client.PostAsync("/setup", content);

        finish.StatusCode.Should().Be(HttpStatusCode.Redirect);
        finish.Headers.Location!.ToString().Should().EndWith("/waiting-for-messages");

        // The auth cookie from CompleteFirstRunSetupAsync's sign-in must actually work now.
        HttpResponseMessage dashboards = await client.GetAsync("/dashboards");
        dashboards.StatusCode.Should().Be(HttpStatusCode.OK, "the wizard signs the admin in on finish");

        // Setup can never be reached again once it has completed.
        HttpResponseMessage setupAgain = await client.GetAsync("/setup");
        setupAgain.StatusCode.Should().Be(HttpStatusCode.Redirect);
        setupAgain.Headers.Location!.ToString().Should().Be("/dashboards");
    }

    private static async Task<HttpResponseMessage> PostStepAsync(
        HttpClient client, string handler, string page, Dictionary<string, string> fields)
    {
        string token = TokenRegex().Match(page).Groups["v"].Value;
        fields["_handler"] = handler;
        fields["__RequestVerificationToken"] = token;
        return await client.PostAsync("/setup", new FormUrlEncodedContent(fields));
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<v>[^\"]+)\"")]
    private static partial Regex TokenRegex();
}
