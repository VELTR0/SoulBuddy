using SoulBuddy.Models;

namespace SoulBuddy.Services;

public sealed class PartyStateService
{
    private readonly Dictionary<int, PartySlot> _slots = new();

    public IReadOnlyList<PartySlot> ApplyUpdates(
        IEnumerable<PartySlot> updates)
    {
        foreach (var update in updates)
        {
            _slots[update.SlotId] = update;
        }

        return _slots
            .OrderBy(entry => entry.Key)
            .Select(entry => entry.Value)
            .ToList();
    }
}