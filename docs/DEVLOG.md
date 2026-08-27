# FireFront 0.17.2

Vanilla-preserving structure-fire spread for Valheim. Pieces that take vanilla
fire damage catch fire, burn for a configurable duration, then are destroyed.
Fire spreads to burnable neighbors within a configurable radius, gated by a
concurrent-burn cap and a FIFO overflow queue.

- **Wind bias now scales with real wind strength (0.17.2).** Wind bias shipped
  in 0.16.0 reading `EnvMan.GetWindDir()` only, so the front leaned downwind by
  exactly the same amount in dead calm as in a gale — weather could change which
  way the fire went, never how sharply. `ValheimBridge.GetWindIntensity()` now
  reflects vanilla's `EnvMan.GetWindIntensity()` (verified against the decompiled
  body, not the signature: it returns `m_wind.w`, and every write goes through
  `SetTargetWind`, which clamps to 0.05–1). A new `WindInfluence` config (0–1,
  default 1) multiplies with that live intensity to give a single bias strength:
  0 or dead calm ignites every neighbor exactly as unweighted spread did, 1 at
  full gale applies the whole `WindUpwindIgniteChance` curve. Defaulting to 1
  keeps the knob out of the way — live wind alone decides how sharp the front
  is, and turning it down damps how much weather swings the fire's shape.
  `WindUpwindIgniteChance` keeps its existing meaning and name, so existing
  configs still load unchanged — but note the front is now *softer* than it was
  through 0.17.1 at anything below a gale even at influence 1, since intensity
  sits mid-range most of the time.
  `firestatus` prints the influence setting and the live intensity side by side,
  because the two multiply and a correctly-set influence with low wind looks
  identical to the feature being broken.

- **Ground fire leash (0.16.6).** Confirmed via a real dedicated-server test
  (client + server logs both captured): `IgniteAdjacentGroundCells`
  (cell-to-adjacent-cell propagation) had no distance limit at all — only a
  cap on how many cells could burn *at once*, not how far the front could
  travel. Combined with wind bias, a fire would march steadily in one
  direction indefinitely (observed: 100+ meters in under 3 minutes, still
  going when the test ended), wandering into zones far from any player.
  Since spread-to-real-objects and damage zones both depend on the fire
  actually being near something, the practical symptom looked like "fire
  doesn't spread and nothing takes damage" — the fire was very much active,
  just silently marching away from the player instead. Fixed with a leash:
  `GroundMaxSpreadDistance` (default 40m) caps how far ground-to-ground
  spread can travel from the current fire's origin point, captured once at
  first ignition (object or ground). Toggle with `fireset
  groundleashenabled`; tune with `fireset groundleashdistance`.
  Object-to-ground seeding (a burning piece/tree lighting the ground right
  around itself, bounded by `GroundSpreadRadius`) essentially never trips
  this. Single global origin, not per-fire — same simplification the ramp
  clock already makes — so a second, unrelated fire started while an
  earlier one is still smoldering is leashed to the first fire's origin,
  not its own; origin resets once every fire fully burns out.

- **Ground fire height echo fixed (0.16.7).** Confirmed via a real
  dedicated-server test: `GetGroundHeight`'s reflected call to vanilla's
  `ZoneSystem.GetGroundHeight` was returning exactly `10000.00` — the same
  synthetic Y the code deliberately queries from (see the 0.13.2/0.13.3
  floating-fire fix above). When a zone's heightmap isn't generated/loaded
  yet, the real vanilla method doesn't raycast at all — it just echoes back
  whatever Y it was asked about, and that Y is always our own synthetic
  10000. Trusting it verbatim spawned that ground cell's VFX/damage zone
  10,000 meters in the sky, unreachable by any player, and made the
  water-level check meaningless (10000 always clears ~30). Fixed by
  treating any result above 9000 as an unresolved-terrain echo rather than
  a real sample, falling back to the inherited approximate Y instead — off
  by some drift, but in the real terrain's neighborhood instead of orbit.

