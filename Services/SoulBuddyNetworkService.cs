namespace SoulBuddy.Services;

public enum SoulBuddyNetworkMode
{
    None,
    Host,
    Join
}

public enum SoulBuddyNetworkState
{
    Idle,
    Prepared
}

public sealed class SoulBuddyNetworkService : IAsyncDisposable
{
    public SoulBuddyNetworkMode Mode { get; private set; } =
        SoulBuddyNetworkMode.None;

    public SoulBuddyNetworkState State { get; private set; } =
        SoulBuddyNetworkState.Idle;

    public string SessionId { get; private set; } = string.Empty;

    public string PlayerName { get; private set; } = string.Empty;

    public string StatusText { get; private set; } =
        "Netzwerk noch nicht gestartet.";

    public event EventHandler? StatusChanged;

    public void PrepareHost(string sessionId, string playerName)
    {
        Prepare(
            SoulBuddyNetworkMode.Host,
            sessionId,
            playerName,
            $"Host vorbereitet · Session {sessionId} · Verbindung folgt im nächsten Schritt.");
    }

    public void PrepareJoin(string sessionId, string playerName)
    {
        Prepare(
            SoulBuddyNetworkMode.Join,
            sessionId,
            playerName,
            $"Beitritt vorbereitet · Session {sessionId} · Verbindung folgt im nächsten Schritt.");
    }

    public void Reset()
    {
        Mode = SoulBuddyNetworkMode.None;
        State = SoulBuddyNetworkState.Idle;
        SessionId = string.Empty;
        PlayerName = string.Empty;
        StatusText = "Netzwerk noch nicht gestartet.";
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Prepare(
        SoulBuddyNetworkMode mode,
        string sessionId,
        string playerName,
        string statusText)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new InvalidOperationException(
                "Für die Netzwerkvorbereitung wird eine Session-ID benötigt.");
        }

        if (string.IsNullOrWhiteSpace(playerName))
        {
            throw new InvalidOperationException(
                "Für die Netzwerkvorbereitung wird ein Spielername benötigt.");
        }

        Mode = mode;
        State = SoulBuddyNetworkState.Prepared;
        SessionId = sessionId.Trim();
        PlayerName = playerName.Trim();
        StatusText = statusText;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public ValueTask DisposeAsync()
    {
        Reset();
        return ValueTask.CompletedTask;
    }
}
