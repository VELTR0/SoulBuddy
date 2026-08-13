using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SoulBuddy.ViewModels;

namespace SoulBuddy.Services;

internal static class VisualRootCompatibility
{
    private static readonly ConditionalWeakTable<ScrollViewer, object> IsolatedEncounterScrollers = new();
    private static DispatcherTimer? _encounterIsolationTimer;

    [ModuleInitializer]
    internal static void InitializeEncounterIsolation()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _encounterIsolationTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _encounterIsolationTimer.Tick += (_, _) => IsolateEncounterPanels();
            _encounterIsolationTimer.Start();
            IsolateEncounterPanels();
        });
    }

    internal static object? GetVisualRoot(this Control control) =>
        control.Parent is null ? null : control;

    private static void IsolateEncounterPanels()
    {
        if (Application.Current?.ApplicationLifetime is not
            IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        foreach (var window in desktop.Windows)
        {
            if (window.DataContext is not MainWindowViewModel)
                continue;

            var heading = window.GetVisualDescendants()
                .OfType<TextBlock>()
                .FirstOrDefault(text =>
                    LocalizationService.IsTranslationOf(text.Text, "Encounters"));
            if (heading?.Parent is not Grid headerGrid)
                continue;

            var sectionGrid = headerGrid.GetVisualAncestors()
                .OfType<Grid>()
                .FirstOrDefault(grid => grid.Children.OfType<ScrollViewer>().Any());
            if (sectionGrid is null)
                continue;

            var scroll = sectionGrid.Children.OfType<ScrollViewer>().FirstOrDefault();
            if (scroll?.Content is not StackPanel visiblePanel)
                continue;

            if (!IsolatedEncounterScrollers.TryGetValue(scroll, out _))
            {
                var legacyPanel = visiblePanel;
                visiblePanel = new StackPanel
                {
                    Spacing = legacyPanel.Spacing,
                    Margin = legacyPanel.Margin,
                    HorizontalAlignment = legacyPanel.HorizontalAlignment,
                    VerticalAlignment = legacyPanel.VerticalAlignment
                };

                scroll.Content = visiblePanel;
                IsolatedEncounterScrollers.Add(scroll, new object());
            }

            var count = headerGrid.Children
                .OfType<TextBlock>()
                .FirstOrDefault(text => !ReferenceEquals(text, heading));

            BindSoulLinkEncounterState(window, visiblePanel, count);
        }
    }

    private static void BindSoulLinkEncounterState(
        Window window,
        StackPanel visiblePanel,
        TextBlock? count)
    {
        var statesField = typeof(MainWindowSoulLinkUi).GetField(
            "States",
            BindingFlags.Static | BindingFlags.NonPublic);
        if (statesField?.GetValue(null) is not IDictionary states || !states.Contains(window))
            return;

        var state = states[window];
        if (state is null)
            return;

        var stateType = state.GetType();
        var panelField = stateType.GetField(
            "_encounterPanel",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var countField = stateType.GetField(
            "_encounterCount",
            BindingFlags.Instance | BindingFlags.NonPublic);

        var panelChanged = panelField is not null &&
                           !ReferenceEquals(panelField.GetValue(state), visiblePanel);
        var countChanged = countField is not null &&
                           !ReferenceEquals(countField.GetValue(state), count);

        if (panelField is not null)
            panelField.SetValue(state, visiblePanel);
        if (countField is not null)
            countField.SetValue(state, count);

        if (!panelChanged && !countChanged)
            return;

        stateType.GetMethod(
                "ForceRefresh",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?
            .Invoke(state, null);
    }
}
