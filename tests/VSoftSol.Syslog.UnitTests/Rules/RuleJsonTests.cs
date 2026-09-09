using FluentAssertions;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Data.Rules;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Rules;

public sealed class RuleJsonTests
{
    [Fact]
    public void Actions_RoundTripPolymorphically()
    {
        var actions = new List<RuleAction>
        {
            new AddTagAction { Tag = "hot" },
            new SendEmailAction
            {
                Host = "smtp.example.com", From = "a@example.com", To = ["b@example.com"],
                Subject = "{severity}", Body = "{message}", SecretName = "pw",
                Throttle = new ActionThrottle(10, 3600, 60),
            },
            new SuppressAction(),
        };

        string json = RuleJson.SerializeActions(actions);
        List<RuleAction> back = RuleJson.DeserializeActions(json);

        back.Should().HaveCount(3);
        back[0].Should().BeOfType<AddTagAction>().Which.Tag.Should().Be("hot");

        ((SendEmailAction)back[1]).To.Should().ContainSingle().Which.Should().Be("b@example.com");
        ((SendEmailAction)back[1]).Throttle.CooldownSeconds.Should().Be(60);
        back[2].Should().BeOfType<SuppressAction>();
    }
}
