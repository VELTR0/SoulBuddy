using SoulBuddy.Models;

namespace SoulBuddy.Sources;

public sealed class PlayerLiveStateSource
{
    private readonly object _sync = new();
    private PlayerLiveState _current = new();

    public event EventHandler<PlayerLiveState>? StateChanged;

    public PlayerLiveState Read()
    {
        lock (_sync)
        {
            return _current;
        }
    }

    public void Apply(PlayerLiveState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        lock (_sync)
        {
            _current = state;
        }

        StateChanged?.Invoke(this, state);
    }
}
