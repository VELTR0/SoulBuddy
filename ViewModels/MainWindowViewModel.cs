using System.Collections.ObjectModel;
using Avalonia.Threading;
using SoulBuddy.Data;
using SoulBuddy.Models;
using SoulBuddy.Services;

namespace SoulBuddy.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly DispatcherTimer _refreshTimer;
    private readonly LocationMapper _locationMapper = new();
    private SoulBuddyRuntime? _runtime;
    private bool _refreshInProgress;
    private bool _lastBattleState;
    private string _lastActivitySignature = string.Empty;
    private string _statusText = "SoulBuddy wird gestartet …";
    private string _connectionText = "Offline";
    private string _partyCountText = "0 / 6";
    private string _pokemonCountText = "0 Pokémon";
    private string _detailsTitle = "Kein Pokémon ausgewählt";
    private string _detailsText = "Wähle ein Pokémon aus, um seine Details anzuzeigen.";
    private string _liveEncounterTitle = "LIVE-STATUS";
    private string _liveEncounterText = "Warte auf Live-Daten aus dem Emulator …";
    private string _localPlayerStatus = "Emulator wird gesucht …";
    private string _localGameText = "Spiel: unbekannt";
    private string _localActivePokemonText = "Aktives Pokémon: wird ermittelt …";
    private string _partnerStatus = "Noch keine Netzwerkverbindung";

    public MainWindowViewModel()
    {
        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _refreshTimer.Tick += OnRefreshTimerTick;
    }

    public ObservableCollection<PokemonCardViewModel> Party { get; } = [];
    public ObservableCollection<PokemonCardViewModel> StoredPokemon { get; } = [];
    public ObservableCollection<string> ActivityFeed { get; } = [];

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ConnectionText
    {
        get => _connectionText;
        private set => SetProperty(ref _connectionText, value);
    }

    public string PartyCountText
    {
        get => _partyCountText;
        private set => SetProperty(ref _partyCountText, value);
    }

    public string PokemonCountText
    {
        get => _pokemonCountText;
        private set => SetProperty(ref _pokemonCountText, value);
    }

    public string DetailsTitle
    {
        get => _detailsTitle;
        private set => SetProperty(ref _detailsTitle, value);
    }

    public string DetailsText
    {
        get => _detailsText;
        private set => SetProperty(ref _detailsText, value);
    }

    public string LiveEncounterTitle
    {
        get => _liveEncounterTitle;
        private set => SetProperty(ref _liveEncounterTitle, value);
    }

    public string LiveEncounterText
    {
        get => _liveEncounterText;
        private set => SetProperty(ref _liveEncounterText, value);
    }

    public string LocalPlayerStatus
    {
        get => _localPlayerStatus;
        private set => SetProperty(ref _localPlayerStatus, value);
    }

    public string LocalGameText
    {
        get => _localGameText;
        private set => SetProperty(ref _localGameText, value);
    }

    public string LocalActivePokemonText
    {
        get => _localActivePokemonText;
        private set => SetProperty(ref _localActivePokemonText, value);
    }

    public string PartnerStatus
    {
        get => _partnerStatus;
        private set => SetProperty(ref _partnerStatus, value);
    }

    public async Task InitializeAsync()
    {
        try
        {
            _runtime = await SoulBuddyRuntime.CreateAsync();
            _runtime.PlayerLiveStateSource.StateChanged += OnLiveStateChanged;
            _runtime.Start();

            ConnectionText = _runtime.Config.SoullockeEnabled
                ? "Soullocke aktiviert"
                : "Lokal / Offline";
            LocalPlayerStatus = "🟢 Collector verbunden";
            LocalGameText = "Spiel: HeartGold / SoulSilver";
            PartnerStatus = "Offline · Über die Netzwerksteuerung optional verbinden";
            AddActivity("SoulBuddy gestartet");
            AddActivity("Emulator-Collector verbunden");

            StatusText = $"Collector aktiv · {_runtime.EventFilePath}";
            await RefreshAsync();
            _refreshTimer.Start();
        }
        catch (Exception ex)
        {
            LocalPlayerStatus = "🔴 Collector nicht verbunden";
            StatusText = $"Startfehler: {ex.Message}";
            AddActivity($"Startfehler: {ex.Message}");
        }
    }

    public void SelectPokemon(PokemonCardViewModel pokemon)
    {
        DetailsTitle = pokemon.DetailsTitle;
        DetailsText = pokemon.DetailsText;
    }

    private void OnLiveStateChanged(object? sender, PlayerLiveState state)
    {
        Dispatcher.UIThread.Post(() => ApplyLiveState(state));
    }

    private async void OnRefreshTimerTick(object? sender, EventArgs eventArgs)
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_runtime is null || _refreshInProgress)
        {
            return;
        }

        _refreshInProgress = true;

        try
        {
            var party = await _runtime.LivePartySource.ReadPartyAsync(CancellationToken.None);
            var stored = await _runtime.KnownPokemonStore.GetAllAsync(CancellationToken.None);
            var liveState = _runtime.PlayerLiveStateSource.Read();

            ReplaceItems(Party, CreatePartyCards(party));
            ReplaceItems(StoredPokemon, CreateStoredCards(stored));
            UpdateLiveEncounter(liveState, party);

            PartyCountText = $"{Party.Count} / 6";
            PokemonCountText = $"{StoredPokemon.Count} Pokémon";
            StatusText = $"Collector aktiv · Letzte Aktualisierung {DateTime.Now:HH:mm:ss}";

            await SynchronizeNetworkAsync(liveState);
        }
        catch (Exception ex)
        {
            StatusText = $"Aktualisierungsfehler: {ex.Message}";
        }
        finally
        {
            _refreshInProgress = false;
        }
    }

    private async Task SynchronizeNetworkAsync(PlayerLiveState liveState)
    {
        var network = SoulBuddyNetworkService.Current;
        if (network is null)
        {
            return;
        }

        if (network.State == SoulBuddyNetworkState.Connected)
        {
            ConnectionText = $"Online · {network.RemotePlayerName}";

            var active = liveState.ActivePokemon is null
                ? Party.FirstOrDefault(item => item.CurrentHp > 0)
                : null;

            var snapshot = new NetworkPlayerSnapshot
            {
                PlayerName = network.PlayerName,
                Game = LocalGameText.Replace("Spiel: ", string.Empty),
                Timestamp = DateTimeOffset.UtcNow,
                StoredPokemonCount = StoredPokemon.Count,
                ActivePokemon = liveState.ActivePokemon is not null
                    ? new NetworkPokemonSnapshot
                    {
                        SpeciesId = liveState.ActivePokemon.SpeciesId,
                        SpeciesName = liveState.ActivePokemon.SpeciesName,
                        Nickname = liveState.ActivePokemon.Nickname,
                        Level = liveState.ActivePokemon.Level,
                        CurrentHp = liveState.ActivePokemon.CurrentHp,
                        MaxHp = liveState.ActivePokemon.MaxHp
                    }
                    : active is null
                        ? null
                        : ToNetworkPokemon(active),
                Party = Party.Select(ToNetworkPokemon).ToArray()
            };

            await network.SendPlayerSnapshotAsync(snapshot);
        }
        else
        {
            ConnectionText = network.State switch
            {
                SoulBuddyNetworkState.Waiting => "Online · wartet",
                SoulBuddyNetworkState.Connecting => "Online · sucht",
                SoulBuddyNetworkState.Error => "Netzwerkfehler",
                _ => "Lokal / Offline"
            };
        }

        UpdatePartnerStatus(network.LatestRemoteSnapshot, network);
    }

    private void UpdatePartnerStatus(NetworkPlayerSnapshot? snapshot, SoulBuddyNetworkService network)
    {
        if (snapshot is null)
        {
            PartnerStatus = network.State switch
            {
                SoulBuddyNetworkState.Connected => $"🟢 {network.RemotePlayerName} verbunden · warte auf Spieldaten …",
                SoulBuddyNetworkState.Waiting => "🟡 Online · warte auf Mitspieler …",
                SoulBuddyNetworkState.Connecting => "🔍 Suche nach der Session im lokalen Netzwerk …",
                SoulBuddyNetworkState.Error => $"🔴 {network.StatusText}",
                _ => "Offline · keine Partnerdaten"
            };
            return;
        }

        var activeText = snapshot.ActivePokemon is null
            ? "Aktiv: unbekannt"
            : $"Aktiv: {snapshot.ActivePokemon.DisplayName} · Lv. {snapshot.ActivePokemon.Level} · {snapshot.ActivePokemon.CurrentHp}/{snapshot.ActivePokemon.MaxHp} KP";
        var age = DateTimeOffset.UtcNow - snapshot.Timestamp;
        var ageText = age.TotalSeconds < 2 ? "gerade eben" : $"vor {Math.Max(1, (int)age.TotalSeconds)} Sekunden";

        PartnerStatus =
            $"🟢 {snapshot.PlayerName} online\n" +
            $"Spiel: {snapshot.Game}\n" +
            $"{activeText}\n" +
            $"Team: {snapshot.Party.Count}/6 · Gespeichert: {snapshot.StoredPokemonCount}\n" +
            $"Aktualisiert: {ageText}";
    }

    private static NetworkPokemonSnapshot ToNetworkPokemon(PokemonCardViewModel pokemon) => new()
    {
        SpeciesId = pokemon.SpeciesId,
        SpeciesName = pokemon.Species,
        Nickname = pokemon.DisplayName == pokemon.Species ? string.Empty : pokemon.DisplayName,
        Level = pokemon.Level,
        CurrentHp = pokemon.CurrentHp,
        MaxHp = pokemon.MaxHp,
        Location = pokemon.Subtitle
    };

    private void ApplyLiveState(PlayerLiveState state)
    {
        LocalPlayerStatus = "🟢 Live-Daten werden empfangen";
        var active = state.ActivePokemon;
        LocalActivePokemonText = active is null
            ? "Aktives Pokémon: wird ermittelt …"
            : $"Aktiv: {DisplayPokemonName(active)} · Lv. {active.Level} · {active.CurrentHp}/{active.MaxHp} KP";

        if (state.InBattle != _lastBattleState)
        {
            AddActivity(state.InBattle ? "Kampf begonnen" : "Kampf beendet");
            _lastBattleState = state.InBattle;
        }

        var signature = state.InBattle
            ? $"battle:{state.BattleKind}:{state.Opponent?.SpeciesId}:{state.ActivePokemon?.SpeciesId}"
            : $"field:{state.LocationId}:{state.LocationName}";

        if (signature != _lastActivitySignature)
        {
            if (state.InBattle && state.Opponent is not null)
            {
                AddActivity($"Gegner erkannt: {DisplayPokemonName(state.Opponent)} Lv. {state.Opponent.Level}");
            }
            else if (!state.InBattle && !string.IsNullOrWhiteSpace(state.LocationName))
            {
                AddActivity($"Aufenthalt: {state.LocationName}");
            }
            _lastActivitySignature = signature;
        }

        UpdateLiveEncounter(state, []);
    }

    private void AddActivity(string message)
    {
        ActivityFeed.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
        while (ActivityFeed.Count > 30)
        {
            ActivityFeed.RemoveAt(ActivityFeed.Count - 1);
        }
    }

    private void UpdateLiveEncounter(PlayerLiveState state, IReadOnlyList<PartySlot> party)
    {
        var ownPokemon = state.ActivePokemon;
        if (ownPokemon is null)
        {
            var fallback = party.Where(slot => slot.Pokemon is not null)
                .OrderBy(slot => slot.SlotId)
                .Select(slot => slot.Pokemon!)
                .FirstOrDefault(pokemon => pokemon.Hp.Current > 0);
            if (fallback is not null)
            {
                ownPokemon = new LivePokemonState
                {
                    SpeciesId = fallback.Species,
                    SpeciesName = fallback.SpeciesName,
                    Nickname = fallback.Nickname,
                    Level = fallback.Level,
                    CurrentHp = fallback.Hp.Current,
                    MaxHp = fallback.Hp.Max
                };
            }
        }

        if (ownPokemon is not null)
        {
            LocalActivePokemonText = $"Aktiv: {DisplayPokemonName(ownPokemon)} · Lv. {ownPokemon.Level} · {ownPokemon.CurrentHp}/{ownPokemon.MaxHp} KP";
        }

        var location = string.IsNullOrWhiteSpace(state.LocationName)
            ? state.LocationId is null ? "Aufenthaltsort wird ermittelt" : $"Unbekannter Ort ({state.LocationId})"
            : state.LocationName;

        if (!state.InBattle)
        {
            LiveEncounterTitle = "LIVE · AUSSERHALB DES KAMPFES";
            LiveEncounterText = $"📍 {location}";
            return;
        }

        var kind = state.BattleKind switch
        {
            "trainer" => string.IsNullOrWhiteSpace(state.TrainerName) ? "Trainerkampf" : $"Trainerkampf · {state.TrainerName}",
            "wild" => "Wilder Kampf",
            _ => "Kampf erkannt"
        };
        var lines = new List<string> { $"⚔ {kind}", $"📍 {location}" };
        lines.Add(state.Opponent is null
            ? "Gegner: wird ermittelt …"
            : $"Gegner: {DisplayPokemonName(state.Opponent)} · Lv. {state.Opponent.Level} · {state.Opponent.CurrentHp}/{state.Opponent.MaxHp} KP");
        if (ownPokemon is not null)
        {
            lines.Add($"Aktiv: {DisplayPokemonName(ownPokemon)} · Lv. {ownPokemon.Level} · {ownPokemon.CurrentHp}/{ownPokemon.MaxHp} KP");
        }
        LiveEncounterTitle = "LIVE ENCOUNTER";
        LiveEncounterText = string.Join(Environment.NewLine, lines);
    }

    private static string DisplayPokemonName(LivePokemonState pokemon) =>
        string.IsNullOrWhiteSpace(pokemon.Nickname) ? pokemon.SpeciesName : pokemon.Nickname;

    private IEnumerable<PokemonCardViewModel> CreatePartyCards(IReadOnlyList<PartySlot> party)
    {
        return party.Where(slot => slot.Pokemon is not null)
            .OrderBy(slot => slot.SlotId)
            .Select(slot =>
            {
                var pokemon = slot.Pokemon!;
                var displayName = string.IsNullOrWhiteSpace(pokemon.Nickname) ? pokemon.SpeciesName : pokemon.Nickname;
                var gender = pokemon.IsGenderless ? "Geschlechtslos" : pokemon.IsFemale ? "Weiblich" : "Männlich";
                var ball = GetPokeballName(pokemon.Pokeball);
                var location = GetLocationDisplayName(pokemon.LocationMet);
                return new PokemonCardViewModel
                {
                    DisplayName = displayName,
                    Species = pokemon.SpeciesName,
                    SpeciesId = pokemon.Species,
                    Level = pokemon.Level,
                    CurrentHp = pokemon.Hp.Current,
                    MaxHp = pokemon.Hp.Max,
                    Nature = pokemon.Nature,
                    Ability = pokemon.Ability,
                    Gender = gender,
                    Pokeball = ball,
                    IsShiny = pokemon.IsShiny,
                    Subtitle = location,
                    DetailsTitle = displayName,
                    DetailsText =
                        $"Spezies: {pokemon.SpeciesName} (#{pokemon.Species})\n" +
                        $"Level: {pokemon.Level}\n" +
                        $"KP: {pokemon.Hp.Current}/{pokemon.Hp.Max}\n" +
                        $"Geschlecht: {gender}\n" +
                        $"Wesen: {ValueOrUnknown(pokemon.Nature)}\n" +
                        $"Fähigkeit: {ValueOrUnknown(pokemon.Ability)}\n" +
                        $"Pokéball: {ball}\n" +
                        $"Shiny: {(pokemon.IsShiny ? "Ja" : "Nein")}\n" +
                        $"Fanglevel: {pokemon.LevelMet}\n" +
                        $"Fangort: {location}\n\n" +
                        "Technische Daten\n" +
                        $"Fangort-ID: {pokemon.LocationMet}\n" +
                        $"PID: {pokemon.Pid}\n" +
                        $"Trainer-ID: {pokemon.OriginalTrainerId}\n" +
                        $"Secret-ID: {pokemon.OriginalTrainerSecretId}"
                };
            });
    }

    private static IEnumerable<PokemonCardViewModel> CreateStoredCards(IReadOnlyList<KnownPokemonEntry> pokemon)
    {
        return pokemon.OrderByDescending(item => item.FirstSeenAt)
            .Select(entry =>
            {
                var displayName = string.IsNullOrWhiteSpace(entry.Nickname) ? entry.Species : entry.Nickname;
                return new PokemonCardViewModel
                {
                    DisplayName = displayName,
                    Species = entry.Species,
                    SpeciesId = entry.SpeciesId,
                    Level = entry.CurrentLevel,
                    CurrentHp = entry.CurrentHp,
                    MaxHp = entry.MaxHp,
                    Subtitle = entry.SoullockeSynced ? "Soullocke synchronisiert" : entry.Location,
                    DetailsTitle = displayName,
                    DetailsText =
                        $"Spezies: {entry.Species} (#{entry.SpeciesId})\n" +
                        $"Level: {entry.CurrentLevel}\n" +
                        $"KP: {entry.CurrentHp}/{entry.MaxHp}\n" +
                        $"Fangort: {entry.Location}\n" +
                        $"Fanglevel: {entry.LevelMet}\n" +
                        $"Soullocke: {(entry.SoullockeSynced ? "synchronisiert" : "ausstehend")}\n\n" +
                        "Technische Daten\n" +
                        $"Fangort-ID: {entry.LocationId}\n" +
                        $"PID: {entry.Pid}\n" +
                        $"Erstmals erkannt: {entry.FirstSeenAt.LocalDateTime:g}\n" +
                        $"Zuletzt gesehen: {entry.LastSeenAt.LocalDateTime:g}"
                };
            });
    }

    private string GetLocationDisplayName(int locationId) =>
        _locationMapper.GetLocationName(locationId) ?? $"Unbekannter Fangort ({locationId})";

    private static string ValueOrUnknown(string value) => string.IsNullOrWhiteSpace(value) ? "Unbekannt" : value;

    private static string GetPokeballName(int id)
    {
        return id switch
        {
            1 => "Meisterball", 2 => "Hyperball", 3 => "Superball", 4 => "Pokéball",
            5 => "Safariball", 6 => "Netzball", 7 => "Tauchball", 8 => "Nestball",
            9 => "Wiederball", 10 => "Timerball", 11 => "Luxusball", 12 => "Premierball",
            13 => "Finsterball", 14 => "Heilball", 15 => "Flottball", 16 => "Jubelball",
            _ => id > 0 ? $"Ball #{id}" : "Unbekannt"
        };
    }

    private static void ReplaceItems<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        var next = items.ToArray();
        if (target.Count == next.Length && target.Zip(next).All(pair => ItemsEqual(pair.First, pair.Second)))
        {
            return;
        }

        target.Clear();
        foreach (var item in next)
        {
            target.Add(item);
        }
    }

    private static bool ItemsEqual<T>(T left, T right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is PokemonCardViewModel a && right is PokemonCardViewModel b)
        {
            return a.SpeciesId == b.SpeciesId &&
                   a.DisplayName == b.DisplayName &&
                   a.Species == b.Species &&
                   a.Level == b.Level &&
                   a.CurrentHp == b.CurrentHp &&
                   a.MaxHp == b.MaxHp &&
                   a.Subtitle == b.Subtitle &&
                   a.IsShiny == b.IsShiny;
        }

        return EqualityComparer<T>.Default.Equals(left, right);
    }

    public async ValueTask DisposeAsync()
    {
        _refreshTimer.Stop();
        _refreshTimer.Tick -= OnRefreshTimerTick;
        if (_runtime is not null)
        {
            _runtime.PlayerLiveStateSource.StateChanged -= OnLiveStateChanged;
            await _runtime.DisposeAsync();
            _runtime = null;
        }
    }
}
