using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
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
    private static readonly Dictionary<MainWindow, string> LastDiagnosticSignatures = [];
    private static readonly HashSet<MainWindow> VisualTreeLogged = [];
    private static readonly PokemonVisualService VisualService = new();
    private static DispatcherTimer? _timer;

    [ModuleInitializer]
    internal static void Initialize()
    {
        SoulLinkDiagnosticLog.Write(
            "BOOT",
            $"SoulLink diagnostics started. " +
            $"pid={Environment.ProcessId} base='{AppContext.BaseDirectory}'");

        Dispatcher.UIThread.Post(() =>
        {
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(350)
            };
            _timer.Tick += (_, _) => UpdateOpenWindows();
            _timer.Start();
            UpdateOpenWindows();
        });
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
        var snapshot = network?.LatestRemoteSnapshot;
        var remoteParty = connected ? snapshot?.Party ?? [] : [];

        foreach (var window in desktop.Windows.OfType<MainWindow>())
        {
            UpdateTeamCards(
                window,
                remoteParty,
                connected,
                snapshot?.PlayerName ?? network?.RemotePlayerName ?? string.Empty);
        }

        RemoveDetachedViews(desktop.Windows.OfType<MainWindow>());
    }

    private static void UpdateTeamCards(
        MainWindow window,
        IReadOnlyList<NetworkPokemonSnapshot> remoteParty,
        bool connected,
        string remotePlayerName)
    {
        if (PartyPanelField?.GetValue(window) is not Grid partyPanel)
        {
            SoulLinkDiagnosticLog.Write(
                "ERROR",
                $"Window '{window.Title}': _partyPanel field was not found or is not a Grid.");
            return;
        }

        if (ViewModelField?.GetValue(window) is not MainWindowViewModel viewModel)
        {
            SoulLinkDiagnosticLog.Write(
                "ERROR",
                $"Window '{window.Title}': _viewModel field was not found.");
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

        var diagnosticSignature = BuildDiagnosticSignature(
            connected,
            remotePlayerName,
            localParty,
            remoteParty,
            allButtons.Length,
            cards.Length);
        var shouldLog = !LastDiagnosticSignatures.TryGetValue(window, out var previous) ||
                        !string.Equals(previous, diagnosticSignature, StringComparison.Ordinal);

        if (shouldLog)
        {
            LastDiagnosticSignatures[window] = diagnosticSignature;
            LogSnapshotAndUiState(
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
                if (shouldLog)
                {
                    SoulLinkDiagnosticLog.Write(
                        "UI",
                        $"Local #{index}: PartnerView creation FAILED.");
                }
                continue;
            }

            var localLocation = NormalizeLocation(local.Subtitle);
            Candidate? match = null;
            var matchReason = string.Empty;

            if (shouldLog)
            {
                LogLocalAndComparisons(index, local, localLocation, available);
            }

            if (localLocation.Length > 0)
            {
                match = available.FirstOrDefault(candidate =>
                    NormalizeLocation(candidate.Pokemon.Location) == localLocation);
                if (match is not null)
                {
                    matchReason = "Location";
                }
            }

            if (match is null)
            {
                match = available.FirstOrDefault(candidate =>
                    IsSamePokemon(local, candidate.Pokemon));
                if (match is not null)
                {
                    matchReason = "Pokemon identity";
                }
            }

            if (match is null && !anyLocationOverlap)
            {
                match = available.FirstOrDefault(candidate => candidate.Index == index);
                if (match is not null)
                {
                    matchReason = "Team slot fallback";
                }
            }

            if (match is null)
            {
                ShowUnlinked(view);
                if (shouldLog)
                {
                    SoulLinkDiagnosticLog.Write(
                        "RESULT",
                        $"Local #{index} '{local.NameLine}': NO MATCH FOUND. " +
                        $"location='{local.Subtitle}', normalized='{localLocation}'.");
                    SoulLinkDiagnosticLog.Write(
                        "UI",
                        $"Local #{index}: ShowUnlinked completed. " +
                        $"viewAttached={ReferenceEquals(view.Border.Parent, card.Content) || view.Border.Parent is not null}.");
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
                    $"reason={matchReason}, localLocation='{local.Subtitle}', " +
                    $"remoteLocation='{match.Pokemon.Location}'.");
                SoulLinkDiagnosticLog.Write(
                    "UI",
                    $"Local #{index}: ShowLinked completed. " +
                    $"partnerName='{view.Name.Text}', speciesId={view.SpeciesId}, " +
                    $"borderVisible={view.Border.IsVisible}, " +
                    $"borderBounds={view.Border.Bounds.Width:0.#}x{view.Border.Bounds.Height:0.#}.");
            }
        }

        if (shouldLog && count == 0)
        {
            SoulLinkDiagnosticLog.Write(
                "ERROR",
                $"Window '{window.Title}': no team cards were processed. " +
                $"localParty={localParty.Length}, matchingButtons={cards.Length}.");
        }
    }

    private static void LogSnapshotAndUiState(
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

        SoulLinkDiagnosticLog.Write(
            "SNAPSHOT",
            $"Remote snapshot received: player='{remotePlayerName}', " +
            $"partyCount={remoteParty.Count}.");
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

        for (var index = 0; index < allButtons.Count; index++)
        {
            var button = allButtons[index];
            var texts = button.GetVisualDescendants()
                .OfType<TextBlock>()
                .Select(text => text.Text ?? string.Empty)
                .Where(text => text.Length > 0)
                .ToArray();
            SoulLinkDiagnosticLog.Write(
                "BUTTON",
                $"all[{index}] row={Grid.GetRow(button)} col={Grid.GetColumn(button)} " +
                $"contentType='{button.Content?.GetType().Name ?? "null"}' " +
                $"isPartyCard={IsPartyPokemonCard(button)} " +
                $"texts=[{string.Join(" | ", texts)}].");
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

    private static void LogLocalAndComparisons(
        int localIndex,
        PokemonCardViewModel local,
        string localLocation,
        IReadOnlyList<Candidate> available)
    {
        SoulLinkDiagnosticLog.Write(
            "MATCH",
            $"Local #{localIndex}: name='{local.NameLine}', species='{local.Species}', " +
            $"location='{local.Subtitle}', normalized='{localLocation}', " +
            $"level={local.Level}.");

        foreach (var candidate in available)
        {
            var remote = candidate.Pokemon;
            var remoteLocation = NormalizeLocation(remote.Location);
            var locationMatch = localLocation.Length > 0 &&
                                localLocation == remoteLocation;
            var speciesMatch = IsSamePokemonIgnoringLevel(local, remote);
            var levelMatch = local.Level <= 0 || local.Level == remote.Level;

            SoulLinkDiagnosticLog.Write(
                "COMPARE",
                $"local#{localIndex} vs remote#{candidate.Index}: " +
                $"remote='{DisplayName(remote)}', " +
                $"localLocation='{local.Subtitle}', remoteLocation='{remote.Location}', " +
                $"normalizedLocal='{localLocation}', normalizedRemote='{remoteLocation}', " +
                $"LocationMatch={locationMatch}, SpeciesMatch={speciesMatch}, " +
                $"LevelMatch={levelMatch}, IdentityMatch={IsSamePokemon(local, remote)}.");
        }
    }

    private static string BuildDiagnosticSignature(
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

    private static string BuildVisualTree(Control root)
    {
        var builder = new StringBuilder();
        AppendVisual(root, builder, 0);
        return builder.ToString();
    }

    private static void AppendVisual(Control control, StringBuilder builder, int depth)
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
            builder.Append(" text='").Append(text.Text.Replace("\n", "\\n")).Append(''');
        }
        else if (control is Button button)
        {
            builder.Append(" contentType='")
                .Append(button.Content?.GetType().Name ?? "null")
                .Append(''');
        }

        builder.AppendLine();
        foreach (var child in control.GetVisualChildren().OfType<Control>())
        {
            AppendVisual(child, builder, depth + 1);
        }
    }

    private static bool IsPartyPokemonCard(Button card)
    {
        var texts = card.GetVisualDescendants().OfType<TextBlock>();
        return texts.Any(text =>
            text.Text?.StartsWith("📍", StringComparison.Ordinal) == true);
    }

    private static int GetPartyCardOrder(Button card)
    {
        var row = Grid.GetRow(card);
        var column = Grid.GetColumn(card);
        return row * 2 + column;
    }

    private static PartnerView? GetOrCreateView(
        Button card,
        int localIndex,
        bool log)
    {
        if (Views.TryGetValue(card, out var existing))
        {
            if (log)
            {
                SoulLinkDiagnosticLog.Write(
                    "UI",
                    $"Local #{localIndex}: existing PartnerView found.");
            }
            return existing;
        }

        if (card.Content is not Control originalContent)
        {
            if (log)
            {
                SoulLinkDiagnosticLog.Write(
                    "UI",
                    $"Local #{localIndex}: card.Content is not a Control. " +
                    $"actualType='{card.Content?.GetType().FullName ?? "null"}'.");
            }
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
                $"newContentType='{card.Content.GetType().Name}', " +
                $"layoutChildren={layout.Children.Count}.");
        }

        return view;
    }

    private static void ShowUnlinked(PartnerView view)
    {
        if (view.Signature == "unlinked")
        {
            return;
        }

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

    private static void RemoveDetachedViews(IEnumerable<MainWindow> windows)
    {
        var windowArray = windows.ToArray();
        var liveCards = windowArray
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

        foreach (var window in LastDiagnosticSignatures.Keys
                     .Where(window => !windowArray.Contains(window))
                     .ToArray())
        {
            LastDiagnosticSignatures.Remove(window);
            VisualTreeLogged.Remove(window);
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

internal static class SoulLinkDiagnosticLog
{
    private static readonly object Sync = new();
    private static readonly string LogPath = CreateLogPath();

    public static void Write(string category, string message)
    {
        var line =
            $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] " +
            $"[SoulBuddy SoulLink] [{category}] {message}";

        lock (Sync)
        {
            try
            {
                Console.WriteLine(line);
                File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Diagnostics must never interrupt SoulBuddy.
            }
        }
    }

    private static string CreateLogPath()
    {
        var runtimeDirectory = Path.Combine(AppContext.BaseDirectory, "runtime");
        Directory.CreateDirectory(runtimeDirectory);
        return Path.Combine(
            runtimeDirectory,
            $"soullink-debug-{Environment.ProcessId}.log");
    }
}
