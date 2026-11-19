using Microsoft.Win32.SafeHandles;
using StartSet.Engine.Native;

namespace StartSet.Tests.Engine;

public class UserSessionTokenTests
{
    [Fact]
    public void RemoteSignInUsesItsTokenWithoutConsultingTheConsole()
    {
        var queried = new List<int>();
        var provider = new UserSessionTokenProvider(
            () => throw new InvalidOperationException("The console belongs to another user"),
            (int session, out SafeAccessTokenHandle token) =>
            {
                queried.Add(session);
                token = SafeAccessTokenHandle.InvalidHandle;
                return true;
            }, TimeSpan.Zero, TimeSpan.Zero);

        Assert.True(provider.TryGetToken(2, out var token, out var reason));
        using (token)
        {
            Assert.Equal(new[] { 2 }, queried);
            Assert.Empty(reason);
        }
    }

    [Fact]
    public void UnavailableRemoteSessionNeverFallsBackToTheConsole()
    {
        var queried = new List<int>();
        var provider = new UserSessionTokenProvider(
            () => 1,
            (int session, out SafeAccessTokenHandle token) =>
            {
                queried.Add(session);
                token = SafeAccessTokenHandle.InvalidHandle;
                return session == 1;
            }, TimeSpan.Zero, TimeSpan.Zero);

        Assert.False(provider.TryGetToken(2, out _, out var reason));
        Assert.Equal(new[] { 2 }, queried);
        Assert.Contains("session 2", reason);
    }

    [Fact]
    public void TokenRetriesStayInTheRemoteSession()
    {
        var queried = new List<int>();
        var provider = new UserSessionTokenProvider(
            () => 1,
            (int session, out SafeAccessTokenHandle token) =>
            {
                queried.Add(session);
                token = SafeAccessTokenHandle.InvalidHandle;
                return queried.Count == 3;
            }, TimeSpan.FromSeconds(10), TimeSpan.Zero);

        Assert.True(provider.TryGetToken(2, out var token, out _));
        token.Dispose();
        Assert.Equal(new[] { 2, 2, 2 }, queried);
    }

    [Fact]
    public void ConsoleFallbackIsResolvedOnceEvenIfTheConsoleChangesDuringRetry()
    {
        var consoleLookups = 0;
        var queried = new List<int>();
        var provider = new UserSessionTokenProvider(
            () => ++consoleLookups,
            (int session, out SafeAccessTokenHandle token) =>
            {
                queried.Add(session);
                token = SafeAccessTokenHandle.InvalidHandle;
                return queried.Count == 2;
            }, TimeSpan.FromSeconds(10), TimeSpan.Zero);

        Assert.True(provider.TryGetToken(null, out var token, out _));
        token.Dispose();
        Assert.Equal(1, consoleLookups);
        Assert.Equal(new[] { 1, 1 }, queried);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void InvalidExplicitSessionDoesNotSelectAnotherUser(int sessionId)
    {
        var provider = new UserSessionTokenProvider(
            () => throw new InvalidOperationException("Must not select the console"),
            (int session, out SafeAccessTokenHandle token) => throw new InvalidOperationException("Must not query a token"),
            TimeSpan.Zero, TimeSpan.Zero);

        Assert.False(provider.TryGetToken(sessionId, out _, out var reason));
        Assert.Contains($"session {sessionId}", reason);
    }

    [Fact]
    public void ExhaustedConsoleWaitDoesNotSuppressRemoteTokenRetries()
    {
        var remoteAttempts = 0;
        var provider = new UserSessionTokenProvider(
            () => 1,
            (int session, out SafeAccessTokenHandle token) =>
            {
                token = SafeAccessTokenHandle.InvalidHandle;
                if (session == 1)
                    return false;
                return ++remoteAttempts >= 2;
            }, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1));

        Assert.False(provider.TryGetToken(1, out _, out _));
        Assert.True(provider.TryGetToken(2, out var token, out _));
        token.Dispose();
        Assert.Equal(2, remoteAttempts);
    }
}
