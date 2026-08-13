using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SoulBuddy.Services;

internal static class EncounterCountUiGuard
{
    private static readonly HashSet<TextBlock> Wired = [];
    private static readonly HashSet<TextBlock> Applying = [];
    private static DispatcherTimer? _timer;

    [ModuleInitializer]
    internal static void Initialize()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (_, _) => Apply();
            _timer.Start();
            Apply();
        });
    }

    private static void Apply()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        foreach (var window in desktop.Windows)
        {
            var heading = window.GetVisualDescendants()
                .OfType<TextBlock>()
                .FirstOrDefault(text => LocalizationService.IsTranslationOf(text.Text, "Encounters"));
            if (heading?.Parent is not Grid header)
                continue;

            var count = header.Children.OfType<TextBlock>()
                .FirstOrDefault(text => !ReferenceEquals(text, heading));
            if (count is null)
                continue;

            if (Wired.Add(count))
                count.PropertyChanged += OnCountChanged;

            Normalize(count);
        }
    }

    private static void OnCountChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (sender is TextBlock text && args.Property == TextBlock.TextProperty)
            Normalize(text);
    }

    private static void Normalize(TextBlock text)
    {
        if (!Applying.Add(text))
            return;

        try
        {
            var current = text.Text ?? string.Empty;
            var digits = new string(current.TakeWhile(character => char.IsDigit(character)).ToArray());
            if (digits.Length > 0 && !string.Equals(current, digits, StringComparison.Ordinal))
                text.SetCurrentValue(TextBlock.TextProperty, digits);
        }
        finally
        {
            Applying.Remove(text);
        }
    }
}