- **Real cause of "nothing takes fire damage" found and fixed (0.16.9).**
  The diagnostics added in 0.16.8 caught it immediately: `GetCharacterLayerMask`
  was resolving `EffectArea.s_characterMask` to a literal `0`. A LayerMask of
  0 matches NO physics layers at all, so every `FireBurnZone`'s
  `Physics.OverlapSphereNonAlloc` query silently found nothing, forever — no
  error, no exception, indistinguishable from "no character was ever nearby."
  Root cause: `s_characterMask` is apparently only populated by some real
  `EffectArea` instance's own `Awake()` (a lit campfire/bonfire, etc.), not a
  static initializer — if none had run yet anywhere in the loaded world, the
  static field just sat at C#'s default `0`, and the old code trusted that
  zero as a legitimately-resolved mask. Fixed by treating a resolved `0` the
  same as an unresolved field: fall back to `~0` (everything), same as the
  null/wrong-type case already handled — safe either way since callers still
  filter by `Character` component afterward. Not cached, so as soon as a
  real `EffectArea` initializes later in the session, the correct mask
  starts being used automatically.

- **`UseProceduralVfx` now defaults to true (0.17.0).** It shipped defaulting
  to false, meaning fire was fully simulated (ignition, spread, damage
  zones) but rendered nothing at all unless a player explicitly ran `fireset
  procedural true` — confirmed as the actual explanation for a real,
  repeated "no spread, no VFX, no damage" report on a live dedicated server:
  every heartbeat line for the whole session showed `vfx '', procedural
  False`. IMPORTANT if upgrading an existing install: BepInEx only applies a
  coded default the FIRST time a config key is created — it will NOT
  retroactively flip an existing `UseProceduralVfx = false` already written
  to your server's `.cfg` file. Existing installs must either delete that
  line from the config (so BepInEx regenerates it with the new default) or
  edit it to `true` directly, or run `fireset procedural true` after the
  world loads.

- **Floating ground fire — real cause found and fixed (0.17.1).** The
  0.16.7 "implausible height" guard treated the reflected
  `ZoneSystem.GetGroundHeight` failure as an occasional edge case. A full
  dedicated-server session proved otherwise: every single call, hundreds of
  them across dozens of meters of terrain, failed identically — 100%
  failure, not intermittent. The "fall back to inherited y" guard was
  therefore silently active for every ground cell in every fire, which
  pins the ENTIRE fire's ground-cell height to wherever it first ignited,
  never updating as it spreads across real (sloped/varied) terrain — the
  actual cause of visibly floating fire, and, since the damage zone uses
  the same wrong height, also the reason standing in visible ground fire
  dealt no damage. Fixed by no longer trusting the reflected ZoneSystem
  call at all: `GetGroundHeight` now raycasts directly against Valheim's
  own `"terrain"` Unity layer via `Physics.Raycast`, which doesn't depend
  on whatever internal per-zone state the reflected method needs. The old
  reflected path is kept only as a secondary fallback if the raycast
  itself somehow finds nothing.

## Scope (original 0.1 spec — historical)

*The numbers below are the original locked spec. Most have since been widened
for real-world testing — current defaults live in `Config/FireConfig.cs` and
print live via `firestatus`.*

- **Ignition:** vanilla fire damage on a piece with `m_burnable = true`. No new
  ignition triggers, no flammability changes, no drop logic changes.
- **Burn timer:** 30s per piece (config), then destroy.
- **Spread:** requires an actively burning neighbor within 2–4m (config).
- **Cap:** 25 concurrent burning targets (config).
- **Queue:** FIFO, 5–10 slots (config), no distance weighting. Overflow drops
  silently and is naturally re-attempted next cycle.
- **Targets (0.2.0):** structures (`WearNTear`), standing trees (`TreeBase`),
  and felled logs (`TreeLog`) — toggle via `BurnTreesAndLogs` config / `fireset
  trees`. A burning structure can ignite nearby trees and vice versa.
- **Visuals — corrected in 0.5.0.** Earlier versions (0.3.0–0.3.1) tried
  spawning vanilla prefabs and stripping scripts off them as a safety net.
  That was wrong: destroying a `ZNetView` directly with `Object.Destroy()`
  (rather than calling its own `Destroy()`) is itself the same category of
  bug that corrupted `ZNetScene` earlier — stripping only ever protected
  against non-networked scripts, and it turns out every registered
  fire-related vanilla prefab carries a `ZNetView` (they're normally spawned
  as a result of networked actions). `SpawnVfx` now refuses outright to
  spawn anything with a `ZNetView` anywhere in its hierarchy, rather than
  trying to clean it up after the fact — fail-safe by construction.
  Since that ruled out every real vanilla candidate, 0.5.0 adds a **procedural
  fire effect** built entirely in code (`ValheimBridge.CreateProceduralFireVfx`)
  — a small particle system + warm light, zero vanilla dependency, zero
  ZNetView, zero corruption risk. Enable with `fireset procedural true`
  (takes priority over `VfxPrefabName`). Won't look identical to vanilla
  fire, but it's the safe path. `firecheckprefab <name>` still exists if you
  want to check a specific vanilla prefab yourself — it'll tell you truthfully
  whether it's usable (most won't be).
