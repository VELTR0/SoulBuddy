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
    private string _statusText = "SoulBuddy wird gestartet …";
    private string _connectionText = "Offline";
    private string _partyCountText = "0 / 6";
    private string _pokemonCountText = "0 Pokémon";
    private string _detailsTitle = "Kein Pokémon ausgewählt";
    private string _detailsText = "Wähle ein Pokémon aus, um seine Details anzuzeigen.";
    private string _liveEncounterTitle = "LIVE ENCOUNTER";
    private string _liveEncounterText = "Warte auf Live-Daten aus dem Emulator …";

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

    public async Task InitializeAsync()
    {
        try
        {
            _runtime = await SoulBuddyRuntime.CreateAsync();
            _runtime.Start();

            ConnectionText = _runtime.Config.SoullockeEnabled
                ? "Soullocke aktiviert"
                : "Lokal / Offline";

            StatusText = $"Collector aktiv · {_runtime.EventFilePath}";
            await RefreshAsync();
            _refreshTimer.Start();
        }
        catch (Exception ex)
        {
            StatusText = $"Startfehler: {ex.Message}";
        }
    }

    public void SelectPokemon(PokemonCardViewModel pokemon)
    {
        DetailsTitle = pokemon.DetailsTitle;
        DetailsText = pokemon.DetailsText;
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
            var party = await _runtime.LivePartySource.ReadPartyAsync(
                CancellationToken.None);
            var stored = await _runtime.KnownPokemonStore.GetAllAsync(
                CancellationToken.None);
            var liveState = _runtime.PlayerLiveStateSource.Read();

            ReplaceItems(Party, CreatePartyCards(party));
            ReplaceItems(StoredPokemon, CreateStoredCards(stored));
            UpdateLiveEncounter(liveState, party);

            PartyCountText = $"{Party.Count} / 6";
            PokemonCountText = $"{StoredPokemon.Count} Pokémon";
            StatusText =
                $"Collector aktiv · Letzte Aktualisierung {DateTime.Now:HH:mm:ss}";
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

    private void UpdateLiveEncounter(
        PlayerLiveState state,
        IReadOnlyList<PartySlot> party)
    {
        var ownPokemon = state.ActivePokemon;

        if (ownPokemon is null)
        {
            var fallback = party
                .Where(slot => slot.Pokemon is not null)
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

        var location = string.IsNullOrWhiteSpace(state.LocationName)
            ? state.LocationId is null
                ? "Aufenthaltsort wird ermittelt"
                : $"Unbekannter Ort ({state.LocationId})"
            : state.LocationName;

        if (!state.InBattle)
        {
            LiveEncounterTitle = "LIVE · AUSSERHALB DES KAMPFES";
            LiveEncounterText = $"📍 {location}";
            return;
        }

        var kind = state.BattleKind switch
        {
            "trainer" => string.IsNullOrWhiteSpace(state.TrainerName)
                ? "Trainerkampf"
                : $"Trainerkampf · {state.TrainerName}",
            "wild" => "Wilder Kampf",
            _ => "Kampf erkannt · Typ wird geprüft"
        };

        var lines = new List<string>
        {
            $"⚔ {kind}",
            $"📍 {location}"
        };

        if (state.Opponent is not null)
        {
            lines.Add(
                $"Gegner: {DisplayPokemonName(state.Opponent)} · " +
                $"Lv. {state.Opponent.Level} · " +
                $"{state.Opponent.CurrentHp}/{state.Opponent.MaxHp} KP");
        }
        else
        {
            lines.Add("Gegner: wird ermittelt …");
        }

        if (ownPokemon is not null)
        {
            lines.Add(
                $"Aktiv: {DisplayPokemonName(ownPokemon)} · " +
                $"Lv. {ownPokemon.Level} · " +
                $"{ownPokemon.CurrentHp}/{ownPokemon.MaxHp} KP");
        }

        LiveEncounterTitle = "LIVE ENCOUNTER";
        LiveEncounterText = string.Join(Environment.NewLine, lines);
    }

    private static string DisplayPokemonName(LivePokemonState pokemon) =>
        string.IsNullOrWhiteSpace(pokemon.Nickname)
            ? pokemon.SpeciesName
            : pokemon.Nickname;

    private IEnumerable<PokemonCardViewModel> CreatePartyCards(
        IReadOnlyList<PartySlot> party)
    {
        return party
            .Where(slot => slot.Pokemon is not null)
            .OrderBy(slot => slot.SlotId)
            .Select(slot =>
            {
                var pokemon = slot.Pokemon!;
                var displayName = string.IsNullOrWhiteSpace(pokemon.Nickname)
                    ? pokemon.SpeciesName
                    : pokemon.Nickname;
                var gender = pokemon.IsGenderless
                    ? "Geschlechtslos"
                    : pokemon.IsFemale ? "Weiblich" : "Männlich";
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

    private static IEnumerable<PokemonCardViewModel> CreateStoredCards(
        IReadOnlyList<KnownPokemonEntry> pokemon)
    {
        return pokemon
            .OrderByDescending(item => item.FirstSeenAt)
            .Select(entry =>
            {
                var displayName = string.IsNullOrWhiteSpace(entry.Nickname)
                    ? entry.Species
                    : entry.Nickname;

                return new PokemonCardViewModel
                {
                    DisplayName = displayName,
                    Species = entry.Species,
                    SpeciesId = entry.SpeciesId,
                    Level = entry.CurrentLevel,
                    CurrentHp = entry.CurrentHp,
                    MaxHp = entry.MaxHp,
                    Subtitle = entry.SoullockeSynced
                        ? "Soullocke synchronisiert"
                        : entry.Location,
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

    private string GetLocationDisplayName(int locationId)
    {
        return _locationMapper.GetLocationName(locationId)
            ?? $"Unbekannter Fangort ({locationId})";
    }

    private static string ValueOrUnknown(string value) =>
        string.IsNullOrWhiteSpace(value) ? "Unbekannt" : value;

    private static string GetPokeballName(int id)
    {
        return id switch
        {
            1 => "Meisterball",
            2 => "Hyperball",
            3 => "Superball",
            4 => "Pokéball",
            5 => "Safariball",
            6 => "Netzball",
            7 => "Tauchball",
            8 => "Nestball",
            9 => "Wiederball",
            10 => "Timerball",
            11 => "Luxusball",
            12 => "Premierball",
            13 => "Finsterball",
            14 => "Heilball",
            15 => "Flottball",
            16 => "Jubelball",
            _ => id > 0 ? $"Ball #{id}" : "Unbekannt"
        };
    }

    private static void ReplaceItems<T>(
        ObservableCollection<T> target,
        IEnumerable<T> items)
    {
        target.Clear();

        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _refreshTimer.Stop();
        _refreshTimer.Tick -= OnRefreshTimerTick;

        if (_runtime is not null)
        {
            await _runtime.DisposeAsync();
            _runtime = null;
        }
    }
}
