using UltimatePoKeSync.Contracts;

namespace UltimatePoKeSync.Parsing;

/// <summary>
/// Picks the parser that fits the current ROM.
/// </summary>
/// <remarks>
/// This is where a new generation hooks in: register one more parser and nothing else
/// changes, neither in the provider nor in the UI.
/// </remarks>
public sealed class PartyParserResolver : IPartyParserResolver
{
    private readonly IReadOnlyList<IPartyParser> _parsers;

    public PartyParserResolver(IEnumerable<IPartyParser> parsers)
        => _parsers = parsers.ToArray();

    /// <summary>The set of parsers currently implemented.</summary>
    public static PartyParserResolver CreateDefault() =>
        new([new Gen3PartyParser(), new Gen4PartyParser(), new Gen5PartyParser()]);

    public IPartyParser? Resolve(GameIdentity game)
    {
        foreach (IPartyParser parser in _parsers)
        {
            if (parser.CanParse(game))
            {
                return parser;
            }
        }

        return null;
    }
}
