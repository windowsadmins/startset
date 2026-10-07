namespace StartSet.Engine;

/// <summary>
/// Decides whether a logon event is a user arriving at a desktop, and makes sure each
/// desktop sign-in runs the login payloads once.
/// </summary>
/// <remarks>
/// Event 4624 with an interactive logon type is written for far more than a person
/// signing in. Task Scheduler, services calling LogonUser and runas all produce them,
/// and an administrator's own sign-in produces two (the filtered and the elevated
/// token). Each one used to run the whole login batch against the console session, so
/// anything that logged on repeatedly -- a scheduled task every minute -- ran the login
/// payloads every minute.
///
/// A desktop sign-in is identified by the session it lands in, the user signed in to it
/// and the time they signed in. The first logon event for that sign-in runs the batch;
/// every other logon event in the session until the next sign-in is ignored.
/// </remarks>
public sealed class LoginGate
{
    private readonly object _lock = new();
    private readonly Dictionary<int, (string User, DateTime? LogonTime)> _handled = new();

    /// <summary>
    /// Whether a logon in <paramref name="logonSessionId"/> (the desktop session the
    /// logon session is in, from LSA) can be a desktop sign-in at all. Returns the reason
    /// when it cannot.
    /// </summary>
    public static string? RejectBeforeWaiting(int logonSessionId)
    {
        if (logonSessionId == Native.LogonSessions.NoSuchLogonSession)
            return "its logon session had already ended, so it was not a desktop sign-in";
        if (logonSessionId == 0)
            return "it is in session 0, where services and scheduled tasks run and no desktop exists";
        return null;
    }

    /// <summary>
    /// Claims the desktop sign-in for the login batch. Returns null when the caller
    /// should run the batch, or the reason it should not.
    /// </summary>
    /// <param name="logonUser">TargetUserName from the event, or null for the start-up catch-up.</param>
    /// <param name="sessionId">The desktop session.</param>
    /// <param name="sessionUser">The user signed in to that session now.</param>
    /// <param name="sessionLogonTime">When that user signed in to the session.</param>
    public string? Claim(string? logonUser, int sessionId, string? sessionUser, DateTime? sessionLogonTime)
    {
        if (string.IsNullOrWhiteSpace(sessionUser))
            return $"nobody is signed in to session {sessionId}";

        if (logonUser != null && !string.Equals(StripDomain(logonUser), StripDomain(sessionUser), StringComparison.OrdinalIgnoreCase))
            return $"session {sessionId} belongs to {sessionUser}, so this was another account logging on inside it (runas or an elevation prompt)";

        lock (_lock)
        {
            var signIn = (StripDomain(sessionUser).ToUpperInvariant(), sessionLogonTime);
            if (_handled.TryGetValue(sessionId, out var handled) && handled == signIn)
                return $"the login payloads already ran for {sessionUser}'s sign-in to session {sessionId}";

            _handled[sessionId] = signIn;
            return null;
        }
    }

    private static string StripDomain(string name)
    {
        var slash = name.LastIndexOf('\\');
        return slash >= 0 ? name[(slash + 1)..] : name;
    }
}
