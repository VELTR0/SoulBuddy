using Avalonia.Controls;

namespace SoulBuddy.Services;

internal static class VisualRootCompatibility
{
    internal static object? GetVisualRoot(this Control control) =>
        control.Parent is null ? null : control;
}
