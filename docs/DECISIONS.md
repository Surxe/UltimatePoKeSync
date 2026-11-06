# Decision log: UltimatePoKeSync

A record of every design choice, with the alternatives considered and the reasoning.
Update it in the same commit as the change it describes.

Format: sequential `D-nnn`. Status: `Accepted` · `Superseded by D-xxx` · `Open`.

---

## D-001. Reference emulator: mGBA, not BizHawk

**Status:** Accepted · 2026-08-10

BizHawk depends on 64-bit WinForms and has no official macOS support, let alone Apple
Silicon. Since macOS is a first-class target, it cannot be the starting emulator.

mGBA 0.10.5 ships a universal macOS build (Intel and ARM), plus Windows and Linux
(AppImage, including arm64), and has had stable Lua scripting since 0.10.0.

**Alternatives considered:** BizHawk (rejected: no macOS), DeSmuME (no Lua scripting
across all platforms), RetroArch network command interface (protocol too poor, cannot
read arbitrary ranges efficiently).

**Consequence:** the first target is GBA, therefore Gen 3. See D-004.

---

## D-002. Lua to app transport: TCP socket, not a JSON file

**Status:** Accepted · 2026-08-10

The original idea allowed "TCP socket **or** a JSON file kept up to date". The file option
is not possible: mGBA's scripting API exposes no filesystem I/O whatsoever. There is no
`io.open`; the only file operations are `loadFile`, `loadSaveFile` and savestate handling.
TCP is therefore the only route.

Source: <https://mgba.io/docs/scripting.html>

**Consequence:** see D-003 for the direction of the connection.

---

## D-003. The Lua script is the server, the C# app is the client

**Status:** Accepted · 2026-08-10

mGBA's socket API exposes `socket.bind()` / `listen()` / `accept()` / `poll()` /
`hasdata()`, all non-blocking, plus `"received"` and `"error"` events. `socket.connect()`,
by contrast, is documented as blocking: calling it from the frame callback would stall
emulation on every failed reconnect attempt.

So Lua listens on `127.0.0.1:8888` (configurable), and the C# app connects as a client
with backoff. Retry logic belongs in the process that can afford it.

**Consequence:** multiple mGBA instances need different ports. The port is a parameter of
both the script and the app configuration.

---

## D-004. Starting generation: Gen 3, reference game Pokémon Emerald

**Status:** Accepted · 2026-08-10 · amended 2026-08-10, see D-017

A direct consequence of D-001 (mGBA means GBA). Among the Gen 3 games, Emerald has the
most public reference material (Ironmon Tracker, pokebot-bizhawk, Archipelago), which
makes diagnosing a bad read faster.

**Verified RAM addresses** (cross-checked between Data Crystal and `GameSettings.lua`
from 40Cakes/pokebot-bizhawk, a tool in active use):

| Game (USA)          | Party data   | Party count  | Domain |
| ------------------- | ------------ | ------------ | ------ |
| Emerald             | `0x020244EC` | `0x020244E9` | EWRAM  |
| FireRed / LeafGreen | `0x02024284` | `0x02024029` | EWRAM  |
| Ruby / Sapphire     | `0x03004360` | `0x03004350` | IWRAM  |

Layout: 6 contiguous 100-byte slots (80 stored + 20 battle stats).

Non-USA revisions may have different addresses. See D-005 and D-017.

---

## D-005. No hardcoded addresses: identify the game at runtime

**Status:** Accepted · 2026-08-10

Addresses vary per game and per region. Hardcoding the USA Emerald ones would mean
silently reading garbage from any other ROM.

The Lua script reads the game code from the cartridge header at `0x080000AC` (`BPEE`
Emerald USA, `BPRE` FireRed, `BPGE` LeafGreen, `AXVE` Ruby, `AXPE` Sapphire) and selects
the matching address table. If the game code is unknown, the script refuses to read and
says so, instead of guessing.

The game code travels in every message to the app, so the C# side always knows which game
it is interpreting.

---

## D-006. The provider layer carries raw bytes, not Pokémon

**Status:** Accepted · 2026-08-10

This is the choice that makes the multi-emulator abstraction real rather than nominal.

The Lua script parses nothing: it ships the raw party bytes, the count and the game
identity. All decoding (decrypt, unshuffle, checksum, ID mapping) happens in C#, once,
shared by every provider.

Adding BizHawk or DeSmuME tomorrow costs about 150 lines of Lua and zero lines of domain
logic. If each script parsed its own Pokémon, every new emulator would duplicate the same
logic and eventually diverge from it.

The contract lives in `UltimatePoKeSync.Contracts` and is the only project shared between
providers and parsing.

---

## D-007. Parsing via PKHeX.Core; the app is licensed GPLv3

**Status:** Accepted · 2026-08-10

PKHeX.Core (NuGet, latest 26.7.7) targets `net10.0`, has zero dependencies and no WinForms
references: PKHeX's GUI is a separate project. It is cross-platform without reservation.

