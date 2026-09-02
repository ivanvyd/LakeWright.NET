using LakeWright.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LakeWright.TenantIsolation.Tests;

public sealed class LakeWrightOptionsTests
{
    [Fact]
    public async Task Reports_all_registered_option_failures_once_at_host_start()
    {
        var services = new ServiceCollection();
        services.AddOptions<FirstOptions>().Validate(_ => false, "LakeWright:First:Value is required.");
        services.AddOptions<SecondOptions>().Validate(_ => false, "LakeWright:Second:Value is required.");
        LakeWrightOptions.ValidateOnStart<FirstOptions>(services);
        LakeWrightOptions.ValidateOnStart<SecondOptions>(services);
        using var provider = services.BuildServiceProvider();
        var validator = provider.GetServices<IHostedService>().Single();

        var exception = await Should.ThrowAsync<Microsoft.Extensions.Options.OptionsValidationException>(
            () => validator.StartAsync(TestContext.Current.CancellationToken));

        exception.Failures.ShouldBe([
            "LakeWright:First:Value is required.",
            "LakeWright:Second:Value is required.",
        ]);
    }

    private sealed class FirstOptions;
    private sealed class SecondOptions;
}
