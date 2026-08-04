using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SoulBuddy.Models;
using SoulBuddy.ViewModels;
using SoulBuddy.Views;

namespace SoulBuddy.Services;

internal static class DirectSoulLinkUiUpdater
{
    private static readonly FieldInfo? PartyPanelField = typeof(MainWindow).GetField(
        "_partyPanel",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? ViewModelField = typeof(MainWindow).GetField(
        "_viewModel",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly Dictionary<Button, PartnerView> Views = [];
    private static readonly Dictionary<MainWindow, string> LastSignatures = [];
    private static readonly HashSet<MainWindow> VisualTreeLogged = [];
    private static readonly PokemonVisualService VisualService = new();
    private static DispatcherTimer? _timer;

    [ModuleInitializer]
    internal static void Initialize()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _timer.Tick += (_, _) => UpdateOpenWindowsSafely();
            _timer.Start();
            UpdateOpenWindowsSafely();
        });
    }

    private static void UpdateOpenWindowsSafely()
    {
        try
        {
            UpdateOpenWindows();
        }
        catch (Exception ex)
        {
            SoulLinkDiagnosticLog.Write("ERROR", ex.ToString());
        }
    }

    private static void UpdateOpenWindows()
    {
        if (Application.Current?.ApplicationLifetime is not
            IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        var network = SoulBuddyNetworkService.Current;
        var connected = network?.State == SoulBuddyNetworkState.Connected;
        var remotePlayerName = network?.RemotePlayerName ?? string.Empty;
        var remoteParty = connected
            ? network?.LatestRemoteSnapshot?.Party ?? []
            : [];

        var windows = desktop.Windows.OfType<MainWindow>().ToArray();
        foreach (var window in windows)
        {
            try
            {
                UpdateTeamCards(
                    window,
                    connected,
                    remotePlayerName,
                    remoteParty);
            }
            catch (Exception ex)
            {
                SoulLinkDiagnosticLog.Write(
                    "ERROR",
                    $"Window '{window.Title}': {ex}");
            }
        }

        RemoveDetachedViews(windows);
    }

    private static void UpdateTeamCards(
        MainWindow window,
        bool connected,
        string remotePlayerName,
        IReadOnlyList<NetworkPokemonSnapshot> remoteParty)
    {
        if (PartyPanelField?.GetValue(window) is not Grid partyPanel ||
            ViewModelField?.GetValue(window) is not MainWindowViewModel viewModel)
        {
            SoulLinkDiagnosticLog.Write(
                "ERROR",
                $"Window '{window.Title}': party panel or view model not found.");
            return;
        }

        var allButtons = partyPanel
            .GetVisualDescendants()
            .OfType<Button>()
            .ToArray();

        var cards = allButtons
            .Where(IsPartyPokemonCard)
            .OrderBy(GetPartyCardOrder)
            .Take(6)
            .ToArray();

        var localParty = viewModel.Party.ToArray();
        var signature = BuildSignature(
            connected,
            remotePlayerName,
            localParty,
            remoteParty,
            allButtons.Length,
            cards.Length);
        var shouldLog = !LastSignatures.TryGetValue(window, out var oldSignature) ||
                        oldSignature != signature;
        LastSignatures[window] = signature;

        if (shouldLog)
        {
            LogState(
                window,
                partyPanel,
                connected,
                remotePlayerName,
                localParty,
                remoteParty,
                allButtons,
                cards);
        }

        var count = Math.Min(cards.Length, localParty.Length);
        var available = remoteParty
            .Select((pokemon, index) => new Candidate(pokemon, index))
            .ToList();

        var anyLocationOverlap = localParty.Any(local =>
            available.Any(remote =>
                NormalizeLocation(local.Subtitle) ==
                NormalizeLocation(remote.Pokemon.Location)));

        if (shouldLog)
        {
            SoulLinkDiagnosticLog.Write(
                "MATCH",
                $"Window '{window.Title}': anyLocationOverlap={anyLocationOverlap}, " +
                $"cardsToProcess={count}.");
        }

        for (var index = 0; index < count; index++)
        {
            var card = cards[index];
            var local = localParty[index];
            var view = GetOrCreateView(card, index, shouldLog);
            if (view is null)
            {
                SoulLinkDiagnosticLog.Write(
                    "UI",
                    $"Local #{index}: PartnerView creation FAILED.");
                continue;
            }

            var localLocation = NormalizeLocation(local.Subtitle);
            Candidate? match = null;
            var reason = string.Empty;

            if (shouldLog)
            {
                LogComparisons(index, local, localLocation, available);
            }

            if (localLocation.Length > 0)
            {
                match = available.FirstOrDefault(candidate =>
                    NormalizeLocation(candidate.Pokemon.Location) == localLocation);
                if (match is not null)
                {
                    reason = "Location";
                }
            }

            if (match is null)
            {
                match = available.FirstOrDefault(candidate =>
                    IsSamePokemon(local, candidate.Pokemon));
                if (match is not null)
                {
                    reason = "Pokemon identity";
                }
            }

            if (match is null && !anyLocationOverlap)
            {
                match = available.FirstOrDefault(candidate =>
                    candidate.Index == index);
                if (match is not null)
                {
                    reason = "Team slot fallback";
                }
            }

            if (match is null)
            {
                ShowUnlinked(view);
                if (shouldLog)
                {
                    SoulLinkDiagnosticLog.Write(
                        "RESULT",
                        $"Local #{index} '{local.NameLine}': NO MATCH FOUND.");
                }
                continue;
            }

            available.Remove(match);
            var fainted = local.CurrentHp == 0 || match.Pokemon.CurrentHp == 0;
            ShowLinked(view, match.Pokemon, fainted);

            if (shouldLog)
            {
                SoulLinkDiagnosticLog.Write(
                    "RESULT",
                    $"MATCHED local #{index} '{local.NameLine}' -> " +
                    $"remote #{match.Index} '{DisplayName(match.Pokemon)}'. " +
                    $"reason={reason}, localLocation='{local.Subtitle}', " +
                    $"remoteLocation='{match.Pokemon.Location}'.");
                SoulLinkDiagnosticLog.Write(
                    "UI",
                    $"Local #{index}: ShowLinked completed. " +
                    $"partnerName='{view.Name.Text}', speciesId={view.SpeciesId}, " +
                    $"borderVisible={view.Border.IsVisible}.");
            }
        }
    }

    private static PartnerView? GetOrCreateView(
        Button card,
        int localIndex,
        bool log)
    {
        if (Views.TryGetValue(card, out var existing))
        {
            return existing;
        }

        if (card.Content is not Control originalContent)
        {
            return null;
        }

        var oldLink = originalContent
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(text =>
                text.Text?.StartsWith("🔗", StringComparison.Ordinal) == true);

        if (log)
        {
            SoulLinkDiagnosticLog.Write(
                "UI",
                $"Local #{localIndex}: creating PartnerView. " +
                $"originalContent='{originalContent.GetType().Name}', " +
                $"oldLinkFound={oldLink is not null}.");
        }

        if (oldLink is not null)
        {
            oldLink.IsVisible = false;
            oldLink.Height = 0;
            oldLink.Margin = new Thickness(0);
        }

        var image = new Image
        {
            Width = 38,
            Height = 38,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var label = new TextBlock
        {
            Text = "SOULLINK",
            FontSize = 7,
            FontWeight = FontWeight.Bold,
            Foreground = Brush("#FBBF24"),
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var partnerName = new TextBlock
        {
            Text = "Noch nicht\nverknüpft",
            FontSize = 8,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("#FDE68A"),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var partnerStack = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children = { label, image, partnerName }
        };
        var partnerBorder = new Border
        {
            Background = Brush("#2A2111"),
            BorderBrush = Brush("#D97706"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(3, 2),
            MinWidth = 0,
            Child = partnerStack
        };
        var layout = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("3*,1*"),
            ColumnSpacing = 5,
            MinWidth = 0
        };

        // Avalonia controls may only have one visual parent. Detach the old
        // button content before inserting it into the new two-column layout.
        card.Content = null;
        layout.Children.Add(originalContent);
        Grid.SetColumn(partnerBorder, 1);
        layout.Children.Add(partnerBorder);
        card.Content = layout;

        var view = new PartnerView(partnerBorder, label, image, partnerName);
        Views[card] = view;

        if (log)
        {
            SoulLinkDiagnosticLog.Write(
                "UI",
                $"Local #{localIndex}: PartnerView created and assigned. " +
                $"layoutChildren={layout.Children.Count}.");
        }

        return view;
    }

    private static void ShowUnlinked(PartnerView view)
    {
        view.Signature = "unlinked";
        view.SpeciesId = 0;
        view.Image.Source = null;
        view.Label.Text = "SOULLINK";
        view.Label.Foreground = Brush("#FBBF24");
        view.Name.Text = "Noch nicht\nverknüpft";
        view.Name.Foreground = Brush("#FDE68A");
        view.Border.Background = Brush("#2A2111");
        view.Border.BorderBrush = Brush("#D97706");
    }

    private static void ShowLinked(
        PartnerView view,
        NetworkPokemonSnapshot pokemon,
        bool fainted)
    {
        var signature =
            $"{pokemon.SpeciesId}:{pokemon.DisplayName}:{pokemon.CurrentHp}:{fainted}";
        if (view.Signature == signature)
        {
            return;
        }

        view.Signature = signature;
        view.Label.Text = fainted ? "KAMPFUNFÄHIG" : "VERKNÜPFT MIT";
        view.Label.Foreground = Brush(fainted ? "#FCA5A5" : "#86EFAC");
        view.Name.Text = DisplayName(pokemon);
        view.Name.Foreground = Brush("#F8FAFC");
        view.Border.Background = Brush(fainted ? "#301717" : "#10251F");
        view.Border.BorderBrush = Brush(fainted ? "#EF4444" : "#22C55E");

        if (view.SpeciesId != pokemon.SpeciesId)
        {
            view.SpeciesId = pokemon.SpeciesId;
            view.Image.Source = null;
            _ = LoadSpriteAsync(view, pokemon.SpeciesId);
        }
    }

    private static async Task LoadSpriteAsync(PartnerView view, int speciesId)
    {
        try
        {
            var visual = await VisualService.GetAsync(speciesId, false);
            var applied = view.SpeciesId == speciesId && visual.Sprite is not null;
            if (applied)
            {
                view.Image.Source = visual.Sprite;
            }

            SoulLinkDiagnosticLog.Write(
                "SPRITE",
                $"speciesId={speciesId}, spriteFound={visual.Sprite is not null}, " +
                $"applied={applied}.");
        }
        catch (Exception ex)
        {
            SoulLinkDiagnosticLog.Write(
                "SPRITE-ERROR",
                $"speciesId={speciesId}: {ex}");
        }
    }

    private static bool IsPartyPokemonCard(Button card)
    {
        return card.GetVisualDescendants()
            .OfType<TextBlock>()
            .Any(text => text.Text?.StartsWith("📍", StringComparison.Ordinal) == true);
    }

    private static int GetPartyCardOrder(Button card) =>
        Grid.GetRow(card) * 2 + Grid.GetColumn(card);

    private static bool IsSamePokemon(
        PokemonCardViewModel local,
        NetworkPokemonSnapshot remote)
    {
        return (local.Level <= 0 || remote.Level == local.Level) &&
               IsSamePokemonIgnoringLevel(local, remote);
    }

    private static bool IsSamePokemonIgnoringLevel(
        PokemonCardViewModel local,
        NetworkPokemonSnapshot remote)
    {
        var localDisplay = Normalize(local.DisplayName);
        var localSpecies = Normalize(local.Species);
        var remoteDisplay = Normalize(remote.DisplayName);
        var remoteSpecies = Normalize(remote.SpeciesName);

        return localDisplay == remoteDisplay ||
               localDisplay == remoteSpecies ||
               localSpecies == remoteSpecies ||
               localSpecies == remoteDisplay;
    }

    private static void LogComparisons(
        int localIndex,
        PokemonCardViewModel local,
        string localLocation,
        IReadOnlyList<Candidate> available)
    {
        foreach (var candidate in available)
        {
            var remote = candidate.Pokemon;
            var remoteLocation = NormalizeLocation(remote.Location);
            SoulLinkDiagnosticLog.Write(
                "COMPARE",
                $"local#{localIndex} vs remote#{candidate.Index}: " +
                $"localLocation='{local.Subtitle}', remoteLocation='{remote.Location}', " +
                $"normalizedLocal='{localLocation}', normalizedRemote='{remoteLocation}', " +
                $"LocationMatch={localLocation.Length > 0 && localLocation == remoteLocation}, " +
                $"SpeciesMatch={IsSamePokemonIgnoringLevel(local, remote)}, " +
                $"LevelMatch={local.Level <= 0 || local.Level == remote.Level}, " +
                $"IdentityMatch={IsSamePokemon(local, remote)}.");
        }
    }

    private static void LogState(
        MainWindow window,
        Grid partyPanel,
        bool connected,
        string remotePlayerName,
        IReadOnlyList<PokemonCardViewModel> localParty,
        IReadOnlyList<NetworkPokemonSnapshot> remoteParty,
        IReadOnlyList<Button> allButtons,
        IReadOnlyList<Button> cards)
    {
        SoulLinkDiagnosticLog.Write("BLOCK", new string('=', 72));
        SoulLinkDiagnosticLog.Write(
            "STATE",
            $"window='{window.Title}' connected={connected} " +
            $"remotePlayer='{remotePlayerName}' localParty={localParty.Count} " +
            $"remoteParty={remoteParty.Count} allButtons={allButtons.Count} " +
            $"partyCards={cards.Count}.");

        for (var index = 0; index < remoteParty.Count; index++)
        {
            var pokemon = remoteParty[index];
            SoulLinkDiagnosticLog.Write(
                "REMOTE",
                $"[{index}] display='{pokemon.DisplayName}' species='{pokemon.SpeciesName}' " +
                $"speciesId={pokemon.SpeciesId} location='{pokemon.Location}' " +
                $"normalizedLocation='{NormalizeLocation(pokemon.Location)}' " +
                $"level={pokemon.Level} hp={pokemon.CurrentHp}/{pokemon.MaxHp}.");
        }

        for (var index = 0; index < localParty.Count; index++)
        {
            var pokemon = localParty[index];
            SoulLinkDiagnosticLog.Write(
                "LOCAL",
                $"[{index}] display='{pokemon.DisplayName}' species='{pokemon.Species}' " +
                $"speciesId={pokemon.SpeciesId} location='{pokemon.Subtitle}' " +
                $"normalizedLocation='{NormalizeLocation(pokemon.Subtitle)}' " +
                $"level={pokemon.Level} hp={pokemon.CurrentHp}/{pokemon.MaxHp}.");
        }

        if (!VisualTreeLogged.Contains(window))
        {
            VisualTreeLogged.Add(window);
            SoulLinkDiagnosticLog.Write(
                "VISUAL-TREE",
                $"One-time party VisualTree for '{window.Title}':\n" +
                BuildVisualTree(partyPanel));
        }
    }

    private static string BuildVisualTree(Control root)
    {
        var builder = new System.Text.StringBuilder();
        AppendVisual(root, builder, 0);
        return builder.ToString();
    }

    private static void AppendVisual(
        Control control,
        System.Text.StringBuilder builder,
        int depth)
    {
        builder.Append(' ', depth * 2)
            .Append(control.GetType().Name)
            .Append(" row=")
            .Append(Grid.GetRow(control))
            .Append(" col=")
            .Append(Grid.GetColumn(control))
            .Append(" visible=")
            .Append(control.IsVisible)
            .Append(" bounds=")
            .Append(control.Bounds.Width.ToString("0.#"))
            .Append('x')
            .Append(control.Bounds.Height.ToString("0.#"));

        if (control is TextBlock text && !string.IsNullOrWhiteSpace(text.Text))
        {
            builder.Append(" text='")
                .Append(text.Text.Replace("\n", "\\n"))
                .Append('\'');
        }
        else if (control is Button button)
        {
            builder.Append(" contentType='")
                .Append(button.Content?.GetType().Name ?? "null")
                .Append('\'');
        }

        builder.AppendLine();
        foreach (var child in control.GetVisualChildren().OfType<Control>())
        {
            AppendVisual(child, builder, depth + 1);
        }
    }

    private static string BuildSignature(
        bool connected,
        string remotePlayerName,
        IReadOnlyList<PokemonCardViewModel> localParty,
        IReadOnlyList<NetworkPokemonSnapshot> remoteParty,
        int allButtonCount,
        int cardCount)
    {
        var local = string.Join("|", localParty.Select(pokemon =>
            $"{pokemon.SpeciesId}:{pokemon.Level}:{pokemon.CurrentHp}:" +
            $"{NormalizeLocation(pokemon.Subtitle)}"));
        var remote = string.Join("|", remoteParty.Select(pokemon =>
            $"{pokemon.SpeciesId}:{pokemon.Level}:{pokemon.CurrentHp}:" +
            $"{NormalizeLocation(pokemon.Location)}"));
        return $"{connected}:{remotePlayerName}:{allButtonCount}:{cardCount}:" +
               $"L={local}:R={remote}";
    }

    private static string DisplayName(NetworkPokemonSnapshot pokemon) =>
        string.IsNullOrWhiteSpace(pokemon.DisplayName)
            ? pokemon.SpeciesName
            : pokemon.DisplayName;

    private static string NormalizeLocation(string value)
    {
        var normalized = Normalize(value);
        return normalized switch
        {
            "starter" or "newborkia" or "newbarktown" => "starter",
            _ => normalized
        };
    }

    private static string Normalize(string value) => new(value
        .Trim()
        .ToLowerInvariant()
        .Where(char.IsLetterOrDigit)
        .ToArray());

    private static void RemoveDetachedViews(IEnumerable<MainWindow> windows)
    {
        var liveCards = windows
            .SelectMany(window =>
                PartyPanelField?.GetValue(window) is Grid panel
                    ? panel.GetVisualDescendants()
                        .OfType<Button>()
                        .Where(IsPartyPokemonCard)
                    : [])
            .ToHashSet();

        foreach (var card in Views.Keys.Where(card => !liveCards.Contains(card)).ToArray())
        {
            Views.Remove(card);
        }
    }

    private static SolidColorBrush Brush(string value) =>
        new(Color.Parse(value));

    private sealed record Candidate(NetworkPokemonSnapshot Pokemon, int Index);

    private sealed class PartnerView(
        Border border,
        TextBlock label,
        Image image,
        TextBlock name)
    {
        public Border Border { get; } = border;
        public TextBlock Label { get; } = label;
        public Image Image { get; } = image;
        public TextBlock Name { get; } = name;
        public int SpeciesId { get; set; }
        public string Signature { get; set; } = string.Empty;
    }
}