The part that matters, from [PK3.cs](https://github.com/kwsch/PKHeX/blob/master/PKHeX.Core/PKM/PK3.cs):
the `PK3(Memory<byte>)` constructor decrypts automatically when needed (`DecryptParty()`),
so the 100 bytes read from RAM become an object exposing `Species`, `IV_*`, `EV_*`,
`Nature`, `Ability`, `Move1..4`, `HeldItem`, `Stat_Level` directly. On top of that,
`PersonalTable3` provides base stats, types and abilities, and `Learnset` gives learnable
moves.

This avoids hand-writing XOR decryption, substructure permutation and checksums, and
avoids redoing all of it for every future generation, where the format is considerably
more complex.

**Accepted cost:** PKHeX.Core is GPL-3.0-or-later and its copyleft extends through
linking. UltimatePoKeSync is therefore licensed GPLv3. Confirmed by Roberto on 2026-08-10,
having considered the alternative of a hand-written Gen 3 parser (about 250 lines) under a
permissive licence, rejected because it does not scale to later generations.

**Known limitation:** PKHeX.Core carries no competitive data (tiers, common spreads,
metagame items). Those remain our own datasets. See D-009.

---

## D-008. Defences against inconsistent RAM reads

**Status:** Accepted · 2026-08-10 · corrected during implementation

Reading every frame can capture a snapshot while the game is writing to that region (a
torn read), yielding a Pokémon that never existed.

Two defences, at two different levels.

**In the Lua script, confirmation across two reads.** A change seen once is not sent: it
is held as "pending" and only transmitted if the following read produces the same hash.
This costs one polling interval of latency (about 66 ms), imperceptible for a party.

This defence must live in the script, not the app: the script only transmits on change, so
a second identical read would never reach the C# side and the comparison there would be
impossible by construction. This was caught during implementation; the original version of
this decision placed the check in C#.

**In the C# parser, per-slot validation.** Each slot must pass, in order: `Species != 0`,
`FlagHasSpecies`, `ChecksumValid`, `!FlagIsBadEgg`, species ≤ 411.

The empirical findings that produced that list:

| Input                      | `ChecksumValid` | `FlagHasSpecies` | `Valid` |
| -------------------------- | --------------- | ---------------- | ------- |
| Real encrypted Pokémon     | `true`          | `true`           | `true`  |
| 100 random bytes           | **`false`**     | `false`          | `true`  |
| Empty slot (all zeroes)    | **`true`**      | `false`          | n/a     |

Two non-obvious consequences: `ChecksumValid` is not sufficient on its own, because an
all-zero slot passes it; and PKHeX's `Valid` property is useless as a filter, because it
stays `true` even for random bytes.

An empty slot beyond the declared count is not an error and is not reported. An empty slot
within the declared count is, because it signals a genuine inconsistency.

---

## D-009. Analysis is generation-aware from day one

**Status:** Accepted · 2026-08-10

Gen 3 rules differ from modern ones in ways that change the suggestions themselves, not
just the numbers:

- **17 types, no Fairy.** The type chart must be per generation.
- **The physical/special split is by type, not by move.** In Gen 3 every Water move is
  special and every Fighting move is physical, regardless of the move. Role inference
  (physical vs special attacker) and therefore the recommended nature and EVs follow
  different rules from Gen 4 onwards.
- **No hidden abilities**; the ability is a single bit.
- **EVs**: 510 total, 255 cap per stat (252 is merely the efficiency threshold).

Treating these as details to fix later would force a rewrite of the suggestion engine when
Gen 4 is added. The rules therefore sit behind a per-generation abstraction from the start.

---

## D-010. Two analysis profiles: playthrough and competitive

**Status:** Accepted · 2026-08-10

Roberto's choice, 2026-08-10.

- **Playthrough**: moves already available now in the learnset, obtainable items,
  realistic EVs, coverage against Gym Leaders and the League.
- **Competitive**: 252/252/4 spreads, speed benchmarks, metagame natures, items and EVs.

These are two sets of heuristics on top of the same analysis engine, switchable in the UI.
The engine computes facts (role, coverage, projected stats); the profile decides what to
recommend from those facts. The separation is binding from the start, otherwise the two
modes interleave and become unmaintainable.

---

## D-011. Application stack: .NET 10 and Avalonia

**Status:** Accepted · 2026-08-10

.NET 10 is forced by PKHeX.Core 26.x, which targets `net10.0` (D-007).

Avalonia for the UI: it runs natively on Windows, Linux and macOS including Apple Silicon,
unlike WPF. MVVM via CommunityToolkit.Mvvm.

**Alternatives considered:** MAUI (no Linux desktop support), Uno Platform (more setup, no
advantage here), web UI with a local backend (adds a browser and an HTTP layer for no
benefit in a local single-user app).

**Implementation note:** `Avalonia.Diagnostics` (DevTools) is not published for 12.x; it
stops at 11.3.20. Removed from the dependencies. Revisit when the Avalonia 12 equivalent
ships.

---

## D-012. The Lua script is a single file, not modules

**Status:** Accepted · 2026-08-10

mGBA's documentation does not state whether `require` and `package.path` work for local
modules next to the loaded script. Splitting the bridge into `ups/server.lua`,
`ups/games.lua` and so on would make it depend on undocumented behaviour, and would be
awkward in practice: the user loads one file from mGBA's menu.

`emulator-scripts/mgba/ups_bridge.lua` is therefore monolithic but sectioned. That is
acceptable because the script is deliberately dumb (D-006): it stays under about 300 lines
and will never contain domain logic.

---

## D-013. Gen 3 addresses cross-checked against three sources

**Status:** Accepted · 2026-08-10

The addresses in D-004 are confirmed by three independent sources, the last of which is
decisive:

1. Data Crystal (FireRed/LeafGreen RAM map).
2. `GameSettings.lua` from `40Cakes/pokebot-bizhawk`, a tool in active use.
3. `res/scripts/pokemon.lua` shipped by mGBA itself (tag 0.10.5): identical values,
   including `_partyMonSize = 100`.

The same source also provided the idiomatic socket-server pattern
(`res/scripts/socketserver.lua`), which the bridge follows: `socket.bind(nil, port)`,
`listen()`, `add("received", accept)`, with `socket.ERRORS.AGAIN` meaning "no data".

A useful finding: in Gen 3, revisions share the same addresses (FireRed Rev 1, Ruby Rev 1
and 2 inherit the base revision's). The game code alone is therefore enough to pick the
map, with no need for the ROM's CRC32. That may not hold for later generations, which is
why the key remains the full game code rather than just the title.

---

## D-014. PKHeX normalises types to modern indices: verified, not assumed

**Status:** Accepted · 2026-08-10

A classic Gen 3 trap: internal type IDs do not match modern ones, because index 9 in Gen 3
is the `???` (Mystery) type. Internally Fire is 10 and Water is 11; in the modern scheme
they are 9 and 10.

Getting this wrong would produce an entirely false type analysis with no visible error:
every Pokémon would take its neighbour's type.

Verified empirically against PKHeX 26.7.7:

| Pokémon   | `PersonalTable.E[id].Type1` | Reading |
| --------- | --------------------------- | ------- |
| Charizard | 9                           | Modern Fire ✓ (the Gen 3 internal ID would be 10) |
| Gyarados  | 10                          | Modern Water ✓ |
| Magnemite | 12                          | Modern Electric ✓ |

The same holds for `MoveInfo.GetType(id, EntityContext.Gen3)`. PKHeX normalises: there is
no conversion to write, and `PokemonType` can be cast directly.

The assumption is pinned by the `Parse_ReadsModernTypeIndicesNotGen3Internal` test,
because a PKHeX update could change it silently.

**Another confirmed Gen 3 detail:** nature is not stored, it is derived from the PID
(`PID % 25`). PKHeX computes it. Setting `Nature` on a `PK3` with a fixed PID has no
effect, which is worth remembering when building test fixtures.

---

## D-015. Mono-type Pokémon are normalised to `SecondaryType = None`

**Status:** Accepted · 2026-08-10

In the game data a mono-type Pokémon repeats the same type in both fields (Pikachu:
`Type1 = Type2 = 12`). Propagating that as-is would double that type's weight in every
aggregate defensive calculation.

The parser normalises the second type to `None` when it equals the first. Covered by the
`Parse_MonoTypeNormalisesSecondaryToNone` test.

---

## D-016. The repository is written in English

**Status:** Accepted · 2026-08-10

Everything committed is in English: commit messages, identifiers, comments, XML docs,
Markdown, CLI output, UI strings and test names.

Roberto's decision, 2026-08-10, after the first six commits had been written in Italian.
It is a GPLv3 open-source project intended to be readable by contributors who do not speak
Italian. The working tree was translated in one pass; the earlier commit messages were left
as they were, since rewriting unpushed history is not worth the churn.

---

## D-017. Western Emerald localisations share the USA addresses

**Status:** Accepted · 2026-08-10 · verified against the real ROM

The development ROM turned out to be Italian Emerald: game code `BPEI`, not `BPEE`. Under
D-005 the script correctly refused to read it, since it was not in the table.

Evidence that the addresses are shared, from `GameSettings.lua` in
40Cakes/pokebot-bizhawk:

- Emerald France (`BPEF`) and Germany (`BPED`) map to the same table index as the USA
  release, that is, the very same `pstats` and `pcount` entries.
- Emerald Spain (`BPES`) has its own index whose values are identical: `0x020244EC` /
  `0x020244E9`.

Every Western localisation therefore shares the EWRAM layout; only the text differs.
`BPEI` was added on that basis, together with `BPEF`, `BPED` and `BPES`.

Verified on 2026-08-10 against a real Italian Emerald ROM running in mGBA 0.10.5. The
starter appeared correctly the moment it entered the party:

```
┌─ POKEMON EMER [BPEI] rev0  ·  seq 3
│  [0] TREECKO  Lv.5  Grass
│      Nature Bashful · Ability Overgrow · Item -
│      IVs   HP 19  Atk 14  Def 24  SpA 29  SpD 0  Spe 25
│      · Pound  Normal  35/35 PP
│      · Leer   Normal  30/30 PP
└─
```

Species, level, type, ability, moves and IVs all consistent, and zero rejected slots across
the whole session. Since a wrong address could not produce a slot passing `ChecksumValid`,
the inference is confirmed rather than merely plausible.

The same session validated the change detection end to end: PP dropping during a battle
(35, 34, 33, 32) produced one snapshot each, and nothing in between.

Japanese releases genuinely do have different addresses (`0x02024190` / `0x0202418D` for
Emerald) and are deliberately left out until someone can test them.

**Side note:** the parser resolves species, item and ability names through PKHeX's English
string tables regardless of the ROM's language. On an Italian ROM the nicknames the player
typed come through correctly, because the Gen 3 character map is shared across Western
languages, but species names display in English. Worth revisiting when the UI gets
localised.

---

## D-018. Deciding when a change matters is its own layer

**Status:** Accepted · 2026-08-10

The provider knows how to obtain bytes. The parser knows how to read them. Neither is in a
position to judge whether a change deserves recomputing the analysis, and that judgement
has to happen somewhere, or the UI recomputes several times per second during every battle.

`UltimatePoKeSync.Session` holds `PartyTracker`, which sits between the two and emits a
party only when something analytically meaningful changed. It depends on `Contracts` alone,
so it knows neither mGBA nor PKHeX.

**What counts as meaningful:** species, personality value, level, nature, ability, held
item, egg flag, IVs, EVs, move IDs and slot order.

**What does not:** current PP, current HP, and any other battle state. A single battle turn
moves PP; none of it changes a recommendation about EVs, nature, moves or items.

Three further rules live here:

- **Out-of-order snapshots are dropped**, except when the sequence restarts at 1. Reloading
  the script or resetting the emulator restarts the counter, and mistaking that for a stale
  message would leave the tracker deaf until the sequence climbed back past the old
  high-water mark.
- **Snapshots with rejected slots are skipped** in favour of the last good party, so a torn
  read does not make the team flicker down a member and back.
- **But only five times in a row.** A genuinely broken party, such as a bad egg, is a real
  and permanent state, and would otherwise stall the display forever. After five attempts
  the tracker concludes the problem is the party rather than the timing, and reports what it
  sees.

The comparison key is a string rather than a hash: at 15 updates per second the cost is
irrelevant, and a hash collision would silently swallow a real party change, which is
exactly the failure this layer exists to prevent.

---

## D-019. Slots past the declared party count are never read

**Status:** Accepted · 2026-08-10

Found by capturing real RAM rather than by reasoning: in the Italian Emerald capture with
one Pokémon in the party, none of the other five slots was all zeroes. The game does not
reliably wipe a slot when a Pokémon leaves the team.

In that capture the leftovers decoded to `Species = 0`, so nothing surfaced. But the bytes
left behind by a Pokémon deposited in the PC are a complete, checksum-valid Pokémon. The
original implementation examined all six slots and admitted any that passed validation, so
it would eventually have shown a team member the game no longer had.

The parser now iterates only up to `PartyCount`. This is also what `docs/protocol.md`
already promised ("the app uses it as an upper bound"); the code simply had not implemented
its own contract.

Trusting the count is safe because the script confirms it across two consecutive reads
(D-008): a torn count byte would have to read identically twice to get through.

Covered by `Parse_ValidPokemonBeyondTheCountIsNotResurrected` and by the real-RAM fixture
test, which asserts that the leftover bytes are genuinely non-zero, so the test would notice
if a future capture stopped exercising the case.

---

## D-020. Test fixtures captured from real RAM, not only hand-built

**Status:** Accepted · 2026-08-10

Fixtures built with PKHeX prove the parser agrees with PKHeX's own writer. They cannot
prove it agrees with what the game actually puts in memory, and that is the claim the whole
project rests on.

`upks --dump <dir>` writes every raw snapshot as a JSON fixture. The first one,
`tests/…/Fixtures/emerald-it-treecko.json`, is 600 bytes captured from Italian Emerald
running in mGBA, and it immediately paid for itself by exposing D-019.

Implementation note: `--dump` needed no change to the mGBA provider, the parser or the
tracker, just a `DumpingEmulatorProvider` wrapped around the real one.

---

## D-021. Battle facts come from embedded, validated per-generation data

**Status:** Accepted · 2026-08-10

The type chart and move base powers are versioned as embedded JSON in `GameData` and loaded
through an `IGenerationRules` resolved by generation. The loader validates the generation,
the exact ordered type set, every matrix dimension and multiplier, and the expected move
count before exposing any data. There is no runtime network access and no silent fallback
to another generation.

The Gen 3 data is transcribed from the matching `pret/pokeemerald` decompilation tables:
`gTypeEffectiveness` in `src/battle_main.c` and `gBattleMoves` in
`src/data/battle_moves.h`. Keeping move power as data is necessary because PKHeX exposes a
move's type and PP but not its Gen 3 base power; without it a status move such as Leer
would incorrectly count as Normal offensive coverage.

`IGenerationRules` also owns the type-based physical/special split and the six defensive
ability modifiers in M5's scope. This prevents Gen 3 assumptions from leaking into the
analysis engine and gives a future generation one replacement boundary.

**Alternatives considered:** hard-coded C# dictionaries (rejected: difficult to audit as a
complete chart), a current-generation web API (rejected: not generation-stable and would
make an offline app network-dependent), treating every known move as damaging (rejected:
produces false offensive coverage), and putting ability switches directly in the analysis
engine (rejected: they are generation rules, not analysis heuristics).

---

## D-022. Coverage is a complete fact matrix; gaps mean no available answer

**Status:** Accepted · 2026-08-10

The M5 analyzer returns one defensive and one offensive entry for every type available in
the snapshot's generation, in stable chart order. It keeps the per-Pokémon and per-move
matchups rather than collapsing them into a single score, so the UI and both recommendation
profiles can explain every result.

A **defensive gap** is an attacking type that is super effective against at least one party
member and for which no other member has an ability-adjusted resistance or immunity. A
neutral member is not considered a safe defensive answer. An empty party has no weaknesses,
not seventeen defensive gaps.

An **offensive gap** is a single defending type that none of the party's currently known,
damaging moves can hit super effectively. Coverage is not limited to STAB: a coverage move
exists precisely to answer types outside its user's own typing. Status moves are excluded
using the generation's move-power data. Fixed-damage and one-hit knockout moves are also
excluded: their battle scripts may observe immunities but do not apply a super-effective
damage multiplier. Variable-power attacks such as Low Kick and Hidden Power remain valid.
The result records the Gen 3 physical/special category as a fact for later role inference.

The analyzer itself has no playthrough/competitive switch. It computes only facts; the two
profiles from D-010 decide how to turn those facts into recommendations in M6.

**Alternatives considered:** a weighted team score (rejected: hides which member or move
caused the result), declaring a gap at an arbitrary weakness-count threshold (rejected:
profile policy masquerading as a fact), STAB-only offensive coverage (rejected: ignores the
purpose of coverage moves), and passing an analysis profile into the core analyzer
(rejected: would mix facts and recommendations in violation of D-010).

---

## D-023. Nature and projected-stat calculations are generation facts

**Status:** Accepted · 2026-08-10

Nature definitions and projected stat calculations belong to `IGenerationRules`, not to a
recommendation profile. Both playthrough and competitive advice need the same answer to
"what would this Pokémon's stats be at this level with this spread and nature?". Profiles
may choose different inputs, but must not reimplement the formula.

The 25 Gen 3 natures are embedded, ordered by the same ID derived from `PID % 25`, and
validated at load time. The calculator uses the game's integer operations, enforces IV
`0..31`, Gen 3 EV `0..255` and total EV `<= 510`, and preserves Shedinja's one-HP special
case. It accepts a proposed level, nature and EV spread so profiles can compare alternatives
without mutating the live `PokemonSnapshot`.

**Alternatives considered:** calculate stats inside each profile (rejected: duplicated game
rules would drift), use floating-point formulae and round at the end (rejected: the games
truncate at specific intermediate steps), and return only percentage deltas (rejected: EV
and speed recommendations need exact projected values).

---

## D-024. Recommendations combine explainable inference with offline presets

**Status:** Accepted · 2026-08-10

M6 uses a hybrid recommendation model. The core first infers a broad role from auditable
facts in the live snapshot: base stats, the Gen 3 physical/special split, and how many
current moves scale from each offensive stat or provide utility. Recommendation profiles
may then use versioned reference presets as priors for plausible roles and movepools. A
preset never overrides live Pokémon data, generation rules, move legality, team gaps, or
profile policy; missing preset data falls back to the deterministic inference.

The first reference catalog is Pokémon Showdown's Gen 3 Random Battle set data, pinned to
commit `db93869dcc216c0be39e7f86e9a64edcc7496d89` and embedded for offline use. Its 220
species and 393 sets are treated as broad expert-authored role and movepool examples, not
as standard OU usage or as complete competitive builds. The source is MIT-licensed and is
recorded in `THIRD_PARTY_NOTICES.md`.

A future competitive profile may use pinned Smogon ladder statistics whose generated data
is MIT-licensed as a weighting signal. Smogon Dex editorial sets must not be bundled
without explicit permission. No recommendation path performs a runtime network call.

**Alternatives considered:** derive every recommendation from first principles (rejected:
deterministic but unnecessarily reinvents expert movepool knowledge), reproduce one preset
verbatim (rejected: ignores the live team and confuses Random Battle with standard play),
query an online service at runtime (rejected: harms determinism and offline operation), and
bundle Smogon Dex sets without permission (rejected: their reuse terms require permission).

---

## D-025. Recommendations expose candidates and availability, not false certainty

**Status:** Accepted · 2026-08-10

The M6 engine returns a ranked, explainable candidate pool rather than pretending that a
single four-move build is universally best. It computes team and role facts once, then
delegates nature, EV, move and item policy to one of two injectable profiles. Reference
catalogs declare their generation, so Gen 3 data cannot silently serve a later generation.

The competitive profile marks preset-derived moves and items as competitive references.
The playthrough profile distinguishes three facts:

- a currently known move or held item is known to be available;
- a level-up move at or below the current level, a type-boosting item, or a berry still
  requires an availability check;
- competitive reference candidates make no claim about story progression.

That distinction is necessary because party RAM contains no bag, badges, map progress,
Move Reminder access, or cross-game transfer history. The pinned Showdown learnset is also
generation-wide rather than Emerald-specific. Until a later data source supplies those
facts, the playthrough profile may surface these candidates but must not label them as
currently obtainable. Exact competitive EV targets and softer playthrough training
priorities are separate result shapes rather than one spread applied to both modes.

**Alternatives considered:** emit an opaque final build (rejected: hides trade-offs and
team context), assume every legal level-up move and common item is immediately obtainable
(rejected: false for many save states), suppress every candidate not present in the party
(rejected: safe but not useful), and parse the entire save game's progression state inside
M6 (deferred: a meaningful scope expansion that should be designed as its own input).

---

## D-026. The diagnostic CLI is the verification surface for every analysis layer

**Status:** Accepted · 2026-08-11

M5 and M6 shipped fully tested but unreachable: nothing outside the test projects referenced
`TeamAnalyzer` or `PokemonRecommendationEngine`, and the CLI did not even reference
`UltimatePoKeSync.Analysis`. Every earlier milestone was closed by watching it work against
the real Italian Emerald; the analysis layers had never been through that check.

The CLI therefore renders every layer, behind opt-in flags so the M3 party diagnostic stays
readable: `--analyze` prints type coverage and unanswered gaps, and `--recommend
<playthrough|competitive>` prints per-Pokémon role, nature, EV, move and item candidates
with their availability labels. When a profile is selected the CLI reuses the
`TeamAnalysis` the engine already computed rather than analysing twice. Rendering lives in
`AnalysisReport` and formats only: it never re-ranks, re-filters or reinterprets a result,
so what appears on screen is exactly what a future UI will receive.

`--replay <fixture>` renders one dumped snapshot and exits. A capture from real RAM is
already the project's fixture format, so replaying one exercises the whole parse, analyse
and recommend chain with no emulator, and makes any output problem reproducible in a bug
report rather than only reproducible with a specific save.

`NotSupportedException` from an unsupported generation is caught per snapshot and printed as
a line: a 15 Hz stream must not die because one layer cannot serve the current game.

**Alternatives considered:** wait for the M7 Avalonia dashboard (rejected: leaves two
milestones unverified against real hardware and puts the first real look at the output
behind UI work), print analysis unconditionally (rejected: buries the parsing diagnostic
the CLI exists for), and let the CLI re-rank or trim candidates for readability (rejected:
the console would then verify the console's policy instead of the engine's).

---

## D-027. Level-up learnsets are keyed by game, not by generation

**Status:** Accepted · 2026-08-11

The pinned Showdown learnset tagged every Gen 3 level-up entry as `3L`, merging Ruby and
Sapphire, Emerald, and FireRed and LeafGreen into one table. The importer resolved the
disagreements with `Math.min` over the levels, which is not a conservative choice but a
wrong one: it reports a level the running game does not use.

Measured against PKHeX, 42 of the 386 Gen 3 species teach at least one move at a different
level depending on the game. Zubat is the clean example: Supersonic at 6 and Astonish at 11
in RSE, exactly reversed in FRLG. On FireRed the merged table claimed a level-6 Zubat could
already know Supersonic. The failure is invisible: a plausible number in a field that looks
authoritative.

So the level-up learnset moves behind `ILevelUpLearnsetSource`, which takes a
`GameIdentity`. Move identity stays behind `IMoveReferenceCatalog` keyed by generation,
which is the correct granularity for it: a move's number and name are stable within a
generation, and its type changes only across generations (Charm is Normal in Gen 3 and
Fairy from Gen 6).

The implementation reads PKHeX's `LearnSource3E`, `LearnSource3RS`, `LearnSource3FR` and
`LearnSource3LG`. PKHeX is already a dependency for parsing, already GPLv3, and already
ships one learn source per game for every generation from RB to SV. Choosing it removes a
bundled dataset instead of adding one, and the same class shape will serve Gen 1 to Gen 9
unchanged, which matters because supporting every generation is a stated goal.

It lives in its own project, `UltimatePoKeSync.GameData.Learnsets`, so D-007 still holds:
`Analysis`, `Contracts` and the UI depend on the abstraction and never on PKHeX. That costs
the engine its parameterless constructor, since the default composition cannot name a type
Analysis cannot see, so `PokemonRecommendationEngine.CreateDefault` now takes the learnset
source and the composition root supplies it. `Parsing` is consequently no longer the only
project that references PKHeX; the invariant was always "the analysis layer does not", and
that is what the comment in each project now says.

**Alternatives considered:** keep Showdown and pick the maximum instead of the minimum
level (rejected: still one number for three games, wrong in the other direction), generate
per-game datasets from the `pret` disassemblies (rejected: one importer per game, Gen 3
only, and the whole exercise repeats for every future generation, while PKHeX has already
done it), and expose PKHeX types through `GameData` directly (rejected: drags the
dependency into everything that reads game data, including the UI).

---

## D-028. The dashboard answers, and shows its working

**Status:** Accepted · 2026-08-11

M7 turns the analysis layers into an application a player can use without a terminal. Three
choices shape it.

**The window formats; it never decides.** Coverage, roles, strength and builds are computed
in `Analysis` and rendered by view models that only select and format. The dashboard and the
CLI therefore agree by construction rather than by discipline, and a disagreement between
them is a bug with one place to fix.

**A score is never shown alone.** D-022 rejected opaque numbers, and a team strength
indicator is exactly the thing that becomes one. `TeamStrength` is a list of attributed
factors (party size, level cohesion, defensive coverage, offensive coverage, nature fit,
effort-value fit) each carrying its points, the fact behind them and the members
responsible. The score is their sum, and the panel always shows the breakdown beside it.
The effort-value factor measures values spent on the wrong stats rather than values not yet
spent, because story play rarely trains at all and reporting that as a weakness is noise.

**A recommended build, without dropping the candidate pool.** D-025 deliberately refused to
name one best set. A player wants an answer, so `RecommendedBuild` picks four moves and
states the reason for each, ranked by same-type damage, the category the role actually
scales with, whether the move closes a gap nothing else on the team answers, and continuity
with what is already known. What it turned down stays visible as alternatives. The engine
still exposes every candidate; the build is one more result, not a replacement.

Sprites are placeholders: a tile in the primary type's colour with the species name and
level. Pokémon sprites are Nintendo and Game Freak's, and bundling them in a public GPLv3
repository is a licensing problem rather than an asset problem. Reading them from the
player's own ROM through the existing bridge is the clean route and is its own milestone.
Every coloured element also carries its type in text, so the display survives both colour
blindness and a screenshot.

Distribution is a GitHub Actions workflow that publishes self-contained single-file builds
for Windows, Linux and both macOS architectures and attaches them to the release. A user
needs no .NET SDK, no clone and no command line. Compression brings a build to about 62 MB,
verified locally. The macOS builds are wrapped in an unsigned `.app` bundle, so the first
launch needs right-click then Open; notarising would require a paid Apple Developer account
and is a business decision, not a technical one. The bridge script is copied next to the
executable so the setup screen can hand over a path that exists on the user's disk.

`ILiveTeamSource` exists so the dashboard can be driven by the Italian Emerald capture in a
test, and the view model takes its UI-thread dispatch as a delegate so that test needs no
Avalonia application. Without both, the only way to check the window would be to have mGBA
running, which is the kind of verification that never gets done.

**Alternatives considered:** put the analysis logic in the view models where it would be
convenient (rejected: two implementations of the same answers, drifting), show the strength
score alone as a headline number (rejected: unactionable, and D-022 already settled it),
have the build replace the candidate list (rejected: hides the trade-off the profiles exist
to expose), bundle a sprite pack (rejected: not ours to redistribute), and commit prebuilt
binaries to the repository (rejected: bloats history and still fails Gatekeeper).

### Amendment, 2026-08-11: macOS builds must be produced on macOS

`v0.1.0` cross-published every runtime from one Linux runner. The Apple Silicon download
was reported as "damaged and should be moved to the Trash". That is not the Gatekeeper
warning it looks like: Apple Silicon requires every executable to carry a signature, at
minimum an ad-hoc one, and the kernel `SIGKILL`s anything without one. Reproduced locally:
stripping the signature from a working build makes it exit with 137, and re-applying
`codesign --sign -` makes it run again. Right-click then Open cannot rescue it, because the
problem is not the quarantine attribute.

`codesign` only exists on macOS, so the macOS artifacts now build on a `macos-latest`
runner, where the .NET SDK ad-hoc signs the apphost anyway, and the finished bundle is
signed and verified. Packaging uses `ditto` rather than `zip`, which preserves the symlinks
and permissions a signature covers. Verified locally end to end: sign, package, unpack,
`codesign --verify --deep --strict`, launch.

Notarising, which would remove the right-click step entirely, still needs a paid Apple
Developer account and remains a business decision.

---

## D-029. The setup screen points at a stable folder, not at the executable

**Status:** Accepted · 2026-08-11

The setup screen showed the bridge script where it shipped, next to the executable. On
macOS that produced a path like
`/private/var/folders/ch/…/AppTranslocation/005EAC16-…/d/UltimatePoKeSync-2.app/Contents/MacOS/emulator-scripts/ups_bridge.lua`.

That is App Translocation. An app opened straight from Downloads, still carrying the
quarantine attribute and never moved in Finder, is executed from a randomised read-only
copy. Everything works, but every path the app reports about itself is a throwaway. The one
step the user has to perform by hand, finding this file in mGBA's open dialog, became the
hardest one in the whole product.

The script is therefore copied on startup into a per-user folder that does not move:
`~/Library/Application Support/UltimatePoKeSync` on macOS, `%APPDATA%\UltimatePoKeSync` on
Windows, `$XDG_DATA_HOME` or `~/.local/share/UltimatePoKeSync` on Linux. The copy is
refreshed whenever the shipped script differs, so updating the app updates the script.
Alongside the path there is a **Show in Finder** button, because selecting a file in a file
manager beats retyping a path, and the macOS steps start by telling the user to move the app
into Applications, which is what stops translocation happening at all. When the app detects
it is translocated it says so directly instead of leaving the user to wonder about the
strange path.

`~/Documents` would have been the most discoverable location, but writing there triggers a
macOS privacy prompt, and asking for a permission dialog during setup trades one confusion
for another. Application Support needs no permission, and the reveal button removes the only
downside of a hidden folder.

**Alternatives considered:** keep using the executable's folder and tell people to move the
app first (rejected: the instruction arrives after the broken path is already on screen),
write next to the ROM (rejected: the app does not know where the ROM is, and that folder is
the user's, not ours), and embed the script and write it to a temporary file on demand
(rejected: a temporary path is exactly the problem being fixed).

---

## D-030. Machines and tutors are move sources, and a build is chosen one slot at a time

**Status:** Accepted · 2026-08-11

Recommendations only ever proposed level-up moves. In Gen 3 that is close to useless for a
playthrough: the coverage a team is actually short of arrives on a TM. A Charizard was being
told to consider what it learns by levelling while Flamethrower, Earthquake and Fire Blast
sat in the machine list, unmentioned.

PKHeX answers this through the learn sources already in use. `ILearnSource.GetAllMoves`
takes a `MoveSourceType` and is public, so machines and tutors need no new bundled data, and
the answer is per game. Charizard has twenty tutor moves in Emerald and one in FireRed.
Merging that would have repeated the mistake D-027 fixed, on a much larger scale than levels
ever did.

`ILevelUpLearnsetSource` therefore becomes `IMoveLearnSource`, returning level-up moves at
or below the current level, then machines, then tutors, each tagged with how it is obtained.
Level-up entries still come from `GetLearnset` rather than the flag scan, because it is the
only source that carries the level, and the level is what a player acts on.

**Egg moves are deliberately excluded.** A Pokémon already in the party cannot acquire one:
it had to hatch with it. Listing them would be the false certainty D-025 exists to prevent.

Everything that is not already on the Pokémon keeps
`RecommendationAvailability.RequiresAvailabilityCheck`. That stays honest for machines for a
reason particular to this generation: a Gen 3 TM is consumed when used, so even owning one
is not a guarantee.

Widening the pool immediately exposed a flaw in `SelectBuild`. Ranking every candidate in
isolation and taking the top four gave a Treecko three Grass moves, each explaining itself
as "the only Grass damage the party has for a type nothing else answers", three times, for
one gap. Selection is now greedy: after each pick, damaging candidates repeating a type the
build already hits lose more than the same-type bonus is worth, and only the first move of a
type may claim the gap. A second same-type move can still win a slot, but it says plainly
that it is there because nothing else scored higher.

**Alternatives considered:** embed a TM/HM and tutor table of our own (rejected: PKHeX
already has one that is correct per game, and hand-entered tables are how invented data gets
in, see D-013), reach into PKHeX's internal `Tutor_E` and `GetIsTM` by reflection (rejected:
silently breaks on upgrade, which is the failure mode D-014 exists to prevent), mark machine
moves as available since most players eventually own the TM (rejected: single use in Gen 3,
and the label would be a guess), and cap the candidate pool by taking fewer level-up moves
(rejected: hides the machines that are the whole point).

---

## D-031. A build is chosen for a team, not for a Pokémon in isolation

**Status:** Accepted · 2026-08-11

Once machines and tutors widened the candidate pool (D-030), three faults in `SelectBuild`
became impossible to miss. All three came from the same mistake: scoring each move on its
own and taking the top four.

**One Pokémon repeated itself.** A Treecko was handed Solar Beam, Giga Drain and Bullet
Seed, each explaining itself as the answer to the same gap. Selection is now greedy, and a
damaging move repeating a type the build already hits loses more than the same-type bonus is
worth.

**Six Pokémon repeated each other.** Members were built independently against one fixed
`TeamAnalysis`, so every one of them saw the same holes and reached for the same move. The
engine now builds in party order and threads a set of answered types through: it starts from
what the party's current moves already cover, and grows with every build chosen. The same
set also grows inside a build, which is what stops the second slot claiming a hole the first
slot just closed. The version before this printed "nothing else hits Water hard" one line
under a move that hits Water hard.

**Every slot was an attack.** Utility scored nothing unless the Pokémon was a wall, so
attackers got four attacks, which is not what a real set looks like and is helpless against
anything it cannot out-damage. Non-damaging moves now score for everyone, more for a Pokémon
whose bulk is the reason it is on the team, and a build takes at most three attacking moves
while a utility candidate remains.

Two smaller changes come with it. Moves that beat a type the party is defensively weak to now
score: being able to knock out what threatens you is the other half of a type problem. And
every slot carries a `BuildSlotRole` (same type, coverage, team support, utility, filler) so
the answer is scannable before it is read. The parallel `Moves` and `Reasons` lists that
could drift out of step are gone; a slot is one object.

Move candidates were already generation-correct, because the move catalog holds only the 354
Gen 3 moves and anything outside it resolves to nothing. That was incidental rather than
stated, so it is now pinned by a test.

**Alternatives considered:** score the whole party jointly and optimise across it (rejected:
the result stops being explainable per Pokémon, which is the property D-022 and D-028 are
built on), let each member see the others' candidate pools rather than their chosen builds
(rejected: a candidate is not a commitment, so the coordination would be based on something
that may never happen), and forbid a second move of a type outright (rejected: sometimes a
physical and a special move of the same type are both right, so it should cost rather than
be banned).

---

## D-032. The competitive pool is the widest one, and every move says how it is obtained

**Status:** Accepted · 2026-08-11

Two faults found by using the app rather than reading it.

**The profiles were inverted.** The competitive profile drew its candidates only from the
pinned Random Battle sets, falling back to whatever the Pokémon happened to know when no set
matched. The playthrough profile drew from the full learn source. So a level 5 Treecko, which
has no Random Battle entry because it is unevolved, was told to run Pound and Leer
competitively, while the playthrough profile offered it Solar Beam and Thunder Punch. That is
backwards: story play is the constrained case, battling is the one where any legal move is
reachable.

The competitive profile now reads the whole learn source, at level 100, and keeps the
reference sets as a ranking prior rather than as the pool. A competitive Pokémon is a trained
one: nobody battles with the level it was caught at, and a move it learns at 45 is a move it
will have. Reference-set moves the learn source does not reach, mostly egg moves, are still
kept, marked as coming from the set rather than from the game.

**A build never said how to get the move.** The panel told a player to run Dig with no hint
that it means finding TM28, and Thunder Punch with no hint that it means a move tutor. Every
slot now carries its source in the player's terms: already knows it, learns it at level 16,
from a TM or HM, from a move tutor, from a common set.

One thing followed from widening the pool: with every legal move in reach, the build picked
Absorb over Solar Beam, because nothing in the score cared about how hard a move hits. Base
power was in `Gen3Rules` but private, so `IGenerationRules` now exposes it and a move earns
up to four points for it: enough to separate a 120-power move from a 20-power one, not enough
to outweigh coverage. Gen 3's power-1 sentinel for run-time-decided moves is read as average
rather than as almost nothing.

**Alternatives considered:** keep the competitive profile preset-only and accept that
unevolved species get nothing useful (rejected: it is the case where advice is most wanted,
since the player is deciding whether to evolve), use the Pokémon's current level for the
competitive pool (rejected: the profile already proposes exact EV spreads that require
training, so pretending the level is fixed is inconsistent), and rank strictly by base power
(rejected: it would hand every Pokémon four 120-power moves and ignore what the team needs).

---

## D-033. Sprites come from the player's own cartridge, so the bridge learned to answer

**Status:** Accepted · 2026-08-11

Pokémon sprites are Nintendo's, and a public GPLv3 repository is not the place to
redistribute them. The player already owns a copy of every sprite they will ever see: it is
in the ROM they are playing. Reading them from there is both authentic and clean, and it
needs nothing bundled.

**Nothing is hard-coded, because nothing can be.** The pointer tables move with every build
of every localisation (the Italian Emerald keeps its front-sprite table at `0x08300DDC`, and
no other release is obliged to agree) so a table of addresses would be a table of guesses.
They are found from the shape of the data instead: a run of eight-byte records, each a valid
ROM pointer with a size of `0x800` and a tag counting up from zero. Nothing else in sixteen
megabytes looks like that four hundred times over. Emerald animates its front sprites and not
its back ones, so the front table is the one whose entries decompress to two frames; where
that does not hold, the reader says it cannot tell rather than picking, and the coloured tile
stays.

**The bridge had to become two-way.** Scanning means reading, and the script only ever spoke.
Protocol 2 adds one command, `read`, taking an address and a length and answering with base64
bytes, capped at 256 KiB. The script still ships bytes and no meaning (D-006); it now ships
them on request as well as on change. This revises D-003's "the app sends no commands", which
was written when nothing needed to ask.

It reads a window, not the cartridge. Sixteen megabytes over the wire would stall emulation
for seconds; the tables sit near offset `0x300000`, so two reads normally suffice, widening if
they do not, and sprite data is fetched per species a few kilobytes at a time, only for what
is in the party, cached, failures included.

The same command is the missing input behind every "check availability in this save" the app
prints. The bag and the badge flags are in the same memory, and reaching them is now a matter
of knowing where rather than of building a channel.

Two things were nearly got wrong by assuming, and were caught by checking. Gen 3's internal
species order is not the national one plus a constant: Treecko is 252 and 277, which looks
like an offset of 25 until Pelipper turns out to be 279 and 310. The conversion comes from
PKHeX's table, and the test names the species where arithmetic would have been plausible and
wrong. And `StreamWriter` puts a byte-order mark in front of the first line it writes, which
is not JSON: found by a test, not by a user.

The decoder was verified by decoding six species from the Italian Emerald and looking at
them. Its unit tests are entirely synthetic, because no sprite byte belongs in this
repository.

**Alternatives considered:** bundle a sprite pack (rejected: not ours to redistribute), have
the script send the whole ROM on connect (rejected: twenty-one megabytes of base64 per
connection to use less than one), ask the user to locate the ROM file (rejected: it makes
someone hunt for a file already open in the emulator beside them, and it does nothing for the
bag), and hard-code the tables per game code (rejected: they differ per localisation, so the
constants would be wrong for most players and wrong invisibly).

### Amendment, 2026-08-11: what the first live run cost

Everything above was written before the bridge had ever answered a real request. Three things
only showed up once it did.

**A socket takes what fits and tells you how much that was.** The script sent each line with a
single `client:send` and discarded the remainder, which for a 350 KB reply is most of it. Two
reads in twelve failed, always the first two, which are the ones the app needs to start. There
is an outbox per client now, pushed as far as the socket allows and flushed on the next frame.
Sixteen consecutive 256 KB reads succeed where ten of twelve did before. The party stream had
the same latent flaw and is fixed by the same queue.

**Telling the front table from the back one needs bytes the app has not fetched.** The reader
decompresses the first entry to count frames, and that data lives elsewhere in the ROM,
outside the window. The offline check passed for a reason the app did not have: the whole
cartridge was in memory. The choice moved to the caller, which is the only party that can go
and get more; the reader now reports candidates and stops there.

**The script already had a base64 encoder** for the party payload. The one added for `read`
was a second copy of it.

Verified live afterwards: Treecko, Charizard and Pikachu decoded through the production path
against the running emulator. 1.1 s for the first, covering a megabyte of window and the table
identification, and about 30 ms each after that.

### Amendment, 2026-08-11: three things the first day of use found

**An unanswered read is not an answer.** The source treated a failed probe as "this game has
no sprites" and latched it, so closing the emulator for a moment left the tiles blank for the
rest of the session. Failure now says either "not now" or "never", and only the second is
remembered, reached when the ROM has been read through and holds nothing we recognise. The
per-species cache follows the same rule: a decode that never received its bytes is not
recorded as a species without a sprite.

**The frame count only works where there are frames to count.** Emerald animates its front
sprites, so two frames means front; Ruby, Sapphire, FireRed and LeafGreen do not, and there
every candidate holds one frame. The tables are emitted in declaration order with the front
one first, so the lowest address is taken when the frame count decides nothing. That also
picks Emerald's still-front table if the animated one is ever missed, which is a fine sprite.
Verified on Emerald; the other four games remain unverified, and a wrong guess there shows a
back sprite rather than nothing, which is a visible, reportable failure rather than a silent
one.

---

## D-034. A ROM can be asked which addresses it uses

**Status:** Accepted · 2026-08-11

Four more games arrived, Italian Ruby, Sapphire, FireRed and LeafGreen, and none of them was
supported. The table held `AXVE`, `AXPE`, `BPRE` and `BPGE`, the USA releases; the Italian
ones are `AXVI`, `AXPI`, `BPRI` and `BPGI`. Emerald had five localisations because D-017
established they share a layout; the other four games had one apiece.

Adding them meant answering whether an Italian release keeps the USA addresses, and the
sources D-013 cites do not list these codes. So the question went to the ROMs themselves.

An address cannot be read off a ROM, but it is compiled into it: every routine that touches
the party loads it from a literal pool, so the value appears in the image again and again. A
value the game never uses appears zero times, and a random 32-bit constant is expected 0.004
times in sixteen megabytes. That makes a clean test, and the Italian Emerald calibrates it,
because its address is already verified.

The result is diagonal, which is what a real signal looks like:

| ROM | Emerald address | FireRed/LeafGreen | Ruby/Sapphire |
| --- | --- | --- | --- |
| BPEI Emerald | **1091** | 1 | 3 |
| BPRI FireRed | 0 | **740** | 0 |
| BPGI LeafGreen | 0 | **740** | 0 |
| AXVI Ruby | 0 | 0 | **57** |
| AXPI Sapphire | 0 | 0 | **57** |

Each game uses exactly one family and nothing else. The four codes are therefore filed with
their USA counterparts, and `tools/check-gen3-addresses.py` reproduces the check for any ROM,
including one already on file, where a mismatch would mean the entry is wrong.

What this does not establish is that the layout at that address is what the parser expects.
Only loading the game shows that, and the parser fails loudly if it is wrong: checksum
validation rejects slots rather than inventing Pokémon (D-008).

The same four ROMs settled the open question from D-033. Only Emerald animates its front
sprites, so the frame count decides nothing elsewhere; all four turned out to hold exactly two
candidate tables, and decoding Bulbasaur from the lower-addressed one gives the front sprite
in every case. The fallback is now verified on five games rather than assumed on one.

**Alternatives considered:** add the codes and hope (rejected: that is how invented data
enters, and D-005 exists because of it), leave them unsupported until an external source lists
them (rejected: the evidence is stronger than a third-party table, and it is in the user's own
hand), and infer the address at run time by scanning for it (rejected: the scan needs to know
what it is looking for, which is the question).

---

## D-035. A localisation can move the party, and one sample cannot say why

**Status:** Accepted · 2026-08-11

An Italian Ruby with a Mudkip in the party was reported as having none. The bridge was
connected, the game recognised, the read successful, and the answer zero: the most convincing
kind of failure, because nothing anywhere looks wrong.

Reading the live memory found it at once. The count byte the table pointed at held `00`;
sixteen bytes further on it held `01`, with the party data sixteen bytes after that.

That ROM is revision 1, so the first conclusion was that revisions move the party, and the
table gained per-revision overrides. That conclusion was wrong. The next game tried was an
Italian Sapphire, revision 0, and its party sits at exactly the same shifted address. One
sample supported two explanations and the wrong one was picked; a second sample was enough to
tell them apart.

The truth is that the Italian Ruby and Sapphire keep their party sixteen bytes above the USA
ones. A localisation can move what a revision does not, the opposite of D-017, where five
Emerald localisations share one layout. There is no rule here to generalise from: each game
code is its own entry, and the ones that have been run are the ones that are known.

| | USA, from the sources of D-013 | Italian, verified live |
| --- | --- | --- |
| count | `0x03004350` | `0x03004360` |
| party | `0x03004360` | `0x03004370` |

The revision machinery was removed with the conclusion that produced it. Nothing used it, and
a mechanism kept for a reason that turned out false is worse than no mechanism.

Two things are worth keeping from how this was found. It was found with the app's own two-way
bridge (D-033): thirty-two kilobytes of IWRAM in four requests, then the real parser slid
across every four-byte offset until `MUDKIP Lv.5 HP 21/21` appeared. A tool built for sprites
diagnosed the parser.

And it shows the limit of D-034's literal counting. That method proved these ROMs use the
Ruby/Sapphire address family and it was right: `0x03004360` genuinely appears fifty-seven
times, because in these releases it is the count. Counting a constant tells you the game uses
it. It cannot tell you what for.

**Alternatives considered:** keep the per-revision overrides in case they are needed later
(rejected: no entry uses them and the evidence for them evaporated), and find the party at run
time by scanning memory for something that parses (rejected: it works, as this investigation
shows, but searching for data that merely looks valid is how a wrong answer becomes a
confident one, which is why D-005 and D-019 both exist).

---

## D-036. An egg fills a slot, not a place on the team

**Status:** Accepted · 2026-08-12

An egg in the party is a full Pokémon record on disk. It has a species, a personality value,
IVs, EVs, a nature, base stats and a level, and the parser reads every one of them. So every
layer above the parser treated it as a team member: its types were counted towards the party's
resistances, its moves towards the party's coverage, its level towards the team's cohesion,
and it was handed a role, a nature, an EV spread and a best moveset.

None of that is true of an egg. It cannot be sent out, cannot attack, cannot be taught a move
and cannot be given an item. A party of five Pokémon and an egg is a party of five. Counting
the egg credits the team with a switch-in it does not have, and that is wrong in the player's
favour, which hides a real gap.

The contracts now draw the line once, and everything above reads it:

```csharp
public bool CanBattle => !IsEgg;                       // PokemonSnapshot
public IReadOnlyList<PokemonSnapshot> Battlers { get; } // PartySnapshot, the ones that can
```

`TeamAnalyzer`, `TeamStrengthAnalyzer` and `PokemonRecommendationEngine` iterate `Battlers`.
`Members` still exists and still holds all six slots, because the window has to draw the egg:
it is in the party and the player can see it there.

The second half is what the tile is allowed to say. The species inside an egg is in the bytes,
and the game deliberately does not show it. The window therefore shows an egg as **Egg** with
no nickname, no types, no HP bar, no stats, no matchups, no moves and no sprite, and one line
saying why it is not part of the team's numbers. The recommendation card is absent, because
the engine produced nothing for that slot; slots are paired to recommendations by `SlotIndex`,
so a shorter list lines up correctly rather than shifting everyone by one.

In place of the stats it does not have, the egg card shows the one number it does: for an egg
the friendship byte is the count of cycles still to run, one taken off every 256 steps, so the
card says how far there is left to walk.

The strength panel says what it left out ("5 of 6 able to battle; one slot holds an egg")
because a player looking at six occupied slots and a five-Pokémon score deserves to know which
of the two the app believes.

The CLI keeps printing everything, egg included. It is the diagnostic surface of D-026, its
audience is whoever is debugging a capture, and hiding data there would remove the one place
the raw truth can be checked.

**Alternatives considered:** count the egg but weight it down (rejected: a fraction of a
resistance is not a thing that exists in a battle; either it can switch in or it cannot), show
the species with a spoiler warning (rejected: the app's job is to reflect the game, and the
game hides it), and drop eggs from `Members` entirely so they never reach the window (rejected:
the slot is occupied, and a party that shows five tiles when the game shows six looks like the
bug this fixed rather than the fix).

---

## D-037. The next three levels beat the perfect level 100

**Status:** Accepted · 2026-08-12

The app could say what a Pokémon's best possible moveset is (D-031, D-032) and could not say
what it learns tomorrow. Those are not the same question, and for anyone playing through a
story the second one is the one being asked: whether to walk a bit more before the Gym,
whether to keep a weak move for two more levels, whether the Treecko is about to stop being a
Treecko.

It cost almost nothing. The per-game learn source of D-027 already knows the level of every
level-up move, so the moves half is a filter. The evolution half needed a new source, kept
behind `IEvolutionSource` in GameData for the same reason as the learnsets: the data is
PKHeX's, and Analysis must not depend on PKHeX (D-007).

**Where the honesty boundary falls.** A Treecko at Lv.10 learns Absorb at 6, Quick Attack at
11 and Pursuit at 16, and its table also lists Agility at 23. But at 16 it becomes a Grovyle
and follows Grovyle's learnset from then on. Listing Agility would be a false promise with a
precise number attached, which is worse than saying nothing (D-025). So the list stops at the
evolution level and says why it stopped.

The case that made the model honest was found by a test rather than by reasoning. A Treecko
still a Treecko at Lv.30 has cancelled the evolution at every level since 16, and the game
offers it again at 31, so its future is one level long, and the first version of this happily
listed its Lv.35 move. `EvolvesOnNextLevelUp` exists for that Pokémon.

**What a level means, per trigger.** The evolution table's Level column is only trustworthy
for some triggers, and its Argument column means a different thing for each: an item index for
UseItem and TradeHeldItem, a beauty value for Feebas, and a copy of the level everywhere else.
A level is recorded only where reaching it is genuinely sufficient: plain level ups, the
Tyrogue stat split, the Wurmple personality fork, and Ninjask. It is deliberately absent for
Shedinja (Lv.20 is when it can appear, not when it will), and for stones, trades, friendship
and beauty. Kadabra levels to 100 and stays a Kadabra; a card that counts down to a level it
will never honour is worse than one that says "when traded".

The trigger is grouped by what the player has to do rather than by the game's internal method,
because LevelUpFriendshipMorning and LevelUpFriendshipNight are one decision to a player and
two rows to a ROM.

**Alternatives considered:** show the whole remaining learnset (rejected: it is the wrong
species' learnset past the evolution, and a scrolling list is not a decision), reach through
the evolution and show what the evolved form learns (rejected: it is a different Pokémon with
different moves at different levels, and merging the two is how a player ends up waiting for a
move that never comes; worth revisiting as a separate "after it evolves" section), and put this
in the recommendation engine (rejected: it is a fact about a Pokémon, not a judgement about a
build, and it must show for a game with no reference data).

---

## D-038. Four small window decisions, taken together

**Status:** Accepted · 2026-08-12

Issues #12 to #15 were all about the same surface, so they were done as one pass.

**The whole candidate pool is shown, not the four that won (#12).** The console printed every
candidate with how it is obtained and whether it needs checking; the window showed the four
chosen moves and the bare names of the rest. A player judges a recommendation by what it
turned down: a set that skips Thunderbolt reads very differently once you can see Thunderbolt
was on the table and comes from a TM. The pool now lists every candidate with its type, its
source and its availability, with the four that made the build ticked and fully lit while the
rest stay legible but dimmer.

What is still missing is a stated reason for each rejection. The engine ranks candidates and
keeps the top four; it does not record a sentence per loser. Inventing one after the fact would
be a plausible story rather than the real cause, so the pool shows the facts it has.

**Immunities are their own list in the team panel (#13).** The per-Pokémon view already
separated them; the team view said "Resisted or immune". Both readings were defensible, but the
two views of the same fact disagreed. A type with an immunity is now filed under Immune even
when others merely resist, because the immunity is the better switch-in, and the detail counts
both. The weakness line splits them too: "2 weak, 1 immune, 1 resist" says something "2 safe"
does not.

**Empty slots say the same thing in a tenth of the space (#15).** Five placeholders at 72 px
each filled the strip for a party of one, repeating "empty" five times. One bordered line with
a small marker per slot and "5 slots free" carries the same information without implying the
free slots are as interesting as the Pokémon.

**The window remembers where it was, and which profile was chosen (#14).** Size, position and
profile are stored in `settings.json`, beside the Lua script in the folder of D-029 that
survives updates and is never translocated. Three details are deliberate: a stored size below
the minimum is ignored, because a window saved at 40×20 by a crash reopens with no way out of
it; a maximised window stores the pre-maximise bounds and a flag, because saving the screen
bounds leaves no size to un-maximise back to; and every read and write failure ends in defaults
rather than a dialog, because a settings file is not worth a crash on startup.

The profile is restored because it is a statement about how the player wants to be advised, not
about one session.

**Alternatives considered:** make the candidate pool collapsible behind another toggle
(rejected: it already sits behind the best-set toggle, and a second one would hide the honest
part twice), keep one "safe" list and only fix the wording (rejected: it leaves the two views
phrased differently for no reason), drop the empty slots entirely (rejected: a party of one is
worth seeing as a party of one), and remember the layout in the OS-native way per platform
(rejected: three implementations of a file with five numbers in it).

---

## D-039. melonDS answers, but one frame at a time

**Status:** Accepted · 2026-08-12 · corrected 2026-08-13, see below

Gen 4 and 5 are Nintendo DS games, so D-001's emulator cannot run them. melonDS can, ships a
universal macOS build, and offers a GDB stub where mGBA offers Lua. Everything below was
measured against melonDS 1.1 running a real Pokémon Black, because the source told a different
story from the machine in three separate places.

**The JIT is not a price.** The run loop picks the JIT over the GDB stub, so reading memory
means running the interpreter, which sounded like the cost of the whole approach. It is not:
`JIT.Enable` has no entry in melonDS's defaults table, so a fresh install already runs the
interpreter, and on macOS Intel the JIT is not even compiled in. Measured on an Apple Silicon
Mac: 180 FPS uncapped against a 60 FPS target, and a steady 60 while being hammered with
back-to-back reads.

**Two protocol details that the source states wrongly.** A client must send a bare `+` as its
first byte: melonDS waits one second for it and swallows whatever arrives first, so a client
that opens with a packet loses its `$` to the handshake and gets hung up on. And the documented
read limit of 576 bytes is not real: 576 bytes become 1152 hex characters, which no longer
leave room for framing in a response buffer of exactly 1152. Reads near the limit time out. 256
bytes works.

**The ceiling is one request per frame.** Each read takes about 16 ms, which is 1/60th of a
second on a 60 FPS target, and the measured throughput is 60 reads/s: the stub is polled once
per frame, so no amount of pipelining goes faster.

| | mGBA + Lua (D-002) | melonDS + GDB stub |
| --- | --- | --- |
| party read | pushed on change, ~600 B | 1408 B in 6 requests, **95 ms** |
| throughput | ~350 KB in one reply | **15 KB/s** |
| cost while polling | none observed | none observed, 60 FPS held |

95 ms per party is comfortable at one or two polls a second. The 15 KB/s ceiling is not
comfortable for everything: sprites read from the cartridge (D-033) take about 350 KB in Gen 3,
which would be 23 seconds here. Gen 4 and 5 need a different answer for sprites, and that is a
difference in what the app can do per generation rather than a detail of the provider.

Two behaviours the provider must own. Connecting halts the emulated CPU until a continue
arrives, and dropping the socket without detaching leaves the game frozen for whoever is
playing, which happened repeatedly while measuring this. The provider connects once and keeps
the connection, rather than opening one per read.

**Alternatives considered:** wait for melonDS's Lua support (rejected: the pull request has
been open since April 2023, and the API in the fork that develops it has no memory read at all,
so it would not help even if it landed), BizHawk with the melonDS core (rejected: its own
README says Apple Silicon is not available, which is D-001 again, three years on), and running
DS games under a 3DS emulator (rejected: a 3DS switches to separate DS hardware rather than
emulating it, and Azahar's loader accepts no `.nds` file).

### Correction, 2026-08-13: the ARM9 stub is not broken

This entry originally recorded that the ARM9 stub on port 3333 completes the handshake and then
closes the connection on the first command, whatever the command is, and that only the ARM7
stub on 3334 could be used. That was wrong, and it was wrong for the same reason D-035 was: one
sample, and the wrong explanation picked for it.

A stub whose previous client died without sending `D` stays wedged until melonDS restarts, and
a malformed client of ours had wedged 3333 before the measurements were taken. Both stubs work.
The reader now tries 3334 and then 3333, connecting and performing a 16-byte probe read at
`0x02000000` before accepting a port, because a wedged stub still completes the handshake and
only fails on the first real command.

---

## D-040. Pokémon Black, found the way Ruby was found

**Status:** Accepted · 2026-08-12

The first Gen 5 game read end to end, and the method is the one D-035 left behind: dump the
memory, slide a parser across every four-byte offset, and believe whatever survives a checksum.
4 MB of main RAM came out of melonDS in 273 seconds with no lost blocks, and five records passed
PKHeX's `PK5` validation. Four were noise: a 16-bit checksum over a million offsets produces
coincidences, and a level 100 Wurmple with a nickname in Chinese characters is one. The fifth
was the Snivy that was actually in the party, level 6, HP 17/22, nickname and all.

The finder was tested before it was trusted: a `PK5` we built ourselves, encrypted the way the
game writes it, was hidden in random bytes and had to be found. Without that, "nothing found"
would have meant either "no Pokémon" or "broken tool", with no way to tell.

**The layout.** A party is a header and six fixed slots, with `PK5`'s 220-byte party form:

```
head + 0   capacity, always 6
head + 4   how many are carried
head + 8   slot 0, then 220 bytes each
```

**The address is reached through a pointer, not hardcoded.** The head sits at `0x022348AC`,
and exactly one word in all of main RAM points at it: `0x0224F88C`, the third entry in a table
of eighteen pointers that is plainly the game's own directory of save blocks. The duplicate
copy of the party found at `0x02268494` is pointed at by nothing, which is what made it
identifiable as an orphan rather than an alternative.

Both survived a ROM restart unchanged, so this game's heap is laid out deterministically and a
hardcoded address would work today. The pointer is still what gets followed, for the same
reason as D-005: it is the route the game itself takes, it costs one extra read of four bytes,
and it is the only one of the two that can still be right if the allocation ever differs. What
is read is then checked, capacity 6 and count at most 6, so a pointer that goes somewhere wrong
fails loudly rather than producing a party out of noise (D-008).

**The game says who it is, as in Gen 3.** The DS firmware copies the cartridge header into main
RAM at `0x027FFE00`, which mirrors to `0x023FFE00` in the 4 MB a DS actually has. Title at +0,
four-character game code at +0x0C, revision at +0x1E. This cartridge reads `POKEMON B` /
`IRBI`, Italian Black, which is the same identification mechanism as D-005 at a different
address.

**What is not known.** One ROM, one language. D-035 exists because a single sample supported two
explanations and the wrong one was picked, so nothing here is generalised to White, to Black 2
and White 2, or to any other language: `0x0224F88C` is recorded as the entry for `IRBI` and for
nothing else. The next Gen 5 cartridge is what turns one address into a rule or into a table.

**Alternatives considered:** hardcode the party address (rejected: it works today and says
nothing about tomorrow, and the pointer costs one frame), search memory at run time for
something that parses (rejected for the third time; D-005, D-019 and D-035 all exist because
data that merely looks valid is how a wrong answer becomes a confident one), and trust the
community's published Black/White addresses (rejected: they are for other regions, and this
cartridge is Italian, which is exactly the case that moved the party in D-035).

---

## D-041. Gen 5 rules, and the one line that is not a copy

**Status:** Accepted · 2026-08-12

Gen 5 battle rules exist now, and most of them are the Gen 3 ones: the type chart did not
change between the second generation and the fifth, the twenty-five natures did not change, and
the stat formula did not change. What did change is the thing that decides every recommendation
the app makes.

**The physical/special split moved from the type to the move.** In Gen 3, Bite was a special
attack because Dark was a special type; from Gen 4 onwards Bite is physical because Bite is.
Copying `Gen3Rules` and swapping the number would have sent every Dark and Ghost attacker off
the wrong stat, and with it the role, the nature and the EV spread recommended for it, with no
visible symptom, since the output would still look like advice. `Gen5Rules` reads a category per
move, and a test asserts that the two generations disagree about Bite and agree about
Earthquake.

**Base power is per generation as well.** Tackle is 35 in Emerald, 50 in Black, and 40 today.
Showdown stores the current generation's numbers and walks backwards through per-generation
mods, so reading `data/moves.ts` alone gives Gen 9 values wearing Gen 5 names. The importer
applies gen8, gen7, gen6 and gen5 overrides in that order; it was Tackle that proved the chain
works, arriving at 50 through the gen6 mod because gen5 does not mention it.

**What is duplicated on purpose.** The type chart and the nature table ship as one file per
generation even though the two are byte-identical, so a number can always be traced to the
generation it belongs to instead of borrowing another's. A test compares the two charts entry by
entry, which turns the duplication into a checked fact. What is not duplicated is code: the stat
formula and the nature reader are shared, because two copies of a calculation that drift apart
are a bug in one of them.

**Found by running it.** The console crashed on the first real Gen 5 party, because the team
analyser walks all four move slots and an empty slot carries no type: `Gen5Rules` validated the
type before the move id, where Gen 3 does the opposite. The tests all passed at the time. A
generation is not supported because its rules compile; it is supported when a party from a real
cartridge goes through the whole chain, which this one now does: Snivy, Grass, five unanswered
weaknesses, 32 out of 100.

**What Gen 5 still does not have.** No reference presets and no learn source, so the competitive
profile and the "Coming up" card have nothing to work from and say so rather than guessing.
Recommendations are Gen 3 only until a Gen 5 preset catalog exists.

**Alternatives considered:** derive the category from the type as Gen 3 does and accept the
error for Dark and Ghost (rejected: it is wrong for roughly half the movepool and invisible in
the output), take move data from PKHeX (rejected: it carries move type and PP but neither base
power nor category, so it cannot answer the question), and share one type chart file between the
generations (rejected: it saves a kilobyte and costs the ability to say which generation a
multiplier belongs to; the duplication is checked by a test instead).

---

## D-042. Both emulators are watched at once, and nobody is asked which

**Status:** Accepted · 2026-08-12

With two emulators supported the obvious move is a menu: pick mGBA or melonDS. That question has
already been answered by whoever opened one of them, and the promise the app was built on is
that you open it and it works.

So `LiveTeamService` runs both pipelines side by side and adopts whichever produces a party.
Nothing is asked, nothing is remembered, and swapping from a GBA game to a DS one mid-session
needs no announcement: the most recent party wins, so the next poll simply arrives from the
other emulator and the window follows it.

Three details that only matter once both are running:

**The memory reader follows the active emulator.** Sprites are fetched from the machine the team
came from, not from whichever provider happens to be constructed first.

**Only the best connection state is reported.** Otherwise the emulator that is not running keeps
announcing that it is reconnecting, over the top of the one that is working. Before either has
answered they both report, which is harmless, since they are both connecting.

**Sprites stay a Gen 3 feature.** `RomSpriteSource` reads a GBA cartridge at GBA addresses
(D-033), so it is skipped entirely for a DS game. Without that guard it would hunt for tables
that are not there, down a channel that moves 15 KB a second (D-039), and spend half a minute
finding nothing.

The setup screen carries both sets of instructions under their own headings, because the two
could not be less alike: mGBA needs a Lua script found and loaded by hand, while melonDS needs a
checkbox ticked and the JIT left off, and no file at all. Telling a Black player to load
`ups_bridge.lua` would be sending them looking for something that cannot help them.

Verified against the real composition with mGBA closed and melonDS running: `Connecting`,
`Connecting`, `Streaming`, then "connected via melonDS", POKEMON B [IRBI], Snivy Lv.6.

**Alternatives considered:** a dropdown in the UI (rejected: it asks a question the user has
already answered by opening an emulator), remembering the last emulator used and trying it first
(rejected: it saves nothing, since both are already tried at once, and it fails on the day
somebody switches), and picking by the ROM the app is told about (rejected: the app is not told
about a ROM, it finds one, which is the point of D-005).

---

## D-043. Generations dispatch themselves

**Status:** Accepted · 2026-08-12

Gen 5 learnsets and evolutions now come from PKHeX the same way Gen 3's do, and adding them
raised a question that had not existed with one generation: who decides which tables answer.

`IMoveLearnSource.Supports(game)` and `IEvolutionSource.Supports(game)` already existed, so the
answer is the sources' own. `CompositeMoveLearnSource` and `CompositeEvolutionSource` hold
several and ask each in turn; `PKHeXSources` lists them in one place. Adding Gen 4 will mean
writing its source and adding one line, with nothing above it changed. The alternative, a switch
on the generation somewhere in Analysis, is a second place to update and the one people forget.

What the composite must never do is answer for the wrong generation, and the numbers are close
enough that a mix-up would look plausible: Treecko evolves at 16 in Emerald and Snivy at 17 in
Black. A test asks both and checks both answers.

Gen 5 also brings evolutions that no amount of levelling reaches. Karrablast becomes Escavalier
only by being traded for a Shelmet, so the card names the requirement and refuses to count down
to a level that will never arrive, which is the honesty boundary of D-037 meeting the first
generation that really tests it.

**What this does not fix.** Recommendations are still Gen 3 only. The engine needs a reference
preset catalog as well as a learn source, and there is no Gen 5 one: the window now says so on
screen rather than showing an empty card. Learnsets were the cheaper half and they were worth
having first, because knowing that a Snivy learns Vine Whip next level is useful to someone
playing tonight in a way that an optimal level 100 build is not.

**Alternatives considered:** one source per generation resolved by a registry keyed on
`PokemonGeneration` (rejected: a game code, not a generation, is what identifies tables, since
Black and Black 2 are both Gen 5 and teach differently), and giving the analysers a list of
sources to try (rejected: it puts the dispatch in every caller instead of in one place).

---

## D-044. Recommendations pick their reference data by generation

**Status:** Accepted · 2026-08-12

Gen 5 gets recommendations. The engine used to hold one preset catalog and one move catalog and
refuse anything that did not match them; it now holds them keyed by generation and looks up the
party's, in the same way it already keyed the two profiles by kind. A catalog knows which
generation it is for, so nothing else has to be told, and Gen 4 will be two more entries.

The data is Showdown's Gen 5 Random Battle sets from the commit already pinned for Gen 3, 388
species and 606 sets, and it carries exactly the standing D-024 gave the Gen 3 ones:
expert-authored examples of what a species can do, not standard competitive play and not a claim
about this save.

The Gen 5 tables answering rather than the Gen 3 ones is checked by something only they have:
Grass Pledge appears among a Snivy's tutor candidates, and there is no such move in Emerald.

**A test had to be rewritten rather than kept.** An hour earlier the dashboard asserted that a
Gen 5 party gets no build and is told so on screen. That was true when it was written and is now
false, so it was replaced by the opposite assertion rather than worked around. A test that
describes a limitation has a shelf life; what it was really guarding, that the window and the
analysis agree about what exists, is what the replacement checks.

**Alternatives considered:** keep one catalog and construct a second engine for Gen 5 (rejected:
two engines mean two places for a policy change to be forgotten), and resolve catalogs from the
game code rather than the generation (rejected: unlike learnsets, reference sets do not differ
between Black and Black 2, and inventing a distinction the data does not make is how a table
grows entries nobody can justify).

---

## D-045. The artwork comes from the player, animated, and we ship none of it

**Status:** Accepted · 2026-08-12

D-033 reads sprites out of the player's own cartridge, which works because a Game Boy Advance
maps its ROM into memory. A Nintendo DS does not: the cartridge is read in blocks through a
register interface, so there is no address to point at, and what the emulator will answer for
moves at 15 KB/s (D-039). The Gen 5 dashboard therefore had no picture in it.

Decoding the sprites out of the DS ROM file was attempted and got most of the way. The
filesystem, the `a/0/0/4` archive, LZ11 decompression and the NCGR headers all read correctly at
96×96 and 4bpp, and then it stopped at the pixel layout, which is neither of the two obvious
conventions. That work is not wasted but it is not finished either, and it would only ever have
solved Gen 5.

**What was done instead keeps D-033's principle and covers everything.** The player supplies a
folder of sprites; the app reads it. One Black and White style set covers Gen 1 through Gen 5,
so an Emerald team and a Black team look like they belong to the same app. The cartridge stays
the fallback for a GBA game, so nothing that worked before stopped working.

**No Pokémon artwork enters this repository or any release.** It belongs to Nintendo, Game Freak
and Creatures, and the collections that gather it, Project Pokémon's and PokeAPI's, state no
licence of their own. `tools/fetch-sprites.py` downloads it to the player's disk on their own
machine, because the alternative was clicking a thousand times; the tool contains no art and the
app ships none. The test fixtures are a hand-made two-frame GIF, red then blue, for the same
reason.

**Animated, at the file's own speed.** Avalonia draws a GIF's first frame and stops, so the
frames are taken apart with SkiaSharp, which is already inside Avalonia and therefore no new
dependency, and played at the durations the file declares rather than at one invented rate,
which would make every sprite move alike and some of them wrong. Frames compose cumulatively,
because a GIF frame stores only what changed. A still image starts no timer at all, and closing
the window cancels the ones that are running.

**Size was measured rather than argued about.** 649 fronts are 27 MB and the shinies another 27.
Animated WebP was tried and saves 6%: a small palette animation is precisely what GIF is good
at, so recompression is not the lever. The levers are fetching fewer files, so shinies are now
opt-in, with a missing shiny falling back to the ordinary sprite and the tile marking it with a
star regardless, and `--up-to 386` fetches Gen 1 to 3 for 13 MB. None of this touches the 47 MB
download, which carries no sprites at all.

**Alternatives considered:** bundle the sprites (rejected: it is the one thing the project has
been careful never to do, and no licence permits it), fetch them from inside the app on demand
(rejected: it would put a network call in an app that works offline, and the saving is a few
hundred kilobytes against a one-off 27 MB), finish the NARC decoder first (rejected: weeks of
format archaeology for one generation, when a folder solves five), and ship static PNGs instead
of animations (rejected: they are the same size, and the animation is the reason to have them).

---

## D-046. The sprites download from a button, not a terminal

**Status:** Accepted · 2026-08-12

D-045 put the artwork on the player's disk and left them a Python script to put it there. That
works for anyone with a clone of the repository and works for nobody else: a release carries the
executable and no `tools` folder, so there was no script to run, and running it would have needed
Python and a terminal.

So the app fetches them itself, from a button, with a progress bar, into the folder it already
reads. The script stays for people working on the repository.

This is the app's only network call. It happens because somebody pressed it, nothing waits on it,
and an app with no network behaves exactly as before: coloured tiles, and every other feature
identical. That is not in tension with D-024's "no runtime network call": that rule exists so
recommendations stay deterministic and reproducible offline, not because reaching the network is
forbidden. A download the player asked for is not a hidden dependency.

Two details decide whether a 27 MB download is pleasant or infuriating. Each file is written
beside its destination and moved into place, so an interrupted download never leaves half a
sprite that the next attempt would count as finished. And a second run skips what is already
there rather than re-fetching 27 MB to discover it has it, so resuming costs only what is
missing.

**Alternatives considered:** ship the script in the release (rejected: it still needs Python and
a terminal, and most people have neither), bundle the sprites (rejected for the reason D-045
gives, which has not changed), and fetch each sprite the first time it is needed (rejected: it
would make every party update a potential network call, and the saving is a few hundred
kilobytes against a one-off 27 MB).

---

## D-047. The window is the design's own frame

**Status:** Accepted · 2026-08-12

The dashboard is being rebuilt to a supplied design: a comic Pokédex in thick ink outlines, flat
offset shadows and tilted sticker badges. This entry records the decisions the shell forced; the
screens follow it one at a time.

**The tokens live in one file.** `Theme.axaml` holds every colour, font, shadow and the halftone
brush; `Styles.axaml` holds the shapes the design repeats: panel, card, chip, badge, tab, lamp.
Nothing else writes a hex. The whole look rests on one ink colour being exactly the same in every
border and every shadow, and a colour that appears in two files drifts in one of them.

**The title bar replaces the system one rather than joining it.** Drawn first, it produced two
title bars stacked on top of each other. With `ExtendClientAreaToDecorationsHint` the red bar is
the window's title bar, so its three lamps had to stop being a drawing of window buttons and
become the buttons: close, minimise and zoom, left to right, where a Mac user's hand already
goes. The bar drags the window, because the thing that used to do that is gone.

Extending the client area was the wrong tool: on macOS the system still draws its traffic lights,
so the design's lamps landed on top of them and there were six buttons where there should be
three. Avalonia 12 renamed `SystemDecorations` to `WindowDecorations`, and `BorderOnly` is the
right setting: the frame and its resize edges stay, the system title bar and its buttons do not.
It behaves the same on Windows and Linux, so one bar serves all three rather than macOS getting a
special case.

**Fonts are the one new dependency**, and the only files here somebody else wrote: Bungee, Nunito
and DM Mono, under the Open Font License, which permits shipping them. 484 KB, listed in
`THIRD_PARTY_NOTICES.md`.

**Three places where the design predates the code**, decided with the author rather than
invented: the Bridge screen carries two columns of steps, because melonDS has no script to load
and the design knew only mGBA; the sprite download, which does not exist in the design at all,
sits at the foot of the rail while the folder is empty; and the live pill reads `LIVE` alone,
because "4 reads/s" is a number the app does not measure.

A real counter was built later and removed again: it sampled the tracker's diagnostics, and since
the DS provider only emits when the party changes, it read `LIVE · 0 reads/s` while a party was
arriving perfectly well. A number that is usually zero says the bridge is broken when it is not,
so the pill is a word and the measurement is gone rather than left computing for nobody.

**Alternatives considered:** port the design's HTML into a web view (rejected by the brief, and
rightly: the app would gain a browser to draw six panels), and keep the native title bar and drop
the design's (rejected: the bar is the first thing the design establishes, and the lamps work).

---

## D-048. The floor is not a base stat

**Status:** Accepted · 2026-08-13

The Stats screen breaks each stat into where it came from, and the HP bar was full for every
Pokémon, always. Two things caused it, and only one is a bug.

Every stat formula has a constant: `+5` for most stats, `+level+10` for HP. It was being folded
into the base-stat segment, so a level 6 Snivy's HP read as twenty-two points of "base" when
sixteen of them are the floor any Pokémon of that level receives. That is the opposite of what
the panel is for, so the floor is now its own segment, dimmer than the rest, with its own legend
entry: "floor (every Pokémon gets it)".

The second cause is not a defect. Bars are scaled against the largest final stat on screen, which
is the design's own rule, and HP is nearly always that stat, so the HP row fills the width by
construction. Splitting the floor out is what makes a full bar informative again: it shows how
little of that width the species actually earned.

This adds one colour to a legend the design fixed at four, which is a deviation, asked for by
name after the bar was seen against a real party.

**Alternatives considered:** scale each row against its own final value (rejected: the rows stop
being comparable, which is the only reason to draw them together), and leave the floor inside the
base segment and explain it in a note (rejected: the picture would still say something false, and
a note under a wrong picture is not a fix).

---

## D-049. The DS instructions existed and nobody could see them

**Status:** Accepted · 2026-08-13

D-042 put both emulators' setup steps on the screen shown before a bridge answers. The redesign
then gave the disconnected state its own full-window Bridge screen, drawn over everything else,
and that screen only knew about mGBA. The melonDS steps were still in the code, still bound,
still built on every launch, and unreachable: covered when disconnected, hidden when connected.
Someone playing Black was told to open a scripting window melonDS does not have and load a file
that cannot help them.

The Bridge screen now carries the two columns the decision called for: mGBA with its script path,
Reveal and Copy path, and melonDS with its checkbox and its warning about the JIT, each numbered,
each under its own tilted heading. The unreachable markup is gone rather than left to rot: 73
lines that compiled, bound and rendered nothing.

The lesson is not about melonDS. A screen that is covered rather than deleted still passes every
build, and the test that guarded these steps checked the view model, which was faithfully
producing text nobody could read. What was missing was looking at the running app on the machine
it was written for, which is how it was found.

**Alternatives considered:** show the steps for the active emulator only (rejected: when nothing
is connected there is no active emulator, which is exactly when the steps are needed), and keep
the old setup screen and show it before the Bridge one (rejected: two screens explaining the same
thing, and the design gives that job to the Bridge).

---

## D-050. The brief is not part of the product

**Status:** Accepted · 2026-08-13

`design_handoff_pokedex_ui/` held the brief for the redesign: a prompt, a rendered HTML mock and
the script that drove it. It was scaffolding, read once and worked from. Keeping it in the tree
left the repository with two descriptions of how the app looks, and the one that is not compiled
is the one that goes stale. Somebody reading the HTML a year from now would be reading a
proposal, not the product, with nothing on the page saying so.

The design now lives where it is enforced: the tokens in `Theme.axaml`, the shapes in
`Styles.axaml`, and the screens themselves. Those cannot drift from the app, because they are the
app. The 888 KB of mock, and the duplicate copy of the icon inside it, are gone.

Local editor and tool state is now ignored rather than merely untracked, so it cannot be
committed by an absent-minded `git add -A`.

**Alternatives considered:** move the mock under `docs/` (rejected: the same stale second
description, filed more tidily), and keep it and mark it historical (rejected: a folder nobody
may trust is a folder nobody should have to read past).

---

## D-051. Rank every legal move, then cap what the screen shows

**Status:** Accepted · 2026-08-13

The real Lv.6 Snivy exposed a bad boundary. The engine cut the candidate list before choosing
the build, in learn-source order, so weak level-up moves filled the budget before better
machines and tutors were scored. The cap is now presentation only: the engine ranks the
complete legal pool and chooses the build from it, then shows a bounded shortlist that always
includes the four winners.

A status move is not useful merely because it deals no damage. The metadata available here
cannot distinguish Leer from recovery, setup or disruption, so utility is trusted only when
one of the species' reference presets names it. Without that evidence, four attacks are a more
honest result than forcing a weak status move into the fourth slot. Species without a preset
may therefore receive no utility move.

Moves such as Seismic Toss deal real damage but provide no type coverage. They count as attacks
and get a direct-damage label rather than being described as utility; they cannot claim STAB,
close a matchup gap or reserve the status slot merely because their damage ignores the chart.

Playthrough advice also looks a few levels ahead. A nearby level-up move may beat disposable
filler, and is labelled as arriving through levelling rather than as something to find in the
save. The horizon is capped at Lv.100 and at the current species' evolution boundary, including
moves learned on the evolution level and treating an overdue level evolution as happening on
the next level-up. Machines and tutors remain candidates because their availability is not
tied to level.

This narrows D-038: analysis still considers the complete pool, but the screen is a
recommendation view, not an exhaustive legality browser.

**Alternatives considered:** raise the cap while keeping source-order truncation (rejected:
the same bug with a larger number), force one arbitrary status move (rejected: this produced
Leer), maintain a hand-written utility whitelist (rejected: incomplete and brittle), and use
only reference sets (rejected: many unevolved or uncommon species have none, especially during
a playthrough).

---

## D-052. Persistent training targets the final form; playthrough moves stay legal now

**Status:** Accepted · 2026-08-13

Nature and effort values survive evolution, and competitive advice already assumes a fully
trained Pokémon. When an evolution line has one unambiguous destination at every step, role,
nature, effort values and competitive moves therefore target its final species. A Snivy is
judged with Serperior's base stats, and competitive presets and learnsets are resolved for
Serperior rather than pretending that Tackle describes the finished Pokémon.

Playthrough moves follow a different boundary. They remain legal for the species currently in
the party, plus the short upcoming horizon from D-051. The engine must not merge an evolved
form's learnset into the current one or recommend a move the player cannot teach yet. Both
profiles can therefore share a long-term role, nature and EV target while answering different
moveset questions.

The target species, base stats and typing are carried explicitly through the analysis, so speed
thresholds, defensive comparisons, projected stats and competitive same-type move bonuses all
use the same target. Playthrough move bonuses still use the current species' typing. The UI and
CLI name the long-term target whenever it differs from the live species. Current moves remain
weak evidence about how the player is using the Pokémon, but they do not outweigh a clear
base-stat bias.

A branching line has no inferred target. Eevee, Wurmple and Tyrogue fall back to the current
species because choosing a branch would invent the player's plan. Missing evolution or
base-stat data does the same. A unique trade or stone evolution may still be the competitive
target, but playthrough output must not claim that the evolution will happen. Multiple methods
that reach the same species are not a branch (Feebas still targets Milotic), and an evolution
that creates a second Pokémon without replacing the first is ignored as a destination (Nincada
targets Ninjask, not Shedinja). Gender-limited routes are followed only when the decoded party
member is eligible; a male Combee is not silently planned as Vespiquen.

**Alternatives considered:** always judge the current species (rejected: capture-time nature
and accumulated EV advice becomes obsolete on evolution), use the final species for both
movesets (rejected: it produces currently illegal playthrough moves), merge every learnset in
the line (rejected: move levels and legality become false), and select the first or strongest
branch (rejected: table order and raw stats are not player intent).

**Known limitation:** team-wide competitive gap weighting still starts from the live party's
types and current moves. A species whose final form changes type is scored correctly for its own
STAB, but the surrounding team's defensive gaps are not yet rebuilt as six projected final
forms, and a current move later dropped from the competitive build may initially count as an
answer. Fixing that requires a separate two-pass competitive team model rather than another
single-Pokémon scoring exception.

---

## D-053. Team hints stop at the player's actual frontier

**Status:** Accepted · 2026-08-14

The Team screen diagnoses what is already carried; the separate Team hints screen proposes up
to three actionable ways to change it. Every proposal is filtered through an explicit story
milestone before it is scored. Party level is never treated as progress: a trained starter does
not prove that a late route is open, and a low-level catch does not prove that the player is
still near the beginning. Pokémon from the next milestone may be previewed in a clearly separate
section, but never enter an "available now" plan.

Pokémon Black is the first mapped game. Its encounter facts are checked against PKHeX's pinned
per-version data and stored as an immutable local snapshot, while route order and field
prerequisites are curated as product data. Runtime queries against PKHeX's shared encounter
generator were rejected because parallel analyses can race its internal buffers and make app
startup unreliable. Wild
grass, caves, phenomena, Surf, bridge shadows, fossils, roaming Pokémon and in-game trades are
eligible; gifts are not. Mutually exclusive acquisitions carry a shared choice id, so one plan
cannot recommend both Relic Castle fossils. A full party receives an explicit replacement rather
than a seventh member.

An encounter whose current condition cannot be proven (for example Shelmet during winter) stays
out of "available now" plans. One-time/static catches, fossils, trades and roamers remain eligible
but are labelled as limited because RAM support does not yet read whether that opportunity was
already consumed. The first verified acquisition per species is used in this version; later-area
alternatives and most encounter rates remain future refinements, so late-game advice can favour an
older low-level location while remaining obtainable and story-safe.

Automatic progress is deliberately narrower than encounter support. Only revision-zero Italian Black
(`IRBI`) has verified live offsets: the reader follows the known party/save-block pointer to the
one-byte badge mask and the current map id. Badges unlock only checkpoints they prove have been
passed; the map id is displayed as evidence but does not unlock a route until a map-to-story
table has itself been verified. Zero badges therefore defaults to Route 1, and every exact route
remains selectable manually. Other Black languages use the same encounter timeline with manual
progress, rather than borrowing unmeasured RAM addresses.

Plans are ranked by defensive gaps closed, new super-effective reach, type and role diversity,
catch/training practicality and redundant answers. A full-party replacement must have positive
strategic value before convenience is allowed to rank it. Offensive reach counts at most four
damaging level-up move types that a fresh catch can carry or learn along the projected near-term
evolution line. It does not count a low-level evolved-form reminder move, TM or tutor
merely because the species can learn it: this feature knows where the Pokémon is, not which
single-use machines this save still owns. The displayed factors come from the same pure analyzer
that chose the plan, so the UI formats the answer without silently re-ranking it.

The progress reader lives beside the game-specific PKHeX adapters rather than in the melonDS
provider. The provider continues to expose raw memory only; interpreting a byte as a Unova badge
is game knowledge, not transport knowledge.

**Alternatives considered:** infer progress from party level or species (rejected: both can be
traded or trained out of sequence), unlock every route sharing the detected badge count
(rejected: it exposes areas before the next Gym), recommend all legal machine coverage
(rejected: legality is not ownership), include gifts (rejected by the feature policy), and put
the hints inside Team (rejected: diagnosis and acquisition planning have different inputs and
failure states).

### Amendment, 2026-08-14: the agreement with PKHeX is now a test

The entry above said the encounter facts were checked against PKHeX's pinned per-version
data. They had been, by hand, once. What the catalog's tests actually asserted were the
numbers the catalog itself declares: the catalog says Patrat is on Route 1 at levels 2 to 4,
and the test agreed with it. That proves the shape of the data and nothing about the game,
and it is the kind of check that keeps passing while the data rots.

`BlackEncounterPkHexAgreementTests` compares the curated wild entries against
`Encounters5BW.SlotsB`, PKHeX's own Black table. All 58 wild entries name a species that is
genuinely catchable in Black, and both ends of every printed level range are levels that
species really appears at. Levels *between* the ends are deliberately not asserted: a Black
grass slot lists discrete levels, so Munna appears at 8 and at 10 and never at 9, and a
minimum and a maximum are bounds rather than a promise about everything in between.

The table is reached by reflection, because PKHeX exposes the static encounters and the
trades but not the loaded wild areas. A PKHeX upgrade that renames the field fails this test
loudly rather than silently skipping the comparison, which is the same reason D-014 pins its
type indices with a test instead of trusting them.

What this does not cover: fossils, in-game trades, static catches and roamers are not wild
slots, so PKHeX files them elsewhere and they remain curated and unverified. So does the
story timeline itself, which is product data about route order and has no equivalent in
PKHeX to check against.

### Amendment, 2026-08-14: the live offsets were not verified, and detection is off

The entry above said only revision-zero Italian Black has "verified live offsets". It does
not. Running the app against a real Italian Black showed the map id reading 317 and staying
at 317 across a change of map, which the real `PlayerPosition5.M` cannot do. The badge count
read zero at the same time and proved nothing either way: an address pointing at any zero
byte gives the same answer, so the reading that looked like a success was the one reading
that cannot fail.

The offsets were fixed distances from the party head, `+0x780` for the map and `+0x8404` for
the badge mask. That assumes the save blocks lie contiguously in memory, and D-040 had
already found otherwise: the party is reached through the third entry of an eighteen-pointer
directory, so each block is its own allocation. PKHeX models `Misc5BW` and `PlayerPosition5`
as separate blocks for the same reason. Two independent lines of evidence, and the game
agreed with both.

`IrbiStoryProgressReader.OffsetsVerifiedLive` is now false and the dashboard consults it
before offering automatic detection, so the story point stays on the manual selector the
design already carries. The reader itself is kept whole and still tested: its transport,
bounds checks and refusals are correct, and only the two distances are wrong. Finding the
right ones means walking the pointer directory rather than adding a constant, which needs a
differential dump against a running game: read a window, move, read again, keep what changed.

Three dashboard tests drove the automatic path end to end and were removed with the
behaviour they described, replaced by one that asserts the gate holds and that nothing is
read at all. They are in this file's history for when the offsets are found.

What this cost is smaller than it sounds. Manual selection was always the fallback and every
route stays selectable; what is gone is a convenience that was quietly wrong. What it is
worth is the rule the project keeps relearning, in D-035 and again in D-039: a single reading
that is consistent with two explanations has not confirmed either, and zero is the value most
likely to be a coincidence.

### Amendment, 2026-08-14: the map is found, the badges are not

A differential dump settled the map. Three readings against the running game, outdoors on
Route 1, then inside the starting house, then outdoors again, and the field wanted is the one
that is equal in the two outdoor readings and different in the middle one. Everything else
that changes is a counter, a timer or a work buffer, and there is a lot of it: the filter
found 675 candidates before alignment and a plausible range cut it to two, and only one of
those sat in a block whose whole neighbourhood was identical in both outdoor readings.

The map is the low sixteen bits of a four-byte field `+0x10C` into the block pointed at by
the directory entry at `0x0224F89C`, the seventh, where the party is the third. Route 1 reads
317 and the starting house 390. The high half of that word went from 5 outdoors to 0 indoors
and is not understood, so it is masked off rather than named.

Both values that made the first attempt look plausible turned out to be real fields, which is
why they were convincing. `+0x780` is the map the player last saved at: a genuine map id,
correct for Route 1, and constant while walking around, so it survived every check that did
not involve moving. `+0x776` increments by one and is a counter. A wrong address that holds a
sensible number is worse than one that holds nonsense.

The badge byte is still unfound, and cannot be found by this method while the count is zero,
because every wrong address holding a zero agrees with the right one. It needs a dump either
side of the first Gym. Automatic detection therefore stays off: D-053 gates routes on badges
and treats the map as evidence only, so knowing the map unlocks nothing on its own.

The dumps were taken by attaching, reading, and detaching immediately, rather than by holding
the connection open. Attaching halts the emulated CPU, so a short attach pauses the game for a
second and a long one takes it away from whoever is playing. An earlier attempt in the same
session killed the app with a signal instead of closing its window, so its client never sent
`D`, and the stub had to be cleared by reloading the ROM. D-039 said as much and it was worth
relearning.

### Amendment, 2026-08-14: the coverage a hint promises is potential

A suggestion card said a candidate was "super-effective into" a list of types. It is not, yet.
Those types come from the damaging moves the species can learn along the near-term level-up
line projected for it, not from moves it has: the Patrat waiting in the grass knows Tackle.
The entry above already said as much, and the card said something stronger than the entry.

It now reads "Potentially super-effective into", and the plan still carries its own note that
only realistic level-up attacks count. The defensive half keeps its plain wording, because a
resistance follows from the species' typing and is true the moment it is caught.

A single word, and worth the entry: the whole feature is a list of promises about Pokémon the
player does not own yet, and the difference between what one will be able to do and what it
can do is the difference D-025 exists to hold.

## D-054. A second Gen 5 cartridge, found the same way and filed on its own line

**Status:** Accepted · 2026-08-14

An English Pokémon White was tried and nothing happened: no party, no analysis, no hints.
That was `Gen5MemoryMap` refusing to read, correctly, because it held one entry and that
entry was Italian Black. D-005 working as intended, and the reason issue #24 exists: the
parser accepts all 32 Gen 5 codes and the encounter catalog accepts Black in eight languages,
while the layer that actually reaches memory knew one cartridge.

The address was found with the method of D-040, and faster, because the shape was known.
The cartridge header at `0x023FFE00` read `POKEMON W` / `IRAO`. Black's pointer directory is
not in the same place in White, so a megabyte of main RAM was read in 68 seconds and scanned
for the party's shape: capacity 6, then a count that fits in it. That alone produced hundreds
of matches in a region full of repeated sixes, so the filter gained the thing noise cannot
fake, entropy: a party slot is encrypted, and 150 distinct byte values in 220 is not
something a table of small integers produces. Three candidates survived, and the real parser
decoded all three.

| Address | Read as | Pointed at by |
| --- | --- | --- |
| `0x022349CC` | Tepig Lv.5, HP 19/22, Relaxed | **one word, at `0x0224F9AC`** |
| `0x0225D758` | the same Tepig, same PID | nothing |
| `0x0225DCB8` | Snivy Lv.5, HP 19/19, Bold | nothing |

The duplicate and the orphan are exactly what D-040 saw in Black. Being pointed at is what
separates the party from a copy of it, and the player confirmed carrying the Tepig, which is
what turns a strong inference into a fact.

`["IRAO"] = 0x0224F9AC` is therefore filed beside Black's, and only beside it. The two sit
exactly `0x120` apart in both the pointer and the head it leads to, which is a satisfying
number and not a rule: it is two samples, and D-035 exists because two explanations once fit
one. Italian White and the sequels stay unmapped until somebody runs them.

What this does not give White is team hints, which are keyed to the Black encounter timeline
and stay that way until issue #17 maps White's own.

**Alternatives considered:** derive other codes by adding `0x120` (rejected: that is the
inference D-035 punished, and it would be wrong invisibly), scan at run time for anything
that parses (rejected for the fourth time, D-005, D-019, D-035 and D-040), and read the whole
4 MB rather than a window (rejected: 273 seconds against 68, and the window was chosen from
where Black keeps its heap, which is a guess that costs nothing when it is wrong).

## D-055. One Unova, two versions, and the differences listed where they happen

**Status:** Accepted · 2026-08-14

Team hints knew Black. White shares the region, the route order, the gym order and the
milestones, so a second catalog would have been a copy of the first with six lines changed
and every reason to drift apart. `BlackEncounterCatalog` is therefore now
`UnovaEncounterCatalog`, built once per version, with the differences written at the points
where the two games actually diverge.

The differences were taken from PKHeX rather than from memory, by comparing its two tables:
232 wild species in Black, 265 in White, 216 shared. Six places needed a line:

| Where | Black | White |
| --- | --- | --- |
| Pinwheel Forest interior | Cottonee | Petilil |
| Nacrene City trade | give Cottonee, get Petilil | give Petilil, get Cottonee |
| Route 5 | Gothita | Solosis |
| Route 10 | Vullaby | Rufflet |
| Unova roamer | Tornadus | Thundurus |
| Pinwheel Forest entrance | Sawk in grass, Throh in shaking grass | the other way round |

The last one is the reason the cross-check earns its place. The first five are absolute
exclusives, absent from the other version's tables, and easy to find by comparing species
lists. Throh and Sawk are in both games and only swap *roles*, so the species comparison said
nothing and the catalog quietly kept Black's levels for White. The test caught it: "Sawk
claims 12-15, really 15-50". A wrong level on a real species is exactly the plausible failure
this project keeps meeting.

`UnovaEncounterPkHexAgreementTests` now runs against both versions, each against its own slot
table, and asserts they really do swap: Cottonee is in Black's table and not White's, Petilil
the reverse.

**White Forest is excluded**, and it is the one deliberate hole. 33 of White's extra species
live only there, and which of them are present depends on which residents have migrated into
the forest, which is save state the app cannot read. Suggesting a Bellsprout that may not be
there is the promise D-025 exists to prevent, so White will simply never propose those 33.
The catalog stays honest and slightly poorer.

`IEncounterCatalog` gained `FindConservativeMilestone`, which the dashboard used to reach by
casting to the concrete Black catalog. With two catalogs that cast would have been a bug, so
the question moved onto the interface and a `CompositeEncounterCatalog` dispatches by game,
the way learnsets and evolutions already do (D-043).

**Alternatives considered:** a separate `WhiteEncounterCatalog` (rejected: 95% duplication of
a hand-curated list, and the copy that drifts is the one nobody is checking), deriving White
from Black by a lookup table of swaps (rejected: the Throh and Sawk case is not a swap of
species but of levels and methods, so the table would have needed to express the whole entry
anyway), and including White Forest with a warning (rejected: a warning on a suggestion the
app cannot stand behind is still a suggestion).

### Amendment, 2026-08-14: Italian White, and what three samples say

Italian White (`IRAI`) keeps its party pointer at `0x0224F8AC`, leading to `0x022348CC`.
Verified the same way and in seconds rather than by scanning a megabyte: by then the
directory's neighbourhood was known, so reading `0x0224F700` to `0x0224FC00` and testing every
aligned word for a party-shaped target found exactly one. The parser read a Tepig at Lv.5 and
the player confirmed carrying it.

Three cartridges now, and the useful part is that they disagree:

| Cartridge | Pointer | Party head | From Italian Black |
| --- | --- | --- | --- |
| Black, Italian | `0x0224F88C` | `0x022348AC` | — |
| White, Italian | `0x0224F8AC` | `0x022348CC` | `+0x20` |
| White, English | `0x0224F9AC` | `0x022349CC` | `+0x120` |

Version moves the address and so does language, and neither moves it by an amount the other
predicts. Anyone tempted to derive the remaining codes by arithmetic now has three data points
saying it cannot be done, which is worth more than the two that merely failed to warn.

A test that used `IRAI` as its example of an unmapped game had to move to `IRBO`, because its
subject had become mapped. Worth noting rather than fixing quietly: a test whose fixture is
"something we do not support" ages every time support grows.

Italian White is now complete end to end, since D-055 gave White its own encounter timeline:
party, analysis and team hints all work on that cartridge.

### Amendment, 2026-08-14: English Black, and the four cartridges together

English Black (`IRBO`) keeps its party pointer at `0x0224F98C`, leading to `0x022349AC`. Found
the same fast way and confirmed by an Oshawott at Lv.5. That completes the four cartridges the
Unova encounter catalogs of D-055 answer for, so Black and White now work end to end in both
of the languages they were run in.

| Cartridge | Pointer | Party head | From Italian Black |
| --- | --- | --- | --- |
| Black, Italian | `0x0224F88C` | `0x022348AC` | — |
| Black, English | `0x0224F98C` | `0x022349AC` | `+0x100` |
| White, Italian | `0x0224F8AC` | `0x022348CC` | `+0x20` |
| White, English | `0x0224F9AC` | `0x022349CC` | `+0x120` |

Laid out together the four look like a rule: language moves the party by `0x100` and version
by `0x20`, and `0x120` is simply both. It may well be the rule. It is still not being used.
Four samples of one region, all revision zero, is a pattern and not a law, and the cost of
being wrong is not an error message but a team read out of the wrong bytes. The next cartridge
gets measured, and if the arithmetic predicts it, that is five samples rather than a licence.

The fixture in `AGameThatIsNotMappedProducesNoSnapshot` has now moved twice, from Italian
White to English Black to Japanese Black, because each of its subjects became supported. That
is the shape of a test whose subject is an absence: it needs a new example every time the
thing it stands for shrinks.

### Amendment, 2026-08-14: the sprite progress test, twice wrong

The deterministic rewrite of `ProgressIsReportedAllTheWayToTheEnd` traded one bug for another.
`Progress<T>` posts to the thread pool, so the last report could still be in flight when
`DownloadAsync` returned and the assertions raced it; replacing it with a synchronous
`IProgress` fixed that and introduced a worse fault, because the downloader fetches in
parallel and the reports now arrived on several threads at once into a plain `List`, which
answers that with `ArgumentOutOfRangeException` from inside its own `Grow`.

The first version had picked `ConcurrentBag` for exactly that reason and lost it in the
rewrite. Synchronous delivery into a concurrent collection is the combination that is neither
racy nor broken, and both halves are now written down in the test, because each one rules out
the obvious answer to the other.

## D-056. The app says when it is out of date, and takes no for an answer

**Status:** Accepted · 2026-08-14

Somebody running an old build had no way to learn a new one exists. The app now asks GitHub
once at launch and, when there is a newer release, shows a strip above the tabs with Download
and Not now.

**This breaks a promise D-046 made**, and the break is deliberate rather than overlooked.
D-046 said the sprite download was the app's only network call and that it happened because
somebody pressed it. This one happens on its own. What made that rule worth having was that
the app works offline and never depends on a service to do its job, and that part is kept:
every failure is silence. No network, GitHub refusing, the anonymous rate limit, a draft
release, a body that is not the JSON expected: all of them return null and leave the window
exactly as it was. Nothing waits on the check and nothing fails because of it.

**A build had to learn its own version first.** There was none: no `<Version>` in the project
and nothing passed from the tag, so the binary could not have told a new release from itself.
The release workflow now passes `-p:Version=${GITHUB_REF_NAME#v}` to all four publishes, and
anything built anywhere else keeps `0.0.0`, which the check reads as "not a release" and stays
quiet about. Someone running from source is not someone to nag.

**A strip, not a dialog.** The design has neither, so this was asked rather than invented. A
modal would put a question about versions between a player and their team every time a release
lands, for a program whose whole point is to sit beside a game and answer quickly. The strip
says its piece and can be ignored.

**No is remembered.** Turning it down writes that version into `settings.json`, and the notice
does not return for it; a *later* release is a new question and asks again. A notice that
reappears every launch is one people learn to dismiss without reading, which costs the one
time it matters.

**Alternatives considered:** check only when a button is pressed (rejected: it answers a
question nobody thinks to ask, which is the same as not having it), download and install the
update itself (rejected: silently replacing a binary is a large promise, and this app is not
signed by Apple, so it cannot make it well), compare against the tag list rather than
`releases/latest` (rejected: the latest release is exactly the question, and the tag list
includes tags that were never released), and nag every launch until updated (rejected: the
player is allowed to keep the old one, which is what "and use the old one" meant).

## D-057. Hoenn, where half the catalog can be checked and half cannot

**Status:** Accepted · 2026-08-14

Team hints reach Pokémon Emerald. The catalog is built the way Unova's is, and this entry is
mostly about the half that is different.

**The encounters are PKHeX's.** 312 wild entries across 49 places, taken from
`Encounters3RSE.SlotsE` and emitted rather than typed, so a transcription slip is not
possible. `HoennEncounterPkHexAgreementTests` then checks every species is really catchable in
Emerald and that both ends of every printed level range are levels it really appears at, the
same two questions the Unova check asks.

An earlier pass of that extraction merged the slot types and produced ranges like "Goldeen
Lv.5-30", which is grass, Surf and two rods averaged into a number true of nothing. Keyed by
place *and* type, the ranges are honest.

**The story order is not checkable, and is therefore pessimistic.** Which route opens after
which badge is knowledge of the game with no table to verify it against. Every checkpoint is
anchored to a badge count, which is objective, and wherever the order was uncertain the place
was filed behind the badge that certainly precedes it. Being late costs a suggestion; being
early sends somebody to a route they cannot open, and that failure is invisible because it
looks like ordinary advice. The test asserts the property that makes it safe to be wrong: the
timeline never runs backwards, badge counts never decrease, and no badge count unlocks a
checkpoint needing more.

**Grass and Surf only.** Fishing is 288 of the 600 entries and needs a rod, an item the app
cannot see, and it would have filled the pool with Tentacool and Magikarp. Five places PKHeX
knows are left out with a reason each, in the class remarks: two post-game, one event-only,
one story-locked, and Mirage Tower, which appears and vanishes with the day and so can never
be promised.

**Only Emerald.** Ruby and Sapphire share Hoenn and differ in their tables, and nobody has run
one against this catalog. They read as unmapped, which is the honest answer.

**Third time for one shape of mistake.** `TeamHintsStayUnavailableForAnUnmappedGame` used
Emerald as its example of a game with no timeline, and Emerald now has one. Before that the
melonDS test used Italian White until Italian White was mapped, and then English Black. A test
whose subject is an absence needs a new example every time the absence shrinks, and it is
worth naming as a shape rather than fixing quietly a fourth time.

**Alternatives considered:** wait for a verifiable source for the story order (rejected: there
is no such table, and the alternative to a careful guess here is no feature), badge-only
checkpoints with no route detail (rejected: nine checkpoints across a fifty-place region opens
everything in slabs and stops being advice), include fishing behind an availability check
(rejected: it triples the pool to say "check whether you own a rod" about most of it), and
generate the timeline from the order PKHeX's location ids happen to run in (rejected: that is
map data, not story order, and it would look derived while being a guess).

## D-058. Ruby and Sapphire share Hoenn's story and almost none of its table

**Status:** Accepted · 2026-08-14

Team hints reach all three Hoenn games. The structure is not the one D-055 used for Black and
White, and the reason is worth recording, because the two situations look identical from the
outside and are not.

Black and White differ in **six** encounters, so a handful of conditionals inside one list
says exactly what differs and reads as a list of exceptions. Hoenn does not:

| | Wild entries | Species |
| --- | --- | --- |
| Emerald | 312 | 116 |
| Ruby | 287 | 97 |
| Sapphire | 287 | 97 |

59 entries are Emerald's alone, 34 belong to Ruby and not Emerald, and Ruby and Sapphire
disagree with each other in 38 more. Roughly a third of the catalog is version-specific.
Conditionals at that density stop being exceptions and become noise, and every one of them is
a place to make a mistake by hand.

So the split runs the other way from D-055. What the three share is the curated half: one
region, one story timeline, one map of place to checkpoint. What they do not share is the
generated half, and each gets its own emitted list, straight from `SlotsE`, `SlotsR` and
`SlotsS`. The place-to-checkpoint map is shared safely because the *places* are the same in
all three; only the species inside them move.

`HoennEncounterPkHexAgreementTests` runs against all three, each against its own table, and
asserts that they genuinely differ, so a future refactor cannot quietly serve one list to all
three and still pass.

**The absence test, fourth time.** `TeamHintsStayUnavailableForAnUnmappedGame` needed a game
with no timeline. It used Italian White until that was mapped, then Emerald, then Ruby, and
Ruby lasted about ten minutes. The fix this time is not a fourth example: the test now uses a
game code that is not real. A test whose subject is a shrinking set cannot be written against
a member of it, and the three moves before this one were all the same mistake made politely.

**Alternatives considered:** conditionals per differing entry as in D-055 (rejected: ninety
of them, and the file stops being readable), one list plus a per-version patch table (rejected:
the patch table is the same data in a shape nobody can check against PKHeX by eye), and
treating Emerald as the base with Ruby and Sapphire as subsets (rejected: 34 entries exist in
Ruby and not Emerald, so it is not a subset).

## D-059. Kanto, and the islands a badge count cannot describe

**Status:** Accepted · 2026-08-15

Team hints reach FireRed and LeafGreen. The structure is D-058's: one curated timeline for the
region, one emitted encounter list per version, each checked against its own PKHeX table. It
shares nothing with Hoenn, because Kanto is a different region with a different order of Gyms,
and nothing was reused except the shape.

**Thirty-four of PKHeX's seventy-four Kanto places are not suggested**, and almost all of them
are the Sevii Islands. That is the entry's real content.

The catalog's model is a checkpoint with a badge count: everything it can say is "you have
this many badges, so you have certainly been here". The Sevii Islands do not work that way.
They open on Celio's errands, in a sequence that runs alongside the badges rather than with
them, and the later islands wait for the Hall of Fame. A player with five badges may have all
of the first three islands or none of them, and nothing the app can read tells it which.

The choice was to model them badly or leave them out. Leaving them out costs Kindle Road,
Mt. Ember, Berry Forest and the rest, which is a real loss, and it keeps the promise the
feature is built on: everything it names can be gone and caught now. A test asserts they stay
out, because the cheapest way for them to come back is somebody widening a location map
without noticing what it lets through.

Cerulean Cave and the Tanoby chambers are out for the plainer reason that they are post-game,
and Altering Cave for the same reason it is out of Hoenn.

**Alternatives considered:** file the first three islands behind a late badge (rejected: the
badge is not what opens them, so the app would be right by coincidence and wrong for anyone
who did the errands in a different order), show them in a separate section marked "if you have
reached the Sevii Islands" (rejected: that is the player doing the app's job, and D-053 already
refuses conditional encounters for exactly this reason), and read the story flags from memory
to find out (deferred: it is the same shape of problem as the badge byte in issue #16, and it
should wait until that one is solved on a game that already needs it).

## D-060. Black 2 and White 2, where the checkpoints are only the badges

**Status:** Accepted · 2026-08-15

Team hints reach the sequels, and their catalog is built differently from every other one
here: nine checkpoints, one per Gym, and no route detail at all.

Every other catalog has a route-level timeline because the story order was known well enough
to write one and file the uncertain places late. For these two that was not true. Unova is
rebuilt: the game starts in Aspertia, the Gym order is different, and half the western region
that Black and White open in the first hours is reached near the end. Writing forty
route-level checkpoints from a memory that shaky would have produced something precise and
wrong, which is worse than something coarse and true, because precision is what makes a
suggestion believable.

So each place sits at the badge count by which it is certainly reachable, and anything
uncertain sits at the last checkpoint before the League. A test asserts the checkpoints are
exactly the nine badge counts, so this does not drift back into route detail by accident.

**The cost is real and worth naming.** A stretch of the map opens all at once rather than
route by route, so between two Gyms the screen says the same thing. For a player deciding what
to catch this evening that is less useful than Hoenn's version. It is the honest amount of
detail rather than the useful amount, and the two were not both available.

The encounters themselves are the usual: emitted from `SlotsB2` and `SlotsW2`, 131 and 130
lines, checked by `UnovaSequelPkHexAgreementTests` against their own tables. Grass, Surf and
swarms. Hidden Grotto is out because its contents rotate on a timer nothing here can read, and
the post-game places are out for the reason they always are.

**None of this is reachable yet**, and that is the last thing to say. `Gen5MemoryMap` has no
address for `IRE*` or `IRD*`, so the app cannot read a sequel party at all and the catalog
waits for a cartridge, exactly as White's did before D-054. The catalog was still worth
building first: it is the long job and it can be verified offline, while the address takes two
minutes once somebody has the game running.

**Alternatives considered:** write a route-level timeline anyway and correct it from reports
(rejected: the corrections would arrive as "it sent me somewhere I could not go", which is the
one failure this feature cannot afford), map only the stretch up to the fourth badge where the
order is clearer and leave the rest out (rejected: it hides most of the game rather than
describing it coarsely), and wait for someone who knows the games well to write the order
(deferred: that is the better catalog, and this one can be replaced by it without touching
anything else).

## D-061. Black 2 lands nowhere near the originals, which settles the arithmetic

**Status:** Accepted · 2026-08-15

English Black 2 (`IREO`) keeps its party pointer at `0x0223B4C4`, leading to `0x0221E3E4`.
Verified live: one candidate survived the entropy filter across a megabyte of main RAM, exactly
one word in that megabyte pointed at it, and the parser read the Snivy the player confirmed
carrying.

The shortcut did not work. For the four originals the pointer sits in a directory around
`0x0224F8xx`, and by the third cartridge that window could be read in seconds. Black 2's
directory is not there, so this went back to the full scan of D-054: read a megabyte, filter on
the party's shape, then on entropy, then keep whatever exactly one pointer leads to.

| Cartridge | Pointer | Party head |
| --- | --- | --- |
| Black, Italian | `0x0224F88C` | `0x022348AC` |
| Black, English | `0x0224F98C` | `0x022349AC` |
| White, Italian | `0x0224F8AC` | `0x022348CC` |
| White, English | `0x0224F9AC` | `0x022349CC` |
| **Black 2, English** | **`0x0223B4C4`** | **`0x0221E3E4`** |

D-054 recorded a temptation and refused it: the four originals sit within `0x120` of each
other, language apparently moving the party by `0x100` and version by `0x20`, and deriving the
remaining codes by arithmetic would have been easy. Black 2 lands about 82 KB below the whole
group, pointer and head both. Whatever pattern the four had was a pattern of one game in two
languages, and extrapolating it past that would have produced an address that is not merely
wrong but confidently wrong, pointing at real memory that would parse into something.

Five cartridges, and the rule is still the one from D-035: each code is measured. The test now
asserts the sequel's pointer is far below the originals, so the evidence against the
arithmetic is written down where somebody would go looking to use it.

This makes Black 2 complete: the catalog of D-060 was already waiting, so party, analysis and
badge-anchored hints all work on it. White 2 stays unmapped.

## D-062. A catch you cannot make is not a suggestion

**Status:** Accepted · 2026-08-15

Black 2 with a level 5 Snivy was offered a plan headed "Add Sudowoodo, Psyduck, Pidove", worth
133 points. The Sudowoodo was Lv.40-55. Three separate faults produced that one card, and only
the third is interesting.

**Species 0 is not a Pokémon.** The Gen 5 sequel tables carry empty slots as species 0, and
124 of them reached the catalog as encounters named `---`. The agreement test could not catch
it, because species 0 genuinely is in PKHeX's table: it is a placeholder, and the check was
asking whether each species exists there rather than whether it is a species at all. The
generator now drops it, and a test asserts no candidate has an id of zero.

**Swarm slots in the sequels are the post-game.** PKHeX's swarm set for Black 2 and White 2 is
the rustling grass that opens after the Hall of Fame, which is why Route 20, a starting-area
route, holds a Lv.40-55 Sudowoodo. Mapping them to shaking grass pulled the whole post-game
into checkpoint zero. They are out, and a test asserts nothing at the zero-badge checkpoint
starts above Lv.20.

**The real fault was in the analyzer, and it affected every game.** Practicality charged for
levels in one direction only:

```csharp
int grindLevels = Math.Max(0, averageLevel - encounter.MaximumLevel);
```

That is the cost of catching something *below* your team and training it up, which is time.
Something far *above* the team cost nothing at all, when it is the harder problem: it cannot be
worn down without fainting whatever is wearing it down, and it will not stay in a ball. So a
Lv.40 Sudowoodo scored as freely available to a Lv.5 party, and the score was not even wrong
by its own lights, because nobody had told it that up is a direction too.

Encounters more than eight levels above the party's average are now filtered out entirely
rather than merely penalised, beside the milestone gate, because this is the same kind of
claim: something the player cannot actually do. Below that, levels cost in both directions,
and reaching upward costs twice as much per level as grinding upward does.

The player who reported it put it exactly right: of the three, only Pidove was plausible.

**Not changed:** the Psyduck. It sits in Floccesy Ranch's grass at Lv.5 in PKHeX's table, so
the catalog is repeating the game rather than inventing. It may be seasonal, since Black 2
rotates encounters by month and the extraction folds the seasons together, and that would be a
real limitation worth its own entry. It is not this one.

**Alternatives considered:** penalise the level gap heavily instead of filtering (rejected: a
heavy penalty still lets it through when nothing else competes, which is exactly the empty
early game where this happened), scale the limit with the party's level rather than a flat
eight (rejected: eight levels is roughly the point where a wild Pokémon stops being catchable
regardless of where you are in the game), and keep swarms with a corrected milestone (rejected:
their real milestone is "after the credits", which this timeline has no way to express).

## D-063. English is the supported language for DS games, and the screen says which

**Status:** Accepted · 2026-08-15

Every DS cartridge has to be measured against a running game before the app can read it, and
D-061 killed the idea of deriving the rest by arithmetic. Thirteen Gen 5 codes exist per game
and eight languages apiece; measuring them all means owning them all. The supported set is
therefore English, and the setup screen now says so instead of leaving people to discover it
by loading a game and getting silence.

This applies to DS games only. A Game Boy Advance game is found through addresses cross-checked
against three independent sources (D-013) and confirmed by counting literals in the ROM
(D-034), so all thirteen Gen 3 codes work in every language and the note would be false there.

**The two Italian cartridges stay.** Italian Black and Italian White are measured, verified and
working, and deleting a verified address to make a policy tidier would break a player's game to
buy nothing. The policy is about what gets measured next, not about discarding what has been.

**The note is derived, not written.** `Gen5MemoryMap.Mapped` is the list, and the setup screen
composes its sentence from it, because a hand-written list of supported cartridges beside a
code list of supported cartridges is two lists that will disagree. This project has now watched
a test outlive three of its own examples for exactly that reason (D-058); a screen can rot the
same way, and quieter. A test asserts every mapped cartridge is named and that nothing
unmeasured is.

**Alternatives considered:** search for the address at run time and stop needing cartridges
(rejected by the author after being offered: the method works, five for five, and its filter
chain of shape, entropy and a unique pointer has never yet produced a false positive, but it is
five samples of one generation with one Pokémon in the party, and the honest version of it
needs the player to confirm the team it found, which is a feature rather than a fix), say
nothing and let unmapped games explain themselves when loaded (rejected: the explanation
arrives after somebody has gone and found a ROM), and drop the Italian entries for a cleaner
matrix (rejected above).

## D-064. White 2, and the sixth measurement

**Status:** Accepted · 2026-08-15

English White 2 (`IRDO`) keeps its party pointer at `0x0223B504`, leading to `0x0221E424`.
Verified live, an Oshawott at Lv.5, confirmed by the player.

The shortcut worked this time. Black 2's pointer had been found by scanning a megabyte; White 2
sits `0x40` past it, and reading the kilobyte around Black 2's address found it in seconds. Six
cartridges now:

| Cartridge | Pointer | Party head |
| --- | --- | --- |
| Black, Italian | `0x0224F88C` | `0x022348AC` |
| Black, English | `0x0224F98C` | `0x022349AC` |
| White, Italian | `0x0224F8AC` | `0x022348CC` |
| White, English | `0x0224F9AC` | `0x022349CC` |
| Black 2, English | `0x0223B4C4` | `0x0221E3E4` |
| White 2, English | `0x0223B504` | `0x0221E424` |

Within each pair of games the versions sit a fixed distance apart, and the two pairs are 82 KB
from each other. It is the same shape D-061 refused to extrapolate from, now with a second
example of the local regularity and the same reason not to trust it: the regularity holds
inside a pair and says nothing across one, and there is no way to know from four cartridges
which kind of gap the next one will be.

What this does buy is a cheaper search. The window to read first is now "beside the pointer of
the other version of the same game", which found White 2 in seconds rather than 68. That is a
heuristic for where to *look*, which costs nothing when it is wrong, rather than an answer.

Both sequels are now complete: their catalog was written first (D-060) and had been waiting.
Every Gen 5 game the app knows how to suggest for can now be read, in English, plus Black and
White in Italian.

**Note on the stubs.** Port 3334 refused every connection during this measurement and 3333
answered, which is the fallback D-039's correction added, working. It is also the state that
sent the app into silence earlier today, so the app's own fallback still deserves the look it
has not had.

## D-065. On Linux every read waited for an acknowledgement the kernel was holding back

**Status:** Accepted · 2026-11-06

On Linux, reading through melonDS's GDB stub cost about three frames a read instead of one, and
a stream of reads dragged the game down to 22 FPS. The cause is TCP, not melonDS: the client
now asks Linux to acknowledge at once (`TCP_QUICKACK`) before every receive, which brings a
read back to one frame and the game back to 60.

**Measured on** melonDS 1.1 (the Flathub build) on Debian Linux, running an English SoulSilver,
with the player watching the frame counter. Reads back to back, 256 bytes each, for 30 seconds:

| Client | Reads in 30 s | Median read | Game while reading |
| --- | --- | --- | --- |
| as before | 660 | 44.0 ms | 22 FPS |
| acknowledging at once | 1801 | 16.8 ms | 60 FPS |

60 FPS idle on both sides of each run. The second row is D-039's Apple Silicon figure exactly:
one read a frame, 15 KB/s. Through the app's own `MelonDsMemoryReader`, following a pointer
and reading a 1424-byte party, a poll went from a median of 324 ms to 117 ms.

**Why.** melonDS answers a read with two writes, its acknowledgement and then the reply, and
its socket keeps Nagle's algorithm on, so the reply is held until the client acknowledges the
first write. Linux delays acknowledgements by up to 40 ms once a connection looks interactive,
which a stream of small request and reply pairs does. Each side waits for the other until the
timer fires: 40 ms of nothing, then the frame the stub was waiting for anyway. Isolating it:

| Variant | Median read |
| --- | --- |
| 4, 64 or 256 bytes | 44, 44, 48 ms: size is not it |
| port 3333 or 3334 | 44 ms either: the stub is not it |
| `TCP_QUICKACK` before each receive, any size, either port | 16.8 ms |

It explains why D-039 never saw it: macOS does not delay acknowledgements this way, and
Windows was never measured.

**The fix sits in `GdbRemoteClient`**, before every receive, because Linux clears the flag once it
has acted on it, so setting it once at connect time would hold for a few reads and then
silently stop. .NET has no named option for it, so it is set as a raw socket option, on Linux
only. If setting it ever fails the read still works at the old speed, so the failure is
swallowed rather than turned into a lost connection.

**Tested without an emulator.** `FakeGdbStub` can now reply the way melonDS does, two writes
from a socket with Nagle on, and the test that times reads against it failed at 44.0 ms
before the change, the same number the real stub produced, and passes after it. It skips
outside Linux, where there is nothing to reproduce.

**Alternatives considered:** read fewer, larger blocks (rejected: the cost is per request, not
per byte, and the stub's 256-byte ceiling is D-039's), poll less often (rejected as the fix:
it lowers how often the game stutters, not how long a read takes, and it delays the dashboard
for every player to cover a problem on one platform), and ask melonDS to turn Nagle off on its
stub (worth reporting upstream, but it would fix only new versions, while this fixes every
version the app supports, today).
