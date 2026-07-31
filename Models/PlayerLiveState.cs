namespace SoulBuddy.Models;

public sealed class PlayerLiveState
{
    public long Timestamp { get; init; }
    public string LocationName { get; init; } = "Aufenthaltsort wird ermittelt";
    public int? LocationId { get; init; }
    public bool InBattle { get; init; }
    public string BattleKind { get; init; } = "none";
    public string? TrainerName { get; init; }
    public LivePokemonState? Opponent { get; init; }
    public LivePokemonState? ActivePokemon { get; init; }
    public IReadOnlyDictionary<string, long> Diagnostics { get; init; } =
        new Dictionary<string, long>();
}

public sealed class LivePokemonState
{
    public int SpeciesId { get; init; }
    public string SpeciesName { get; init; } = string.Empty;
    public string Nickname { get; init; } = string.Empty;
    public int Level { get; init; }
    public int CurrentHp { get; init; }
    public int MaxHp { get; init; }
}
