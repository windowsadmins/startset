using StartSet.Core.Enums;
using StartSet.Engine;
using StartSet.Engine.Processors;
using StartSet.Core.Models;
using StartSet.Tests.Helpers;

namespace StartSet.Tests.Integration;

public class UserSessionRoutingTests
{
    [Theory]
    [InlineData(".ps1")]
    [InlineData(".cmd")]
    [InlineData(".bat")]
    [InlineData(".exe")]
    public async Task UnavailableTargetIsDeferredThroughDiscoveryAndEveryUserProcessor(string extension)
    {
        using var temp = new TempDirectory();
        temp.CreateFile(Path.Combine("login-every", "Marker" + extension), "exit 0");
        var engine = new ExecutionEngine(TestSettings.Service(), temp.Path, TestSettings.TrustAll);

        // Session 0 has no desktop. Using an explicit invalid target also ensures
        // this test cannot accidentally launch anything in the runner's console.
        var result = Assert.Single(await engine.ExecuteAsync(
            [PayloadType.LoginEvery], waitForNetwork: false, sessionId: 0));

        Assert.Equal(ExecutionStatus.Deferred, result.Status);
        Assert.Null(result.ExitCode);
        Assert.Contains("session 0", result.ErrorMessage);
    }

    [Theory]
    [InlineData(PayloadType.BootEvery)]
    [InlineData(PayloadType.LoginPrivilegedEvery)]
    [InlineData(PayloadType.OnDemandPrivileged)]
    public void PrivilegedPayloadsDoNotBorrowTheTargetUserToken(PayloadType type)
    {
        var script = new ScriptPayload { FilePath = "Marker.cmd", PayloadType = type };

        Assert.Null(UserContextExecution.TryRunAsUser(
            script, "cmd.exe", "/c exit 0", ".", TimeSpan.Zero, DateTimeOffset.UtcNow, sessionId: 0));
    }
}
