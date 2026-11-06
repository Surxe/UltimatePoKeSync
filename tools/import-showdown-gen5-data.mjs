import fs from "node:fs";

// Move metadata for Gen 5, and with --generation=4 for Gen 4, from Pokémon Showdown, pinned to the same commit as the Gen 3
// import so both generations describe the same snapshot of the world. See D-024 and D-041.
//
// Showdown stores the *current* generation's data and walks backwards through per-generation
// mods, each overriding only what changed after it. Reading data/moves.ts alone therefore
// gives Gen 9 numbers wearing Gen 5 names: Assurance is 60 today and was 50 in Black, and
// nothing in the base file says so. The overrides are applied newest first (gen8, gen7,
// gen6, then gen5), so the last word belongs to the generation being imported.
//
// Usage:
//   node tools/import-showdown-gen5-data.mjs <moves.ts> <gen8> <gen7> <gen6> <gen5>
//   node tools/import-showdown-gen5-data.mjs --generation=4 <moves.ts> <gen8> ... <gen5> <gen4>
//
// Gen 4 is the same import with one more mod applied last and a lower ceiling: the
// physical/special split already belongs to the move there, so the output has the same
// shape. Without --generation the script behaves exactly as it always has. See D-070.

const revision = "db93869dcc216c0be39e7f86e9a64edcc7496d89";

/// The last move each generation has. Gen 5 ends at Fusion Bolt, Gen 4 at Shadow Force;
/// anything above did not exist in those games.
const lastMoveByGeneration = { 4: 467, 5: 559 };

const flags = process.argv.slice(2).filter((argument) => argument.startsWith("--"));
const [basePath, ...modPaths] = process.argv.slice(2).filter((a) => !a.startsWith("--"));

const generationFlag = flags.find((flag) => flag.startsWith("--generation="));
const generation = generationFlag ? Number(generationFlag.slice("--generation=".length)) : 5;
const lastMove = lastMoveByGeneration[generation];

if (lastMove === undefined) {
  throw new Error(`Unsupported generation: ${generation}. Known: 4, 5.`);
}

if (!basePath || modPaths.length === 0) {
  throw new Error(
    "Usage: node import-showdown-gen5-data.mjs [--generation=4] <moves.ts> <gen8> ... <gen5> [<gen4>]");
}

const moves = parse(fs.readFileSync(basePath, "utf8"));

for (const modPath of modPaths) {
  const overrides = parse(fs.readFileSync(modPath, "utf8"));

  for (const [id, changed] of overrides) {
    const existing = moves.get(id);
    if (!existing) {
      // A move a later generation removed entirely, or one Showdown only defines in a mod.
      // Either way it cannot be numbered, so it cannot be placed in the table.
      continue;
    }

    for (const field of ["basePower", "category", "type", "name"]) {
      if (changed[field] !== undefined) {
        existing[field] = changed[field];
      }
    }
  }
}

const basePowers = new Array(lastMove + 1).fill(0);
const categories = new Array(lastMove + 1).fill("Status");
const catalog = [];

for (const [id, move] of moves) {
  if (!(move.num >= 1 && move.num <= lastMove)) {
    continue;
  }

  if (move.basePower === undefined || !move.category || !move.type || !move.name) {
    throw new Error(`Move metadata is incomplete for ${move.name ?? move.num}.`);
  }

  basePowers[move.num] = move.basePower;
  categories[move.num] = move.category;
  catalog.push({ id, number: move.num, name: move.name, type: move.type });
}

const missing = [];
for (let id = 1; id <= lastMove; id++) {
  if (!catalog.some((move) => move.number === id)) {
    missing.push(id);
  }
}

if (missing.length > 0) {
  throw new Error(`No data for move numbers: ${missing.join(", ")}`);
}

// Showdown lists every Hidden Power type under the same move number, and a few other
// moves twice. One entry per number, the shortest id winning, exactly as the Gen 3 import
// does; otherwise the catalog cannot be indexed by number at all.
const byNumber = new Map();
for (const move of catalog) {
  const existing = byNumber.get(move.number);
  if (!existing || move.id.length < existing.id.length) {
    byNumber.set(move.number, move);
  }
}

const unique = [...byNumber.values()].sort((left, right) => left.number - right.number);

const which = flags.includes("--catalog") ? "catalog" : "power";
const output = which === "catalog"
  ? { generation, revision, moves: unique }
  : { generation, revision, basePowers, categories };

process.stdout.write(JSON.stringify(output, null, 2) + "\n");

/// Reads one Showdown move table. Entries in a mod carry only what that generation changed,
/// so every field is optional here and merging is the caller's job.
function parse(source) {
  const result = new Map();
  let current;
  let id;

  for (const line of source.split(/\r?\n/)) {
    const entry = line.match(/^\t(?:"([a-z0-9]+)"|([a-z0-9]+)): \{$/);
    if (entry) {
      id = entry[1] ?? entry[2];
      current = {};
      continue;
    }

    if (!current) continue;

    // Only fields at the entry's own indentation are read, so the nested objects and the
    // event handlers below them cannot be mistaken for move data.
    const number = line.match(/^\t\tnum: (-?\d+),$/);
    const name = line.match(/^\t\tname: "(.+)",$/);
    const type = line.match(/^\t\ttype: "([A-Za-z]+)",$/);
    const power = line.match(/^\t\tbasePower: (\d+),$/);
    const category = line.match(/^\t\tcategory: "([A-Za-z]+)",$/);

    if (number) current.num = Number(number[1]);
    if (name) current.name = name[1].replaceAll('\\"', '"');
    if (type) current.type = type[1];
    if (power) current.basePower = Number(power[1]);
    if (category) current.category = category[1];

    if (line === "\t},") {
      const previous = result.get(id);
      // Showdown occasionally defines the same move twice; the first wins, as in the Gen 3
      // import, so a re-run produces the same file.
      if (!previous) {
        result.set(id, current);
      }

      current = undefined;
    }
  }

  return result;
}
