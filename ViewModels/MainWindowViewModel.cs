using System.Collections.ObjectModel;
using Avalonia.Threading;
using SoulBuddy.Data;
using SoulBuddy.Models;
using SoulBuddy.Services;

namespace SoulBuddy.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly DispatcherTimer _refreshTimer;
    private SoulBuddyRuntime? _runtime;
    private bool _refreshInProgress;
    private string _statusText = "SoulBuddy wird gestartet …";
    private string _connectionText = "Offline";
    private string _partyCountText = "0 / 6";
    private string _pokemonCountText = "0 Pokémon";
    private string _detailsTitle = "Kein Pokémon ausgewählt";
    private string _detailsText = "Wähle ein Pokémon aus, um seine Details anzuzeigen.";

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

            ReplaceItems(Party, CreatePartyCards(party));
            ReplaceItems(StoredPokemon, CreateStoredCards(stored));

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

    private static IEnumerable<PokemonCardViewModel> CreatePartyCards(
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

                return new PokemonCardViewModel
                {
                    DisplayName = displayName,
                    Species = pokemon.SpeciesName,
                    Level = pokemon.Level,
                    CurrentHp = pokemon.Hp.Current,
                    MaxHp = pokemon.Hp.Max,
                    Subtitle = $"Fangort-ID {pokemon.LocationMet}",
                    DetailsTitle = displayName,
                    DetailsText =
                        $"Spezies: {pokemon.SpeciesName} (#{pokemon.Species})\n" +
                        $"Level: {pokemon.Level}\n" +
                        $"KP: {pokemon.Hp.Current}/{pokemon.Hp.Max}\n" +
                        $"Fanglevel: {pokemon.LevelMet}\n" +
                        $"Fangort-ID: {pokemon.LocationMet}\n\n" +
                        "Technische Daten\n" +
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
                        $"PID: {entry.Pid}\n" +
                        $"Erstmals erkannt: {entry.FirstSeenAt.LocalDateTime:g}\n" +
                        $"Zuletzt gesehen: {entry.LastSeenAt.LocalDateTime:g}"
                };
            });
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
