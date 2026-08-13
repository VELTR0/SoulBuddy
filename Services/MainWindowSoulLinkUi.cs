using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SoulBuddy.Data;
using SoulBuddy.Models;
using SoulBuddy.ViewModels;

namespace SoulBuddy.Services;

internal static class MainWindowSoulLinkUi
{
    private static readonly Dictionary<Window, WindowState> States = [];
    private static readonly PokemonVisualService Visuals = new();
    private static DispatcherTimer? _timer;

    [ModuleInitializer]
    internal static void Initialize()
    {
        Dispatcher.UIThread.Post(() =>
        {
            LocalizationService.LanguageChanged += (_, _) =>
                Dispatcher.UIThread.Post(() =>
                {
                    foreach (var state in States.Values)
                        state.ForceRefresh();
                });

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += async (_, _) => await RefreshOpenWindowsAsync();
            _timer.Start();
            _ = RefreshOpenWindowsAsync();
        });
    }

    private static async Task RefreshOpenWindowsAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        foreach (var window in desktop.Windows.ToArray())
        {
            if (window.DataContext is not MainWindowViewModel viewModel)
                continue;

            if (!States.TryGetValue(window, out var state))
            {
                state = new WindowState(window, viewModel);
                States[window] = state;
                window.Closed += (_, _) => States.Remove(window);
            }

            await state.RefreshAsync();
        }
    }

    private sealed class WindowState
    {
        private readonly Window _window;
        private readonly MainWindowViewModel _viewModel;
        private readonly List<TextBlock> _partnerLabels = [];
        private TextBlock? _sessionPartnerLine;
        private StackPanel? _encounterPanel;
        private TextBlock? _encounterCount;
        private bool _sessionRestructured;
        private bool _refreshing;
        private string _lastSignature = string.Empty;

        public WindowState(Window window, MainWindowViewModel viewModel)
        {
            _window = window;
            _viewModel = viewModel;
        }

        public void ForceRefresh() => _lastSignature = string.Empty;

        public async Task RefreshAsync()
        {
            if (_refreshing)
                return;

            _refreshing = true;
            try
            {
                var runtime = GetRuntime(_viewModel);
                var partnerName = runtime?.SyncService.PartnerPlayerName;
                if (string.IsNullOrWhiteSpace(partnerName))
                    partnerName = _viewModel.SoullockePartnerName;
                partnerName = string.IsNullOrWhiteSpace(partnerName) ? Local("Partner") : partnerName.Trim();

                RestructureSession(partnerName);
                UpdatePartnerLabels(partnerName);
                LocateEncounterPanel();

                if (runtime is null || _encounterPanel is null)
                    return;

                var local = await runtime.KnownPokemonStore.GetAllAsync(CancellationToken.None);
                var partner = GetPartnerLinks(runtime.SyncService);
                var rows = BuildRows(local, partner);
                var signature = BuildSignature(rows, partnerName, LocalizationService.CurrentLanguage);

                if (signature == _lastSignature)
                    return;

                _lastSignature = signature;
                RenderEncounters(rows);
            }
            catch
            {
                // UI enrichment must never interfere with the main SoulBuddy window.
            }
            finally
            {
                _refreshing = false;
            }
        }

        private void RestructureSession(string partnerName)
        {
            if (!_sessionRestructured)
            {
                var playerHeading = _window.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .FirstOrDefault(text =>
                        LocalizationService.IsTranslationOf(text.Text, "MITTSPIELER"));

                if (playerHeading is not null)
                {
                    var playerCard = playerHeading.GetVisualAncestors().OfType<Border>().FirstOrDefault();
                    if (playerCard?.Parent is Grid cards)
                    {
                        cards.Children.Remove(playerCard);
                        var sessionCard = cards.Children.OfType<Border>().FirstOrDefault();
                        if (sessionCard is not null)
                        {
                            Grid.SetColumn(sessionCard, 0);
                            Grid.SetColumnSpan(sessionCard, Math.Max(1, cards.ColumnDefinitions.Count));
                            if (sessionCard.Child is StackPanel stack)
                            {
                                _sessionPartnerLine = Text(string.Empty, 10, FontWeight.Normal, "#CBD5E1");
                                stack.Children.Add(_sessionPartnerLine);
                            }
                        }
                    }
                }

                _sessionRestructured = true;
            }

            if (_sessionPartnerLine is not null)
                _sessionPartnerLine.Text = $"{Local("Mitspieler")}: {partnerName}";
        }

        private void UpdatePartnerLabels(string partnerName)
        {
            foreach (var label in _partnerLabels.ToArray())
            {
                if (label.GetVisualRoot() is null)
                    _partnerLabels.Remove(label);
                else
                    label.Text = partnerName;
            }

            foreach (var text in _window.GetVisualDescendants().OfType<TextBlock>())
            {
                if (_partnerLabels.Contains(text) || text == _sessionPartnerLine)
                    continue;

                if (!IsGenericPartnerLabel(text.Text))
                    continue;

                if (!IsInsideLiveArea(text))
                    continue;

                text.Text = partnerName;
                _partnerLabels.Add(text);
            }
        }

        private static bool IsGenericPartnerLabel(string? value) =>
            value is not null &&
            (LocalizationService.IsTranslationOf(value, "Partner") ||
             string.Equals(value, "Partner", StringComparison.Ordinal));

        private static bool IsInsideLiveArea(TextBlock text) =>
            text.GetVisualAncestors().Any(ancestor => ancestor is TabControl);

        private void LocateEncounterPanel()
        {
            if (_encounterPanel?.GetVisualRoot() is not null &&
                _encounterCount?.GetVisualRoot() is not null)
                return;

            var heading = _window.GetVisualDescendants()
                .OfType<TextBlock>()
                .FirstOrDefault(text => LocalizationService.IsTranslationOf(text.Text, "Encounters"));
            if (heading is null)
                return;

            var sectionGrid = heading.Parent as Grid;
            if (sectionGrid is null)
                return;

            _encounterCount = sectionGrid.Children
                .OfType<TextBlock>()
                .FirstOrDefault(text => !ReferenceEquals(text, heading));

            _encounterPanel = sectionGrid.Children
                .OfType<ScrollViewer>()
                .Select(scroll => scroll.Content)
                .OfType<StackPanel>()
                .FirstOrDefault();
        }

        private static IReadOnlyList<SoulLinkPartnerInfo> GetPartnerLinks(SyncService syncService)
        {
            var field = typeof(SyncService).GetField(
                "_partnerLinksByLocation",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(syncService) is not IEnumerable values)
                return [];

            var result = new List<SoulLinkPartnerInfo>();
            foreach (var item in values)
            {
                var valueProperty = item?.GetType().GetProperty("Value");
                if (valueProperty?.GetValue(item) is SoulLinkPartnerInfo info && info.SpeciesId > 0)
                    result.Add(info);
            }
            return result;
        }

        private static SoulBuddyRuntime? GetRuntime(MainWindowViewModel viewModel) =>
            typeof(MainWindowViewModel)
                .GetField("_runtime", BindingFlags.Instance | BindingFlags.NonPublic)?
                .GetValue(viewModel) as SoulBuddyRuntime;

        private static List<EncounterRow> BuildRows(
            IReadOnlyList<KnownPokemonEntry> localEntries,
            IReadOnlyList<SoulLinkPartnerInfo> partnerEntries)
        {
            var localByLocation = localEntries
                .Where(entry => entry.SpeciesId > 0 && !entry.IsEgg && !string.IsNullOrWhiteSpace(entry.Location))
                .GroupBy(entry => NormalizeLocation(entry.Location), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(entry => entry.LastSeenAt).First(),
                    StringComparer.OrdinalIgnoreCase);

            var partnerByLocation = partnerEntries
                .Where(entry => entry.SpeciesId > 0 && !string.IsNullOrWhiteSpace(entry.Location))
                .GroupBy(entry => NormalizeLocation(entry.Location), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.First(),
                    StringComparer.OrdinalIgnoreCase);

            var keys = localByLocation.Keys
                .Concat(partnerByLocation.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var rows = new List<EncounterRow>();
            foreach (var key in keys)
            {
                localByLocation.TryGetValue(key, out var own);
                partnerByLocation.TryGetValue(key, out var partner);
                var location = own?.Location ?? partner?.Location ?? key;
                rows.Add(new EncounterRow(location, own, partner, CombinedStatus(own, partner)));
            }

            return rows
                .OrderByDescending(row => row.Own?.FirstSeenAt ?? DateTimeOffset.MinValue)
                .ThenBy(row => row.Location, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private void RenderEncounters(IReadOnlyList<EncounterRow> rows)
        {
            if (_encounterPanel is null)
                return;

            _encounterPanel.Children.Clear();
            if (_encounterCount is not null)
                _encounterCount.Text = rows.Count.ToString();

            if (rows.Count == 0)
            {
                _encounterPanel.Children.Add(new Border
                {
                    Background = Brush("#0F1829"),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10),
                    Child = Text(Local("Noch keine Encounters"), 11, FontWeight.Normal, "#94A3B8")
                });
                return;
            }

            foreach (var row in rows)
                _encounterPanel.Children.Add(BuildEncounterCard(row));
        }

        private Control BuildEncounterCard(EncounterRow row)
        {
            var palette = row.Status switch
            {
                EncounterState.Out => new Palette("#301717", "#EF4444", "#FCA5A5"),
                EncounterState.Boxed => new Palette("#2A2111", "#D97706", "#FDE68A"),
                _ => new Palette("#10251F", "#22C55E", "#86EFAC")
            };

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("1*,1.15*,1*"),
                ColumnSpacing = 8,
                MinWidth = 0
            };

            var own = BuildPokemonSide(row.Own, null, false);
            own.HorizontalAlignment = HorizontalAlignment.Left;
            grid.Children.Add(own);

            var center = new StackPanel
            {
                Spacing = 5,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var location = Text(row.Location, 10, FontWeight.SemiBold, "#F8FAFC");
            location.TextAlignment = TextAlignment.Center;
            location.TextWrapping = TextWrapping.Wrap;
            center.Children.Add(location);
            var status = Text(StatusText(row.Status), 10, FontWeight.Bold, palette.Accent);
            status.TextAlignment = TextAlignment.Center;
            center.Children.Add(status);
            Grid.SetColumn(center, 1);
            grid.Children.Add(center);

            var partner = BuildPokemonSide(null, row.Partner, true);
            partner.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(partner, 2);
            grid.Children.Add(partner);

            return new Border
            {
                Background = Brush(palette.Background),
                BorderBrush = Brush(palette.Border),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(8),
                Child = grid
            };
        }

        private Control BuildPokemonSide(
            KnownPokemonEntry? own,
            SoulLinkPartnerInfo? partner,
            bool alignRight)
        {
            var speciesId = own?.SpeciesId ?? partner?.SpeciesId ?? 0;
            var displayName = own is not null
                ? DisplayName(own)
                : partner is not null
                    ? PartnerDisplayName(partner)
                    : "—";
            var level = own is not null && own.CurrentLevel > 0
                ? $"Lv. {own.CurrentLevel}"
                : "Lv. —";

            var image = new Image
            {
                Width = 52,
                Height = 52,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = alignRight ? HorizontalAlignment.Right : HorizontalAlignment.Left
            };

            var name = Text(displayName, 10, FontWeight.SemiBold, "#F8FAFC");
            name.TextAlignment = alignRight ? TextAlignment.Right : TextAlignment.Left;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.MaxWidth = 100;

            var levelText = Text(level, 9, FontWeight.Medium, "#CBD5E1");
            levelText.TextAlignment = alignRight ? TextAlignment.Right : TextAlignment.Left;

            var stack = new StackPanel
            {
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = alignRight ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Children = { name, image, levelText }
            };

            if (speciesId > 0)
                _ = LoadSpriteAsync(speciesId, own?.IsEgg == false && false, image);

            return stack;
        }

        private static string DisplayName(KnownPokemonEntry entry)
        {
            if (!string.IsNullOrWhiteSpace(entry.Nickname))
                return entry.Nickname!;
            if (!string.IsNullOrWhiteSpace(entry.Species) && !entry.Species.StartsWith("Pokémon #", StringComparison.Ordinal))
                return LocalizationService.PokemonName(entry.SpeciesId, entry.Species);
            return LocalizationService.PokemonName(entry.SpeciesId, $"Pokémon #{entry.SpeciesId}");
        }

        private static string PartnerDisplayName(SoulLinkPartnerInfo partner) =>
            string.IsNullOrWhiteSpace(partner.Nickname)
                ? LocalizationService.PokemonName(partner.SpeciesId, $"Pokémon #{partner.SpeciesId}")
                : partner.Nickname!;

        private static async Task LoadSpriteAsync(int speciesId, bool shiny, Image image)
        {
            var visual = await Visuals.GetAsync(speciesId, shiny);
            if (visual.Sprite is not null)
                image.Source = visual.Sprite;
        }

        private static string BuildSignature(
            IReadOnlyList<EncounterRow> rows,
            string partnerName,
            AppLanguage language) =>
            string.Join("|", new[] { partnerName, language.ToString() }.Concat(rows.Select(row =>
                $"{NormalizeLocation(row.Location)}:{row.Own?.SpeciesId}:{row.Own?.CurrentLevel}:{row.Own?.EncounterStatus}:" +
                $"{row.Partner?.SpeciesId}:{row.Partner?.Nickname}:{row.Partner?.Status}:{row.Status}")));
    }

    private static EncounterState CombinedStatus(KnownPokemonEntry? own, SoulLinkPartnerInfo? partner)
    {
        var ownState = own is null ? (EncounterState?)null : StatusOf(own.EncounterStatus);
        var partnerState = partner is null ? (EncounterState?)null : StatusOf(partner.Status);
        if (ownState == EncounterState.Out || partnerState == EncounterState.Out)
            return EncounterState.Out;
        if (ownState == EncounterState.Boxed || partnerState == EncounterState.Boxed)
            return EncounterState.Boxed;
        return EncounterState.Alive;
    }

    private static EncounterState StatusOf(string? status) =>
        (status ?? "alive").Trim().ToLowerInvariant() switch
        {
            "boxed" or "box" => EncounterState.Boxed,
            "fainted" or "brofailed" or "bro-failed" or "notcaught" or "not-caught" => EncounterState.Out,
            _ => EncounterState.Alive
        };

    private static string StatusText(EncounterState state) => state switch
    {
        EncounterState.Boxed => Local("Boxed"),
        EncounterState.Out => Local("Out"),
        _ => Local("Alive")
    };

    private static string NormalizeLocation(string value)
    {
        var normalized = new string(value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        return normalized switch
        {
            "finsterhöhle" or "dunkelhöhle" or "darkcave" or "placeholder1" => "darkcave",
            "knofensaturm" or "sprouttower" or "placeholder2" => "sprouttower",
            "newborkia" or "newbarktown" or "starter" => "starter",
            _ => normalized
        };
    }

    private static string Local(string key)
    {
        var language = LocalizationService.CurrentLanguage;
        return key switch
        {
            "Mitspieler" => language switch
            {
                AppLanguage.English => "Partner",
                AppLanguage.French => "Partenaire",
                AppLanguage.Spanish => "Compañero",
                AppLanguage.Italian => "Compagno",
                AppLanguage.Japanese => "パートナー",
                _ => "Mitspieler"
            },
            "Partner" => language switch
            {
                AppLanguage.French => "Partenaire",
                AppLanguage.Spanish => "Compañero",
                AppLanguage.Italian => "Compagno",
                AppLanguage.Japanese => "パートナー",
                _ => "Partner"
            },
            "Alive" => language switch
            {
                AppLanguage.German => "Lebendig",
                AppLanguage.French => "En vie",
                AppLanguage.Spanish => "Vivo",
                AppLanguage.Italian => "Vivo",
                AppLanguage.Japanese => "生存",
                _ => "Alive"
            },
            "Boxed" => language switch
            {
                AppLanguage.German => "Box",
                AppLanguage.French => "Au PC",
                AppLanguage.Spanish => "En caja",
                AppLanguage.Italian => "Nel box",
                AppLanguage.Japanese => "ボックス",
                _ => "Boxed"
            },
            "Out" => language switch
            {
                AppLanguage.German => "Ausgeschieden",
                AppLanguage.French => "Éliminé",
                AppLanguage.Spanish => "Fuera",
                AppLanguage.Italian => "Fuori",
                AppLanguage.Japanese => "脱落",
                _ => "Out"
            },
            "Noch keine Encounters" => language switch
            {
                AppLanguage.English => "No encounters yet.",
                AppLanguage.French => "Aucune rencontre pour le moment.",
                AppLanguage.Spanish => "Aún no hay encuentros.",
                AppLanguage.Italian => "Nessun incontro ancora.",
                AppLanguage.Japanese => "まだエンカウントはありません。",
                _ => "Noch keine Encounters."
            },
            _ => key
        };
    }

    private static TextBlock Text(string value, double size, FontWeight weight, string color) => new()
    {
        Text = value,
        FontSize = size,
        FontWeight = weight,
        Foreground = Brush(color)
    };

    private static SolidColorBrush Brush(string color) => new(Color.Parse(color));

    private sealed record EncounterRow(
        string Location,
        KnownPokemonEntry? Own,
        SoulLinkPartnerInfo? Partner,
        EncounterState Status);

    private sealed record Palette(string Background, string Border, string Accent);

    private enum EncounterState
    {
        Alive,
        Boxed,
        Out
    }
}
