namespace SoulBuddy.Models;

public sealed class NetworkPlayerSnapshot
{
    public string PlayerName { get; init; } = string.Empty;
    public string Game { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public int StoredPokemonCount { get; init; }
    public NetworkPokemonSnapshot? ActivePokemon { get; init; }
    public IReadOnlyList<NetworkPokemonSnapshot> Party { get; init; } = [];
}

public sealed class NetworkPokemonSnapshot
{
    public int SpeciesId { get; init; }
    public string SpeciesName { get; init; } = string.Empty;
    public string Nickname { get; init; } = string.Empty;
    public int Level { get; init; }
    public int CurrentHp { get; init; }
    public int MaxHp { get; init; }
    public string Location { get; init; } = string.Empty;

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Nickname) ? SpeciesName : Nickname;
}
