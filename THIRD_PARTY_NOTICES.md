# Third-party notices

## Pokémon Showdown Gen 3 Random Battle sets

The embedded Random Battle sets and the generated Gen 3 move catalog are derived from
Pokémon Showdown:

- Source: https://github.com/smogon/pokemon-showdown
- File: https://github.com/smogon/pokemon-showdown/blob/db93869dcc216c0be39e7f86e9a64edcc7496d89/data/random-battles/gen3/sets.json
- Move metadata: https://github.com/smogon/pokemon-showdown/blob/db93869dcc216c0be39e7f86e9a64edcc7496d89/data/moves.ts

## Pokémon Showdown Gen 5 move metadata

The same pinned commit. Gen 5 values are reconstructed by applying Showdown's own
per-generation overrides (`data/mods/gen8`, `gen7`, `gen6`, `gen5`) over `data/moves.ts`,
because the base file holds the current generation's numbers. See D-041.

- Files: `data/moves.ts`, `data/mods/gen{8,7,6,5}/moves.ts`
- Importer: `tools/import-showdown-gen5-data.mjs`

## Pokémon Showdown Gen 4 move metadata

The same pinned commit and the same importer, run with `--generation=4`, which applies the
`gen4` override last. No Gen 4 Random Battle sets are bundled. See D-070.

- Files: `data/moves.ts`, `data/mods/gen{8,7,6,5,4}/moves.ts`
- Importer: `tools/import-showdown-gen5-data.mjs --generation=4`
- Retrieved: 2026-11-06

## Pokémon Showdown Gen 5 Random Battle sets

The same pinned commit again, and the same standing as the Gen 3 sets: expert-authored role
and movepool examples, not standard OU usage. See D-024 and D-044.

- File: https://github.com/smogon/pokemon-showdown/blob/db93869dcc216c0be39e7f86e9a64edcc7496d89/data/random-battles/gen5/sets.json
- Revision: `db93869dcc216c0be39e7f86e9a64edcc7496d89`
- Retrieved: 2026-08-10

Level-up learnsets were also imported from Showdown until 2026-08-11. They now come from
PKHeX.Core, which holds a separate table per game rather than one per generation (D-027).

Pokémon Showdown is distributed under the MIT License:

Copyright (c) 2011-2026 Guangcong Luo and other contributors
http://pokemonshowdown.com/

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.


## Bungee, Nunito and DM Mono

The three typefaces of the dashboard, under the SIL Open Font License 1.1, which permits
bundling them with software. They ship in `src/UltimatePoKeSync.App/Assets/Fonts` and are the
only files in this repository that somebody else wrote.

- Bungee: Copyright 2023 The Bungee Project Authors (https://github.com/djrrb/Bungee)
- Nunito: Copyright The Nunito Project Authors (https://github.com/googlefonts/nunito)
- DM Mono: Copyright The DM Mono Project Authors (https://github.com/googlefonts/dm-mono)
- Licence: https://openfontlicense.org
