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
            if (heading?.Parent is not Grid sectionGrid)
                continue;

            var scroll = sectionGrid.Children.OfType<ScrollViewer>().FirstOrDefault();
            if (scroll is null || IsolatedEncounterScrollers.TryGetValue(scroll, out _))
                continue;

            if (scroll.Content is not StackPanel legacyPanel)
                continue;

            scroll.Content = new StackPanel
            {
                Spacing = legacyPanel.Spacing,
                Margin = legacyPanel.Margin,
                HorizontalAlignment = legacyPanel.HorizontalAlignment,
                VerticalAlignment = legacyPanel.VerticalAlignment
            };

            IsolatedEncounterScrollers.Add(scroll, new object());
            ForceSoulLinkEncounterRefresh(window);
        }
    }

    private static void ForceSoulLinkEncounterRefresh(Window window)
    {
        var statesField = typeof(MainWindowSoulLinkUi).GetField(
            "States",
            BindingFlags.Static | BindingFlags.NonPublic);
        if (statesField?.GetValue(null) is not IDictionary states || !states.Contains(window))
            return;

        var state = states[window];
        state?.GetType().GetMethod(
                "ForceRefresh",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?
            .Invoke(state, null);
    }
}