- **Ground fire (0.4.0, fixed 0.4.1):** fire can now spread across open
  ground, not just object-to-object. An invisible grid of ground-fire cells
  (position + timer only — no GameObject, no ZNetView, zero corruption risk)
  lets fire cross gaps between trees/pieces that are farther apart than
  SpreadRadius, since grass itself has no real game object to ignite (it's
  `ClutterSystem`, GPU-instanced visual painting with no per-blade component
  — there's nothing to hook). **0.4.1 fixed a real bug**: ground-to-ground
  propagation was using the full `GroundSpreadRadius` every cycle instead of
  just the immediate neighboring cells, so every burning cell instantly
  flooded its entire reach in one tick, and every newly-lit cell did the same
  the next tick — an explosive area blowup (and far more trees catching fire
  than intended) instead of a gradual advancing front. Fixed by making
  ground-to-ground only ever step to adjacent cells per cycle;
  `GroundSpreadRadius` now only governs how far a cell reaches out to catch a
  nearby real object, which was never the runaway part. Toggle with
  `GroundSpreadEnabled` / `fireset groundenabled`; tune with `groundcellsize`,
  `groundradius`, `groundburnduration`, `groundmax`. Test directly with
  `firegroundignite [radius]` (seeds cells around the player, no tree/piece
  needed).
- **Ground fire visuals (0.6.0):** ground cells now get a real (procedural,
  zero-vanilla-dependency) effect when `UseProceduralVfx` is on — deliberately
  cheaper than the per-object fire (no light, fewer/smaller/shorter particles),
  since up to `GroundMaxConcurrent` (default 200) cells can be burning at
  once and rendering 200 full fire effects with dynamic lights would be a
  real performance problem. `GroundVfxMaxConcurrent` (default 30) caps how
  many are actually rendered independent of how many are logically burning —
  the simulation keeps running everywhere, only a bounded number are ever
  visible. Tune with `fireset groundvfxmax`. Each cell's height is now fixed
  at ignition time rather than recomputed every cycle (was silently doing an
  O(cells × objects) scan every single cycle before — also just a straight
  performance win independent of the visual feature).
- **Burn scars (0.12.0):** ground fire now leaves a dark, soft-edged scorch
  decal behind when a cell burns out naturally or gets manually
  extinguished — purely cosmetic, fully fire-and-forget (no tracking
  dictionary or cleanup path needed, unlike the VFX/damage zones — it just
  self-destructs via Unity's own delayed-`Destroy` overload after
  `ScorchMarkLifetimeSeconds`, default 5 minutes). Built the same way as the
  particle fire texture — a small procedural radial-gradient texture, just
  dark instead of bright, on a flat rotated quad with its collider stripped
  so it doesn't interfere with anything. Doesn't fire on `clearfires` (an
  admin reset should leave a clean slate, not scorch everything you were
  just testing). Toggle with `fireset scorchmarks`; tune lifetime with
  `scorchlifetime`.
- **Rain suppresses ground fire (0.11.0):** a real environmental mechanic,
  not another button — while it's raining (`EnvMan.s_isWet`, the same flag
  behind the in-game "Wet" status), ground fire can't spread cell-to-cell at
  all, and any newly-ignited ground cell burns out roughly 70% faster
  (`RainGroundBurnDurationMultiplier`, default 0.3×). Object fire (an
  already-burning structure or tree) is deliberately unaffected — a raging
  building fire keeps going despite rain, but the grass fire crawling away
  from it gets doused, matching real intuition. A burning object can still
  briefly catch nearby grass even in rain (that seeding path isn't gated),
  it just won't travel anywhere before going out. Toggle with
  `fireset rainsuppress`; tune the multiplier with `rainmultiplier`.
  Water-on-contact extinguishing (a burning cell over a river/lake going
  out) is a planned follow-up — `WaterVolume`'s API exists but needs a
  proper multi-instance lookup investigated before building it, rather than
  guessing at the pattern.
- **Manual extinguish (0.10.0):** players can put fire out in-game now, not
  just via console commands. Press `ExtinguishKey` (default `G`, rebindable
  in the config file) to extinguish whatever burning object is under your
  crosshair plus any ground fire within `ExtinguishGroundRadius` (default 3m)
  of you — the same logic `stopfire`/ground-cell removal already used,
  bound to a real key. Shows a HUD message via `Player.Message` when
  something was actually put out. Live-tunable radius via `fireset
  extinguishradius`; the key itself is easiest to rebind directly in the
  BepInEx config file since parsing keycodes from a console string wasn't
  worth the complexity for v1.
- **Fire ramps up over time (0.13.0):** a fire no longer hits full
  configured intensity the instant it starts — it begins at
  `FireRampStartFraction` (default 25%) of its configured spread radius and
  concurrent cap, then climbs linearly to 100% over `FireRampDurationSeconds`
  (default 2 minutes). Applies to `SpreadRadius`, `GroundSpreadRadius`,
  `MaxConcurrentBurning`, and `GroundMaxConcurrent` — the four things that
  made a fire feel instantly catastrophic rather than building like a real
  wildfire. Tracked as a single global fire-age clock (not per-fire), which
  resets to fresh when everything's fully burned out or `clearfires` is
  used, so the next fire ramps up from scratch. Ground-to-ground
  cell-to-adjacent-cell spread isn't further throttled by this — it's
  already the deliberately gradual mechanism from the 0.4.1 fix. Toggle with
  `fireset rampenabled`; tune with `rampduration`/`rampstart`. `firestatus`
  shows current ramp progress as a percentage.
- **Real vanilla dirt paint — the actual journey, through 0.15.7.**
  0.15.0's direct `TerrainComp.PaintCleared` approach reliably failed with
  `FindTerrainCompiler resolved and ran, but returned null` — reflection
  and static/instance flags were confirmed correct (extended the DLL
  metadata parser to verify this directly rather than guess), but
  `TerrainComp` instances are **lazily created**: one only exists for a zone
  once something has genuinely modified that terrain before. 0.15.3 tried
  spawning the real `cultivate` piece (confirmed via `firecheckprefab` to
  carry `ZNetView, Piece, TerrainModifier` — the actual prefab the
  Cultivator places) via a raw `ZNetScene.SpawnObject` call — took two more
  wrong guesses to even find the right method name/signature (it's not
  `Instantiate`), and even once correctly called, it consistently returned
  null with `IsAreaReady` confirmed true, ruling out a timing issue. Turned
  out `cultivate` isn't meant to be spawned as a raw prefab at all — it's
  placed through the Hoe's normal build flow, which does real placement
  setup (`Piece.OnPlaced()`, `TerrainModifier`'s `m_triggerOnPlaced` hook)
  that a raw spawn skips entirely. 0.15.7 calls `Player.PlacePiece` directly
  instead — the actual lower-level placement executor real tool use goes
  through, not the higher-level `TryPlacePiece` wrapper (which adds
  cost/validity UI checks we don't want). Runs on the local player's own
  `Player` instance, `doAttack` passed false to at least skip the swing
  animation. **One real unknown still open**: since this executes through
  the player's own placement pipeline, it's not fully verified whether it
  causes any other visible side effects on the player character beyond the
  swing animation. **Still genuinely higher risk than everything else in
  FireFront** — this writes to persistent per-zone terrain data saved to
  disk. Off by default; enable with `fireset dirtpaint true` on a test world
  only. When enabled it replaces (not adds to) the scorch decal, and the
  result is permanent, not timed. Falls back to the decal automatically if
  placement fails for any reason.
- **Repeated Hoe sound fixed in 0.15.8.** Confirmed by real testing: `PlacePiece`
  plays the piece's own placement effect (`Piece.m_placeEffect`) regardless
  of `doAttack`, so every ground-cell burnout was firing a Hoe swing sound —
  would get old fast on any actively-spreading fire. Fixed by temporarily
  blanking the *prefab's* shared `m_placeEffect` field to an empty
  `EffectList` for the duration of the call only, restoring the original
  value immediately after in a `try`/`finally` (so it happens even if
  `PlacePiece` throws) — real player use of the actual Cultivator/Hoe is
  completely unaffected, since the field is back to normal before anyone
  else could observe the change. Safe specifically because effect playback
  happens synchronously as part of placement, not on some later frame we
  can't control the timing of.
- **Better fire VFX + rising smoke (0.14.0):** the flame itself now varies
  particle size/rotation, grows through its first half-life then shrinks
  (a "flame lick" curve, instead of a constant size fading in place), and
  has subtle turbulence via Unity's particle noise module so it doesn't
  look like it's on rails. New: a separate smoke layer (a child particle
  system, since smoke needs a completely different lifetime/size/color
  curve than flame) rises from just above the flame tips, expands as it
  goes (the opposite of the flame, which shrinks), drifts with a wider
  cone angle, and fades out over 2.5-4 seconds. Scoped to object fire only
  (pieces/trees/logs) — ground fire stays deliberately cheap since up to
  200 cells can be burning at once. Toggle with `FireSmokeEnabled` (no
  console command yet — config-file only for this one).
- **Bare-dirt scorch marks (0.13.3):** the burn-scar decal now looks like
  actual scorched earth instead of a translucent dark smudge — opaque brown
  dirt tones with Perlin-noise mottling for natural variation, fading only
  right at the rim rather than throughout. Widened from 1.2× to 1.5× the
  ground cell size to cover more of the surrounding area. One real
  limitation worth knowing: this is a flat decal on the terrain surface —
  tall grass clutter blades can still poke up visibly through it, since
  hiding/removing the grass itself would mean interacting with
  `ClutterSystem`'s live procedural painting, a much bigger and riskier
  change than a ground texture. Reads well as "this ground burned," just
  won't look like grass was fully cleared away.
- **Floating fire — height query hardened (0.13.2/0.13.3).** Terrain height
  is now sampled from a fixed high point (`y=10000`) rather than the
  previous approximate/inherited Y, so a raycast-based `GetGroundHeight`
  implementation reliably finds the true surface regardless of how far off
  the prior approximation was. If fire still floats after updating, the
  most likely cause is leftover state from before the fix — a cell's height
  is computed once, at ignition, and never re-sampled, so anything already
  burning when you rebuilt keeps its old (wrong) height forever. Run
  `clearfires` and start a fresh fire to properly test.
- **Ground fire damage coverage — real fix in 0.13.1.** Found the actual
  cause of "fire only hurts near trees": `GroundVfxMaxConcurrent` (default
  30) was being used as a single shared cap for BOTH the particle visual
  AND the damage zone. Ground fire can have up to 200 cells burning
  logically, but only ~30 of them ever got a `FireBurnZone` attached at all
  — the other ~170 burned with zero damage capability. Objects
  (pieces/trees/logs) have their own separate, much higher cap
  (`MaxConcurrentBurning`) and were never affected, which is exactly why
  damage only ever seemed to work near something burning, not out in open
  ground. Fixed by splitting into two fully independent caps:
  `GroundVfxMaxConcurrent` (still low, rendering-cost-driven) and the new
  `GroundDamageMaxConcurrent` (defaults to 200 — a `FireBurnZone`'s own
  polling cost is much lower than a full particle system, so a much higher
  cap here is fine). A cell can now have a damage-only invisible zone even
  when it didn't get a visual slot, so damage coverage no longer depends on
  the rendering budget at all. Tune with `fireset grounddamagemax`.
- **Fire damage — real fix in 0.12.1.** 0.9.0/0.9.1 called
  `Character.AddFireDamage()` alone, which produced a "burning" status
  timer that appeared but never counted down, and no actual damage.
  Consistent explanation: `AddFireDamage` queues damage into `SE_Burning`'s
  internal pool but doesn't itself attach/refresh a running status-effect
  instance to process that queue — nothing was actually ticking. Fixed by
  also explicitly calling `SEMan.AddStatusEffect()` every tick (via
  `Character.GetSEMan()`), using `SEMan.s_statusEffectBurning` — the exact
  reference vanilla itself uses, not a guessed hash — continuously
  refreshing the real burning status effect for as long as a character
  stays in a fire zone. The field's actual runtime type (int hash vs a
  `StatusEffect` object reference) is checked dynamically so the correct
  `AddStatusEffect` overload gets called either way, rather than guessing
  which one at compile time.
- **Fire actually hurts — corrected again in 0.9.1.** 0.9.0's `FireBurnZone`
  relied on Unity's `OnTriggerStay`/`OnTriggerExit` events via an attached
  trigger `SphereCollider`. Tested with `firedebug` on through a large,
  sustained fire (ground fire held at its 200-cell cap) — zero `Fire damage
  tick` log lines the entire session, and zero exceptions. That silence
  points at physics layers: our dynamically-created GameObjects sit on
  Unity's default layer, and Valheim's collision matrix most likely blocks
  that layer from generating trigger callbacks against characters at all —
  no error, `OnTriggerStay` just never gets called. Fixed by switching to
  explicit `Physics.OverlapSphereNonAlloc` polling (a few times per tick
  interval) using `EffectArea.s_characterMask` — the exact layer mask
  vanilla's own character-detection already uses, reflected out rather than
  guessed. Manual physics queries like `OverlapSphere` only care about the
  `LayerMask` parameter passed in; they ignore the pairwise collision matrix
  entirely, sidestepping whatever blocked the trigger events. No
  collider/trigger setup needed anymore at all — just a radius to poll.
- **Kill throttle (0.4.2):** the same `ZNetScene.RemoveObjects`
  `NullReferenceException` flood from the earlier tree-removal bug briefly
  reappeared during the 0.4.0 ground-spread explosion — this time from
  *batch size*, not a wrong API: killing ~15+ trees/pieces in one tight burst
  within a single frame (via the correct `ZNetView.Destroy()`) apparently
  raced with `ZNetScene`'s own per-frame object bookkeeping. `MaxKillsPerCycle`
  (default 5) now throttles how many real objects get destroyed per cycle —
  anything past the limit just stays alive past its nominal expiry and gets
  caught on a later cycle instead of all at once. Ground-spread cells (no
  real object involved) aren't affected by this limit.

## File map

```
Plugin.cs                          BepInEx entry, Harmony PatchAll
Config/FireConfig.cs               All config bindings
Fire/FireManager.cs                Burn timers, cap, spread, queue promotion
Fire/FireQueue.cs                  Bounded FIFO with dedupe
Patches/WearNTearRpcDamagePatch.cs Ignition trigger (fire damage -> TryIgnite)
Patches/WearNTearOnDestroyPatch.cs State cleanup on piece removal
Patches/TerminalInitPatch.cs       Command registration hook
Commands/FireDevCommands.cs        Console commands
Utils/ValheimBridge.cs             ONLY non-patch file touching vanilla API
Utils/FireLogger.cs                Logging (verbose gated by config)
```

## Valheim 1.0 future-proofing

All vanilla signatures live in `Patches/` and `Utils/ValheimBridge.cs`. After
1.0 lands (Sept 9, 2026), re-run net_meta.py on the new publicized DLLs and fix
drift in those files only. Verified July 2026 against
`assembly_valheim_publicized.dll`:

- `WearNTear`: `s_allInstances`, `m_burnable`, `m_nview`, `m_piece`,
  `RPC_Damage(long, HitData)`, `OnDestroy()`, `Destroy(HitData, bool)`
- `HitData.m_damage.m_fire`
- `Terminal.InitTerminal()`, `Terminal.ConsoleCommand`, `AddString(text)`
- `GameCamera.m_instance.m_camera`, `Player.m_localPlayer`

## Console commands

| Command | Effect |
|---|---|
| `ignite` | Ignite piece under crosshair |
| `startfire [radius]` | Ignite all burnable pieces within radius of player (default 5m) |
| `stopfire` | Extinguish piece under crosshair |
| `clearfires` | Extinguish everything, empty queue |
| `firestatus` | Counts + live config |
| `firedebug` | Toggle verbose logging |
| `fireset <key> <value>` | Live setters: burnduration, spreadradius, maxburning, queuesize, spreadinterval, trees, vfx, procedural, groundenabled, groundcellsize, groundradius, groundburnduration, groundmax, groundvfxmax, grounddamagemax, firehurts, firehurtsplayeronly, firehurtsradius, firedamage, firetickinterval, extinguishradius, rainsuppress, rainmultiplier, scorchmarks, scorchlifetime, dirtpaint, dirtpaintradius, rampenabled, rampduration, rampstart, exhaustionenabled, fuelregrow, windbias, windupwindchance, windinfluence, firebreaks, treeregrowth, treeregrowthseconds, groundleashenabled, groundleashdistance, enabled |

**In-game controls:** press `G` (rebindable in the BepInEx config file, `ExtinguishKey`) to extinguish the burning object under your crosshair plus any ground fire within `ExtinguishGroundRadius` of you.
| `firelistprefabs [filter]` | List registered prefab names containing filter (default `fire`) — use to find the exact vanilla fire VFX prefab name before visuals get wired in |
| `firepurgevfx` | Emergency cleanup — destroys every live instance of vanilla's own `Fire` gameplay class in the scene (run this once if visuals ever leak) |
| `firecheckprefab <name>` | Inspect a prefab's components without instantiating it — tells you if it's safe to use as vfx |
| `firegroundignite [radius]` | Seed ground-fire cells around the player, for testing ground spread directly |

## Build

1. Drop reference DLLs into `libs\` (BepInEx.dll, 0Harmony.dll, publicized
   assemblies, UnityEngine + CoreModule + PhysicsModule) or fix HintPaths.
2. Build → copy `FireFront.dll` to `BepInEx/plugins/`.

## First test plan

1. `firestatus` — confirm load + config values.
2. Build a small wood shack, `ignite` one wall — confirm 30s later it's destroyed.
3. Confirm spread to adjacent walls within radius.
4. `startfire 10` on a bigger build — watch cap hold at 25 and queue fill.
5. `clearfires` — confirm total stop.
6. Fire arrow a wall — confirm vanilla ignition path works.

## Known limitations (v0.3.2, by design)

- **0.3.2 fixed a serious bug present in 0.2.3–0.3.1**: tree removal called
  `ZNetScene.Destroy(gameObject)` directly, which doesn't properly deregister
  the object from ZNetScene's internal tracking lists — this corrupted
  vanilla's own per-frame object lifecycle system (`ZNetScene.Update()` →
  `CreateDestroyObjects()` → `RemoveObjects()`), producing an infinite flood
  of `NullReferenceException` every frame once any tree burned down. Fixed by
  calling `ZNetView.Destroy()` instead — the tree's own proper self-removal
  method, the same one WearNTear/TreeLog use internally, which correctly
  deregisters everything. **If you saw this exception flood before updating,
  save and reload the world/session after installing 0.3.2** — the fix stops
  it from recurring, but doesn't retroactively repair state already
  corrupted in the running session.
- ~~Trees are hard-removed (no `SpawnLog` felling animation, no log drop)~~ —
  **superseded in 0.7.x**: `KillBurningTarget` now calls vanilla's own
  `TreeBase.SpawnLog` first (real felling, real drops), falling back to
  `ZNetView.Destroy()` only if the tree somehow survives it. Felled logs
  drop too — `TreeLog.Destroy(null)` is vanilla's own destroy and spawns
  the real `m_dropWhenDestroyed` list (verified against the decompiled
  body). The original blocker stands, though: vanilla trees resist fire
  damage almost entirely, so burn-down still can't happen "naturally"
  through the damage system — FireFront's own burn timer does the killing.
- Visuals: see the corrected write-up in Scope above (0.5.0) — vanilla
  prefab spawning was fundamentally unsafe for anything with a ZNetView
  (which is nearly all fire-related prefabs), so the safe default is the
  procedural particle effect (`fireset procedural true`), not a vanilla name.
- Single-authority model: FireManager runs where the mod runs and claims
  ownership to kill the target. Matches the single-server setup.
- Piece names log as raw `$piece_*` tokens (Localization TODO, same as
  SteveCompanion). Tree/log names just fall back to the raw GameObject name.
- Trees/logs have no vanilla-maintained instance list like `WearNTear` does,
  so they're found via a live `FindObjectsOfType` scan each spread cycle,
  scoped to whatever's currently loaded. Watch performance if you drop
  `spreadinterval` very low in a dense forest with trees enabled.
- No `OnDestroy` cleanup patch for trees/logs (only pieces have one) — if a
  burning tree is chopped down by other means, FireFront's state catches up
  via the next cycle's stale-prune check rather than instantly. Harmless,
  just up to `SpreadCheckInterval` seconds of lag before the slot frees up.
