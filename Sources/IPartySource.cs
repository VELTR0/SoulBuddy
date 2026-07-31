using SoulBuddy.Models;

namespace SoulBuddy.Sources;

public interface IPartySource
{
    Task<IReadOnlyList<PartySlot>> ReadPartyAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PartySlot>> ReadAllPokemonAsync(
        CancellationToken cancellationToken);
}
