# Changelog

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
