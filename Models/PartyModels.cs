using System.Text.Json.Serialization;

namespace SoulBuddy.Models;

public sealed class PartySlot
{
    [JsonPropertyName("slotId")]
    public int SlotId { get; init; }

    [JsonPropertyName("changeId")]
    public int ChangeId { get; init; }

    [JsonPropertyName("pokemon")]
    public PartyPokemon? Pokemon { get; init; }
}

public sealed class PartyPokemon
{
    [JsonPropertyName("species")]
    public int Species { get; init; }

    [JsonPropertyName("speciesName")]
    public string SpeciesName { get; init; } = string.Empty;

    [JsonPropertyName("nickname")]
    public string? Nickname { get; init; }

    [JsonPropertyName("pid")]
    public long Pid { get; init; }

    [JsonPropertyName("otid")]
    public int OriginalTrainerId { get; init; }

    [JsonPropertyName("otsid")]
    public int OriginalTrainerSecretId { get; init; }

    [JsonPropertyName("locationMet")]
    public int LocationMet { get; init; }

    [JsonPropertyName("levelMet")]
    public int LevelMet { get; init; }

    [JsonPropertyName("level")]
    public int Level { get; init; }

    [JsonPropertyName("isEgg")]
    public bool IsEgg { get; init; }

    [JsonPropertyName("nature")]
    public string Nature { get; init; } = string.Empty;

    [JsonPropertyName("ability")]
    public string Ability { get; init; } = string.Empty;

    [JsonPropertyName("isFemale")]
    public bool IsFemale { get; init; }

    [JsonPropertyName("isGenderless")]
    public bool IsGenderless { get; init; }

    [JsonPropertyName("isShiny")]
    public bool IsShiny { get; init; }

    [JsonPropertyName("pokeball")]
    public int Pokeball { get; init; }

    [JsonPropertyName("hp")]
    public PokemonHp Hp { get; init; } = new();
}

public sealed class PokemonHp
{
    [JsonPropertyName("current")]
    public int Current { get; init; }

    [JsonPropertyName("max")]
    public int Max { get; init; }
}