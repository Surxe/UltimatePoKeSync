using UltimatePoKeSync.Contracts;

namespace UltimatePoKeSync.GameData;

/// <summary>Resolves the battle rules for a game generation.</summary>
public interface IGenerationRulesResolver
{
    /// <summary>Returns <c>null</c> when the generation is not implemented.</summary>
    IGenerationRules? Resolve(PokemonGeneration generation);
}

/// <summary>The built-in, offline generation-rule registry.</summary>
public sealed class GenerationRulesResolver : IGenerationRulesResolver
{
    public static GenerationRulesResolver Default { get; } = new();

    public IGenerationRules? Resolve(PokemonGeneration generation) => generation switch
    {
        PokemonGeneration.Gen3 => Gen3Rules.Instance,
        PokemonGeneration.Gen4 => Gen4Rules.Instance,
        PokemonGeneration.Gen5 => Gen5Rules.Instance,
        _ => null,
    };
}
