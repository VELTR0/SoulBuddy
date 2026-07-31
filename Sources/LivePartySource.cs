using SoulBuddy.Models;

namespace SoulBuddy.Sources;

public sealed class LivePartySource : IPartySource
{
    private readonly IPartySource _snapshotSource;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<int, PartySlot> _partySlots = new();
    private readonly Dictionary<string, PartySlot> _boxSlots = new();
    private bool _initialized;

    public LivePartySource(IPartySource snapshotSource)
    {
        _snapshotSource = snapshotSource;
    }

    public async Task<IReadOnlyList<PartySlot>> ReadPartyAsync(
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);

        try
        {
            return _partySlots.Values
                .OrderBy(slot => slot.SlotId)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<PartySlot>> ReadAllPokemonAsync(
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);

        try
        {
            return _partySlots.Values
                .Concat(_boxSlots.Values)
                .OrderBy(slot => slot.Box ?? 0)
                .ThenBy(slot => slot.SlotId)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ApplyUpdateAsync(
        IReadOnlyList<PartySlot> updatedSlots,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);

        try
        {
            foreach (var slot in updatedSlots)
            {
                if (slot.SlotId is < 1 or > 6)
                {
                    continue;
                }

                _partySlots[slot.SlotId] = slot;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ApplyBoxUpdateAsync(
        IReadOnlyList<PartySlot> updatedSlots,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);

        try
        {
            foreach (var slot in updatedSlots)
            {
                if (slot.Box is < 1 or > 18 ||
                    slot.SlotId is < 1 or > 30)
                {
                    continue;
                }

                var key = $"{slot.Box}:{slot.SlotId}";

                if (slot.Pokemon is null)
                {
                    _boxSlots.Remove(key);
                }
                else
                {
                    _boxSlots[key] = slot;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureInitializedAsync(
        CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (_initialized)
            {
                return;
            }

            var snapshot =
                await _snapshotSource.ReadPartyAsync(cancellationToken);

            foreach (var slot in snapshot)
            {
                if (slot.SlotId is < 1 or > 6)
                {
                    continue;
                }

                _partySlots[slot.SlotId] = slot;
            }

            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }
}
