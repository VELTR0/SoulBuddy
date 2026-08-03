using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SoulBuddy.Models;

namespace SoulBuddy.Services;

public sealed class SessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _sessionsDirectory;
    private readonly string _activeSessionPath;

    public SessionStore(string? baseDirectory = null)
    {
        var root = baseDirectory ?? Path.Combine(AppContext.BaseDirectory, "data");
        _sessionsDirectory = Path.Combine(root, "sessions");
        _activeSessionPath = Path.Combine(root, "active-session.json");
        Directory.CreateDirectory(_sessionsDirectory);
    }

    public static string NormalizeSessionId(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        normalized = Regex.Replace(normalized, "[^a-z0-9]+", "-");
        normalized = Regex.Replace(normalized, "-+", "-").Trim('-');

        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Die Session-ID enthält keine gültigen Zeichen.", nameof(value));
        }

        return normalized;
    }

    public async Task<SessionContext> CreateAsync(
        string sessionId,
        string sessionName,
        string playerName,
        CancellationToken cancellationToken = default)
    {
        var normalizedId = NormalizeSessionId(sessionId);
        ValidatePlayerName(playerName);

        var path = GetSessionPath(normalizedId);
        if (File.Exists(path))
        {
            throw new InvalidOperationException("Diese Session existiert lokal bereits. Nutze stattdessen ‚Beitreten‘.");
        }

        var localPlayer = CreatePlayer(playerName, 1);
        var session = new SoulLinkSession
        {
            Id = normalizedId,
            Name = string.IsNullOrWhiteSpace(sessionName) ? normalizedId : sessionName.Trim(),
            Players = [localPlayer]
        };

        await SaveSessionAsync(session, cancellationToken);
        await SaveActiveSessionAsync(session.Id, localPlayer.Id, cancellationToken);

        return new SessionContext
        {
            Session = session,
            LocalPlayer = localPlayer,
            LaunchMode = SessionLaunchMode.Host
        };
    }

    public async Task<SessionContext> JoinAsync(
        string sessionId,
        string playerName,
        CancellationToken cancellationToken = default)
    {
        var normalizedId = NormalizeSessionId(sessionId);
        ValidatePlayerName(playerName);

        var session = await LoadSessionAsync(normalizedId, cancellationToken)
            ?? new SoulLinkSession
            {
                Id = normalizedId,
                Name = normalizedId
            };

        var existingPlayer = session.Players.FirstOrDefault(player =>
            string.Equals(player.DisplayName, playerName.Trim(), StringComparison.OrdinalIgnoreCase));

        SessionPlayer localPlayer;
        if (existingPlayer is not null)
        {
            localPlayer = existingPlayer;
        }
        else
        {
            if (session.Players.Count >= 2)
            {
                throw new InvalidOperationException("Diese SoulLink-Session hat bereits zwei Spieler.");
            }

            localPlayer = CreatePlayer(playerName, session.Players.Count + 1);
            session.Players.Add(localPlayer);
            session.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await SaveSessionAsync(session, cancellationToken);
        }

        await SaveActiveSessionAsync(session.Id, localPlayer.Id, cancellationToken);

        return new SessionContext
        {
            Session = session,
            LocalPlayer = localPlayer,
            LaunchMode = SessionLaunchMode.Join
        };
    }

    public async Task<SessionContext?> LoadActiveAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_activeSessionPath))
        {
            return null;
        }

        await using var activeStream = File.OpenRead(_activeSessionPath);
        var active = await JsonSerializer.DeserializeAsync<ActiveSession>(
            activeStream,
            JsonOptions,
            cancellationToken);

        if (active is null)
        {
            return null;
        }

        var session = await LoadSessionAsync(active.SessionId, cancellationToken);
        var player = session?.Players.FirstOrDefault(item => item.Id == active.PlayerId);

        if (session is null || player is null)
        {
            return null;
        }

        return new SessionContext
        {
            Session = session,
            LocalPlayer = player,
            LaunchMode = SessionLaunchMode.Continue
        };
    }

    public async Task<SoulLinkSession?> LoadSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var normalizedId = NormalizeSessionId(sessionId);
        var path = GetSessionPath(normalizedId);

        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<SoulLinkSession>(
            stream,
            JsonOptions,
            cancellationToken);
    }

    private async Task SaveSessionAsync(
        SoulLinkSession session,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(session, JsonOptions);
        await WriteAtomicallyAsync(GetSessionPath(session.Id), json, cancellationToken);
    }

    private async Task SaveActiveSessionAsync(
        string sessionId,
        string playerId,
        CancellationToken cancellationToken)
    {
        var active = new ActiveSession
        {
            SessionId = sessionId,
            PlayerId = playerId
        };
        var json = JsonSerializer.Serialize(active, JsonOptions);
        await WriteAtomicallyAsync(_activeSessionPath, json, cancellationToken);
    }

    private static SessionPlayer CreatePlayer(string displayName, int slot)
    {
        return new SessionPlayer
        {
            Id = Guid.NewGuid().ToString("N"),
            DisplayName = displayName.Trim(),
            Slot = slot
        };
    }

    private static void ValidatePlayerName(string playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName))
        {
            throw new ArgumentException("Bitte gib einen Spielernamen ein.", nameof(playerName));
        }
    }

    private string GetSessionPath(string sessionId) =>
        Path.Combine(_sessionsDirectory, $"{sessionId}.json");

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
