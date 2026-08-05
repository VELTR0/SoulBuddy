using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoulBuddy.Models;

public sealed class PlayerLiveState
{
    public long Timestamp { get; init; }
    public string LocationName { get; init; } = "Aufenthaltsort wird ermittelt";
    public int? LocationId { get; init; }
    public bool InBattle { get; init; }
    public string BattleKind { get; init; } = "none";
    public string? TrainerName { get; init; }

    [JsonPropertyName("opponentPokemon")]
    public LivePokemonState? Opponent { get; init; }

    public LivePokemonState? ActivePokemon { get; init; }
    public IReadOnlyDictionary<string, JsonElement> Diagnostics { get; init; } =
        new Dictionary<string, JsonElement>();
}

public sealed class LivePokemonState
{
    public int SpeciesId { get; init; }
    public string SpeciesName { get; init; } = string.Empty;
    public string Nickname { get; init; } = string.Empty;
    public int Level { get; init; }
    public int CurrentHp { get; init; }
    public int MaxHp { get; init; }
    public long Pid { get; init; }
    public int OriginalTrainerId { get; init; }
    public int OriginalTrainerSecretId { get; init; }
    public int LocationMet { get; init; }
    public bool IsShiny { get; init; }
}
