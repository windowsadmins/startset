using StartSet.Infrastructure.Logging;
using Xunit;

namespace StartSet.Tests.Infrastructure;

/// <summary>
/// The naming of a session directory, and what still counts as one when reading them back.
/// </summary>
/// <remarks>
/// A logon starts three sessions inside the same minute - boot, login-window and login -
/// and the directory name used to be the minute alone, so two of them landed in HHMM_2 and
/// HHMM_3 with nothing to say which was which. Reading "the newest" then gave the wrong
/// session about as often as the right one. The run type is in the name to end that, and
/// these tests hold both halves of the change together: the name that gets written, and the
/// matcher that has to keep recognising every name already on disk.
/// </remarks>
public class SessionDirectoryNamingTests
{
    [Theory]
    [InlineData("1430-login")]
    [InlineData("0000-boot")]
    [InlineData("2359-on-demand")]
    [InlineData("0800-login_2")]
    public void RecognisesRunTypedSessionDirectories(string name)
    {
        Assert.True(SessionLogger.IsTimeSessionDirectory(name));
    }

    [Theory]
    [InlineData("1430")]
    [InlineData("1430_2")]
    public void StillRecognisesTheLegacyNames(string name)
    {
        // Every machine has these on disk already. A name the matcher rejects is not a
        // session, so those directories would stop being enumerated - invisible to anyone
        // looking for them, and never aged out.
        Assert.True(SessionLogger.IsTimeSessionDirectory(name));
    }

    [Theory]
    [InlineData("2400-login")]   // not a time
    [InlineData("9999")]         // not a time
    [InlineData("0800login")]    // no separator
    [InlineData("0800-")]        // no run type
    [InlineData("reports")]      // a sibling that is not a session
    [InlineData("080")]          // too short
    public void RejectsWhatIsNotASessionDirectory(string name)
    {
        Assert.False(SessionLogger.IsTimeSessionDirectory(name));
    }

    [Theory]
    [InlineData("login", "login")]
    [InlineData("On-Demand", "on-demand")]
    [InlineData("login window", "login-window")]
    [InlineData("", "session")]
    [InlineData("  ", "session")]
    public void RunTypeIsReducedToSomethingSafeForAPathSegment(string runType, string expected)
    {
        Assert.Equal(expected, SessionLogger.SanitizeRunType(runType));
    }

    [Fact]
    public void ASanitisedRunTypeIsAlwaysAcceptedBackByTheMatcher()
    {
        // The writer and the reader have to agree, or a session logs into a directory
        // nothing will ever enumerate.
        foreach (var runType in new[] { "boot", "login", "login-window", "on-demand", "service", "cli", "manual" })
        {
            var name = $"1430-{SessionLogger.SanitizeRunType(runType)}";
            Assert.True(SessionLogger.IsTimeSessionDirectory(name), name);
        }
    }
}
