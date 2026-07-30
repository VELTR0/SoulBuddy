using SoulBuddy.Models;

namespace SoulBuddy.Sources;

public sealed class LivePartySource : IPartySource
{
    private readonly IPartySource _snapshotSource;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<int, PartySlot> _slots = new();
    private bool _initialized;

    public LivePartySource(IPartySource snapshotSource)
    {
        _snapshotSource = snapshotSource;
    }

    public async Task<IReadOnlyList<PartySlot>> ReadPartyAsync(
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await ReconcileWithSnapshotAsync(cancellationToken);

        await _gate.WaitAsync(cancellationToken);

        try
        {
            return _slots.Values
                .OrderBy(slot => slot.SlotId)
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

                _slots[slot.SlotId] = slot;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ReconcileWithSnapshotAsync(
        CancellationToken cancellationToken)
    {
        var snapshot = await _snapshotSource.ReadPartyAsync(
            cancellationToken);

        var snapshotBySlot = snapshot
            .Where(slot => slot.SlotId is >= 1 and <= 6)
            .ToDictionary(slot => slot.SlotId);

        await _gate.WaitAsync(cancellationToken);

        try
        {
            for (var slotId = 1; slotId <= 6; slotId++)
            {
                if (snapshotBySlot.TryGetValue(slotId, out var slot))
                {
                    _slots[slotId] = slot;
                }
                else
                {
                    _slots.Remove(slotId);
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

                _slots[slot.SlotId] = slot;
            }

            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }
}
