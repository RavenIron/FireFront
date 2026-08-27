# Changelog

## 0.18.3

- **`fireset` now applies on the server no matter where you type it.** Console
  commands run where they're typed, and every FireFront setting that matters is
  read by the server's simulation — so a client's fireset used to change its own
  irrelevant config copy and silently do nothing ("rampstart didn't work",
  "burnbuildings didn't work"). A client's fireset now also forwards to the
  server over a new RPC and lands on the server's real config, with the same
  value parsing and range clamping. The server logs every remote set with the
  sender's peer id.

## 0.18.2

- **New config `BurnPlayerBuildings` (default true) + `fireset burnbuildings`.**
  Set false for an anti-grief server: fire never ignites anything carrying a
  placement creator stamp — walls, floors, furniture — by any path (spread, fire
  arrows, console), while world-generated structures still burn. The wildfire
  still crawls past a protected base and still hurts anyone standing in it. This
  is the hard guarantee on top of the terrain firebreak, which already stops
  spread at leveled/pathed base ground but not deliberate arson.

## 0.18.1

- **Fixed: fire went invisible for a client that reconnected without relaunching.**
  Valheim creates a fresh routing instance per connection; FireFront registered its
  network handlers once per process, so a client kicked by a server restart that
  auto-rejoined got none of the fire broadcasts — the fire burned, invisibly, until
  the game was fully relaunched. Handlers now re-register whenever the routing
  instance changes. Receipt-side `[SYNC-DIAG]` debug traces are kept so this class
  of silent drop is diagnosable from a single log in future.

## 0.18.0

- **Fires survive server restarts.** Burning objects and ground fire come back with
  their remaining burn time, spent fuel stays spent, the fire keeps its origin,
  ramp age, and arsonist, and trees waiting to regrow still regrow. Stored in a
  small sidecar file next to the world save, written every 60s and on shutdown —
  a hard kill loses at most the last minute of fire drift. Toggle with `fireset
  persistfires`.

## 0.17.6

- Fixed a `FieldAccessException` spamming the main menu from 0.17.5's item
  registration (a private-in-the-real-assembly ObjectDB field). Registration
  failures now degrade to "item missing" with one warning, never menu errors.

## 0.17.5

- **New item: the Dousing Bomb.** A throwable that extinguishes everything within
  ~6m of impact — ground fire and burning structures/trees alike. Hand-craftable:
  3 Resin + 2 Leather scraps makes 3. Tune the blast with `fireset dousingradius`.
- The extinguish key's radius now also clears burning objects around you, not just
  ground fire.

## 0.17.4

- **Forest spread now actually works on dedicated servers.** Object-to-object and
  ground-to-object spread had never worked there — the headless server tracks the
  world as ZDOs and never instantiates the GameObjects the old candidate scan
  looked for, so the server literally could not see trees. Spread candidates now
  come from the ZDO layer, and an instance is created only for objects that
  actually catch fire.

## 0.17.3

- **Fires remember who lit them.** The spreading front carries its igniter's player
  id, captured once per fire event from the actual attacker (not the network sender),
  and reset when the fires die. Natural and creature fire belongs to nobody. This
  feeds Ragnarok's Wrath's arson attribution.
- The ignite request RPC was renamed so a mixed-version server/client pair quietly
  no-ops instead of desyncing — update both sides together.

## 0.17.2

- **Wind bias now scales with real wind strength.** A gale drives a long narrow
  tongue of fire; dead calm burns evenly in all directions. `WindInfluence` (0–1,
  default 1) dials how much weather shapes the front.
- Public read API (`FireManager.CollectActiveFirePositions`) for companion mods —
  Ragnarok's Wrath reads it to scar burning zones.

## 0.17.0 – 0.17.1

- Procedural fire visuals now default **on** — earlier builds simulated fire fully
  but rendered nothing until a console toggle. Upgrading an existing install? BepInEx
  keeps your old `UseProceduralVfx = false`; flip it to true or delete the line.
- Floating ground fire fixed for real: terrain height now raycasts Valheim's own
  terrain layer instead of trusting a call that echoed its input back.

## Earlier (0.1 – 0.16)

The road here, condensed: ignition from vanilla fire damage; spread with caps, a
queue, and ramp-up; ground fire as an invisible cell grid with its own visuals,
damage, and a 40m leash; rain suppression; firebreaks on dirt and cultivated ground;
water blocking; fuel exhaustion and burn scars; tree felling with real drops and
timed regrowth; the G-key extinguisher. The full engineering log — every bug and
what it taught — lives in the repo at `docs/DEVLOG.md`.
