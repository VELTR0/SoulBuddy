using System.Text;
using System.Text.Json;
using SoulBuddy.Models;

namespace SoulBuddy.Services;

public sealed class SessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _sessionPath;
    private readonly string _activeSessionPath;

    public SessionStore(string? baseDirectory = null)
    {
        var root = baseDirectory ?? Path.Combine(AppContext.BaseDirectory, "data");
        _sessionPath = Path.Combine(root, "local-player.json");
        _activeSessionPath = Path.Combine(root, "active-player.json");
        Directory.CreateDirectory(root);
    }

    public async Task<SessionContext> StartAsync(
        string playerName,
        string soullockeLink,
        string soullockePassword,
        bool showMainWindow = true,
        CancellationToken cancellationToken = default)
    {
        ValidatePlayerName(playerName);

        var normalizedName = playerName.Trim();
        var existing = await LoadSessionAsync(cancellationToken);
        var localPlayer = existing?.Players.FirstOrDefault(player =>
            string.Equals(player.DisplayName, normalizedName, StringComparison.OrdinalIgnoreCase));

        if (localPlayer is null)
        {
            localPlayer = new SessionPlayer
            {
                Id = Guid.NewGuid().ToString("N"),
                DisplayName = normalizedName,
                Slot = 1
            };
        }
        else
        {
            localPlayer.DisplayName = normalizedName;
        }

        var session = existing ?? new SoulLinkSession();
        session.Players.Clear();
        session.Players.Add(localPlayer);
        session.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await SaveSessionAsync(session, cancellationToken);
        await SaveActiveSessionAsync(
            localPlayer.Id,
            soullockeLink,
            soullockePassword,
            showMainWindow,
            cancellationToken);

        return new SessionContext
        {
            Session = session,
            LocalPlayer = localPlayer,
            LaunchMode = SessionLaunchMode.Auto,
            SoullockeEnabled = true,
            SoullockeLink = soullockeLink.Trim(),
            SoullockePassword = soullockePassword,
            ShowMainWindow = showMainWindow
        };
    }

    public async Task<SessionContext?> LoadActiveAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_activeSessionPath))
            return null;

        await using var activeStream = File.OpenRead(_activeSessionPath);
        var active = await JsonSerializer.DeserializeAsync<ActiveSession>(
            activeStream,
            JsonOptions,
            cancellationToken);

        if (active is null)
            return null;

        var session = await LoadSessionAsync(cancellationToken);
        var player = session?.Players.FirstOrDefault(item => item.Id == active.PlayerId);

        if (session is null || player is null)
            return null;

        return new SessionContext
        {
            Session = session,
            LocalPlayer = player,
            LaunchMode = SessionLaunchMode.Auto,
            SoullockeEnabled = true,
            SoullockeLink = active.SoullockeLink,
            SoullockePassword = active.SoullockePassword,
            ShowMainWindow = active.ShowMainWindow ?? true
        };
    }

    private async Task<SoulLinkSession?> LoadSessionAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_sessionPath))
            return null;

        await using var stream = File.OpenRead(_sessionPath);
        return await JsonSerializer.DeserializeAsync<SoulLinkSession>(
            stream,
            JsonOptions,
            cancellationToken);
    }

    private async Task SaveSessionAsync(SoulLinkSession session, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(session, JsonOptions);
        await WriteAtomicallyAsync(_sessionPath, json, cancellationToken);
    }

    private async Task SaveActiveSessionAsync(
        string playerId,
        string soullockeLink,
        string soullockePassword,
        bool showMainWindow,
        CancellationToken cancellationToken)
    {
        var active = new ActiveSession
        {
            PlayerId = playerId,
            SoullockeEnabled = true,
            SoullockeLink = soullockeLink.Trim(),
            SoullockePassword = soullockePassword,
            ShowMainWindow = showMainWindow
        };
        var json = JsonSerializer.Serialize(active, JsonOptions);
        await WriteAtomicallyAsync(_activeSessionPath, json, cancellationToken);
    }

    private static void ValidatePlayerName(string playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName))
            throw new ArgumentException("Bitte gib einen Spielernamen ein.", nameof(playerName));
    }

    private static async Task WriteAtomicallyAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            content,
            new UTF8Encoding(false),
            cancellationToken);
        File.Move(temporaryPath, path, true);
    }
}
