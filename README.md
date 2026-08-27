# FireFront

Fire that actually spreads. Torch a wall and it can take the whole build with it, jump
to the treeline, and crawl across open ground to get there — not just "this one piece
is on fire," an actual moving front.

## What it does

- Structures, standing trees, and felled logs catch fire and burn down **for real** —
  with real drops (trees fell via vanilla's own felling and leave logs, logs leave wood)
- Fire spreads to nearby burnable things: structure-to-structure, tree-to-tree,
  structure-to-tree
- It also crawls **across open ground** between things too far apart to ignite each
  other — visible as small ember flickers advancing through the grass
- Standing in fire hurts, players and mobs alike — the same mechanism vanilla
  campfires use
- **Fire follows the wind.** The front stretches downwind and thins upwind using the
  game's real wind — and how *hard* it blows matters: a gale drives a long narrow
  tongue of fire, a calm day burns a lazy, even circle
- Fires start small and **ramp up** over ~10 minutes instead of instantly raging
- **Rain douses grass fire** (an already-burning building keeps going)
- Burned ground is spent — it can't relight for a while, so the front *advances*
  instead of churning in place, and it leaves burn scars behind
- **Dirt paths and cultivated ground are real firebreaks**; water stops spread too
- Ground fire won't wander more than ~40m from where the fire started
- Burned trees **regrow** after ~15 minutes if the spot is still clear
- Press **G** to extinguish what you're looking at, plus ground fire around you
- Fires **remember who lit them** — the whole spreading front carries its arsonist,
  even fire that crawled far from the first spark; natural fire belongs to nobody
- Fires **survive a server restart** — burning objects and ground fire come back with
  their remaining burn time, spent fuel stays spent, and trees waiting to regrow
  still regrow
- Everything is config-tunable, and adjustable live from the console — no restart

## Install

Through a mod manager: install and play — no config needed.

By hand: drop `FireFront.dll` into `BepInEx/plugins/` on **both server and clients** —
the server runs the fire, and a client without the mod can light fires the server never
learns about (ignition processes on the piece's owner). Keep versions matched: a mixed
pair fails safe (ignition quietly no-ops) rather than desyncing.

## Plays well with

- **Ragnarok's Wrath** (Raven Iron) — the world-simulation mod this fire feeds: burning
  zones scar the land itself, arson is booked into the land's memory, and Devastating
  Storms hurl lightning that starts real FireFront fires. Each mod runs fine without
  the other.

## Console commands (` to open)

| Command | Effect |
|---|---|
| `firestatus` | What's burning, plus every live setting |
| `ignite` | Ignite what's under your crosshair |
| `startfire [radius]` | Ignite everything burnable within radius of you |
| `stopfire` | Extinguish what's under your crosshair |
| `clearfires` | Extinguish everything instantly |
| `firedebug` | Toggle verbose fire logging |
| `fireset <key> <value>` | Live-tune any setting, no restart |

## Known limitations

- The fire visual is homemade (procedural), not a vanilla asset — it looks like fire,
  just not *exactly* like Valheim's own
- Defaults are tuned lively; `fireset` them down if the fire is too hungry for your
  server
- For a Steam-Cloud world, the fire-state sidecar stays on the host machine and does
  not travel with the save (cloud saves have no filesystem path to sit next to)
