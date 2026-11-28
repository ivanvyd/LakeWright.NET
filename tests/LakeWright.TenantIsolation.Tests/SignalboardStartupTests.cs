using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Signalboard;

namespace LakeWright.TenantIsolation.Tests;

/// <summary>
/// The sample's header authentication believes any subject a caller names. It must not start on a
/// host real users can reach unless someone opted in on purpose.
/// </summary>
public sealed class SignalboardStartupTests
{
    [Fact]
    public void A_production_host_refuses_demo_authentication_without_the_explicit_opt_in()
    {
        using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseEnvironment("Production")
            .UseSetting("ConnectionStrings:LakeWright", "Host=unused.invalid;Database=unused"));

        var failure = Should.Throw<InvalidOperationException>(() => app.CreateClient());

        failure.Message.ShouldContain(DemoAuthenticationExtensions.AllowSetting);
    }
}
