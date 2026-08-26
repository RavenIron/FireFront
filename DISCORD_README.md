🔥 **FireFront — Tester Build v0.7.2**

Fire that actually spreads. Torch a wall and it can take the whole build with it, jump to the treeline, and crawl across open ground to get there — not just "this one piece is on fire," an actual moving front.

**Requires:** BepInEx (you already have this if you're testing Valheim mods)

**Install:** drop `FireFront.dll` into `BepInEx/plugins/`, launch, done. No config needed to just play — everything below is for people who want to poke at it.

—

**What it does**
• Structures, standing trees, and felled logs can all catch fire and burn down for real
• Fire spreads to nearby burnable stuff, structure-to-structure, tree-to-tree, or structure-to-tree
• Fire also spreads across open ground/grass between things that are too far apart to ignite each other directly — it's not just object-to-object, it actually travels, and you can now *see* it crawling through grass as small ember flickers
• Burning structures and standing trees now leave real drops behind (trees drop actual logs/wood via vanilla's own felling — this is fresh, tell us if it's not dropping anything yet)
• Fire effects (custom-built, not vanilla assets) show on anything currently burning, plus lighter ember flickers on ground fire
• Everything's config-tunable and mostly adjustable live via console command — spread distance, speed, how much can burn at once, whether trees are included, queue size, all of it

**What to expect / known limits**
• Felled logs (already-on-the-ground logs, not standing trees) still just vanish when they burn — no drop yet
• The fire visual is homemade, not a vanilla asset — it'll look like fire, just not *exactly* like Valheim's own fire
• Standing in ground fire doesn't hurt you (yet) — that's actively being worked on
• Default settings on this build are tuned aggressive for testing — expect fire to spread fast and hungrily unless you dial it down yourself

—

**Useful console commands** (` key to open console)
```
firestatus              — see what's currently burning + all current settings
ignite                  — ignite whatever's under your crosshair
startfire [radius]      — ignite everything burnable within radius of you
stopfire                — extinguish whatever's under your crosshair
clearfires              — nuke every active fire instantly
firedebug                — toggle verbose fire logging
fireset <key> <value>   — live-tune settings, no restart needed
```
Full list of tunable `fireset` keys: `burnduration`, `spreadradius`, `maxburning`, `queuesize`, `spreadinterval`, `trees`, `vfx`, `procedural`, `groundenabled`, `groundcellsize`, `groundradius`, `groundburnduration`, `groundmax`, `groundvfxmax`, `enabled`

There are a few more diagnostic-only commands (`firelistprefabs`, `firecheckprefab`, `firepurgevfx`, `firegroundignite`) mostly meant for dev-side debugging — ask in here if you're curious what they do.

—

**If something breaks**
Please grab your `LogOutput.log` (BepInEx console or `%appdata%LocalLow\IronGate\Valheim\`) and send it over, especially if you see a wall of red repeating errors. Screenshots of weird spread behavior are also genuinely useful — "this jumped way further than it should have" is easier to diagnose with a picture than a description.

Thanks for testing — this thing has had a real rough-and-tumble development process (ask if you want the story), so any weirdness you catch now saves everyone a headache later.
