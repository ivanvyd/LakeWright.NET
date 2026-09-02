using LakeWright.Conversations;

namespace LakeWright.TenantIsolation.Tests;

public class GenieAnswerSanitizerTests
{
    [Fact]
    public void Model_supplied_HTML_and_untrusted_links_are_neutralized()
    {
        var sanitizer = new GenieAnswerSanitizer();

        var text = sanitizer.Sanitize(
            "Open [this](javascript:alert(1)) <img src=x onerror=alert(1)>now</img>.");

        text.ShouldBe("Open this now.");
    }

    [Fact]
    public void Only_an_explicit_exact_HTTPS_host_is_preserved_as_a_link()
    {
        var sanitizer = new GenieAnswerSanitizer(["docs.example.test"]);

        var text = sanitizer.Sanitize(
            "[Allowed](https://docs.example.test/path) [Lookalike](https://docs.example.test.attacker.test/path) [Http](http://docs.example.test/path)");

        text.ShouldBe("[Allowed](https://docs.example.test/path) Lookalike Http");
    }

    [Fact]
    public void Sanitizing_an_answer_preserves_its_conversation_metadata()
    {
        var answer = new GenieAnswer("conversation", "message", GenieOutcome.Completed, "[Go](https://untrusted.test)", "SELECT 1");

        var sanitized = new GenieAnswerSanitizer().Sanitize(answer);

        sanitized.ShouldBe(answer with { Text = "Go" });
    }
}
