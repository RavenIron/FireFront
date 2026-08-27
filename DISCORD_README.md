🔥 **FireFront — Tester Build v0.17.3**

Fire that actually spreads. Torch a wall and it can take the whole build with it, jump to the treeline, and crawl across open ground to get there — not just "this one piece is on fire," an actual moving front.

**Requires:** BepInEx (you already have this if you're testing Valheim mods)

**Install:** drop `FireFront.dll` into `BepInEx/plugins/`, launch, done. No config needed to just play — everything below is for people who want to poke at it. On a dedicated server, the server needs the dll too (it runs the actual fire; your client just shows it to you).

—

**What it does**
• Structures, standing trees, and felled logs can all catch fire and burn down for real — with real drops (trees fell via vanilla's own felling and leave logs, logs leave wood)
• Fire spreads to nearby burnable stuff, structure-to-structure, tree-to-tree, or structure-to-tree
• Fire also spreads across open ground/grass between things too far apart to ignite each other directly — you can *see* it crawling through grass as small ember flickers
• Standing in fire hurts, players and mobs alike — same mechanism vanilla campfires use
• **New: fire follows the wind.** The front stretches downwind and thins out upwind, using the game's real wind — and how *hard* it's blowing matters now too. A gale drives a long narrow tongue of fire; a calm day burns in a lazy, even circle. Watch a fire when the weather turns.
• Fires start small and ramp up over ~10 minutes instead of instantly raging
• Rain douses grass fire (an already-burning building keeps going)
• Burned ground is spent — it can't relight for ~90s, so the front *advances* instead of churning in place, and it leaves burn scars behind
• Dirt paths and cultivated ground are real firebreaks; water stops spread too
• Ground fire won't wander more than ~40m from where the fire started
• Burned trees regrow after ~15 minutes if the spot's still clear
• Press **G** to extinguish what you're looking at plus ground fire around you
• Fires now remember who lit them — the whole spreading front carries its arsonist, even fire that crawled a long way from the first spark (natural/creature fire belongs to nobody). Nothing visible in-game yet; it feeds a companion mod's reputation system
• Everything's config-tunable and adjustable live via console — no restart needed

**Heads-up if updating from an older build:** update server and client together — a mixed-version pair (0.17.3 with anything older) means clicking `ignite` from a client silently does nothing.

**What to expect / known limits**
• The fire visual is homemade, not a vanilla asset — it'll look like fire, just not *exactly* like Valheim's own fire
• Tree regrowth is in-memory only — trees mid-regrow when the server restarts won't come back
• Default settings are tuned aggressive for testing — expect fire to spread fast and hungrily unless you dial it down yourself

—

**Useful console commands** (` key to open console)
```
firestatus              — see what's currently burning + all current settings
ignite                  — ignite whatever's under your crosshair
startfire [radius]      — ignite everything burnable within radius of you
stopfire                — extinguish whatever's under your crosshair
clearfires              — nuke every active fire instantly
firedebug               — toggle verbose fire logging
fireset <key> <value>   — live-tune settings, no restart needed
```
Full list of tunable `fireset` keys: `burnduration`, `spreadradius`, `maxburning`, `queuesize`, `spreadinterval`, `trees`, `vfx`, `procedural`, `groundenabled`, `groundcellsize`, `groundradius`, `groundburnduration`, `groundmax`, `groundvfxmax`, `grounddamagemax`, `firehurts`, `firehurtsplayeronly`, `firehurtsradius`, `firedamage`, `firetickinterval`, `extinguishradius`, `rainsuppress`, `rainmultiplier`, `scorchmarks`, `scorchlifetime`, `dirtpaint`, `dirtpaintradius`, `rampenabled`, `rampduration`, `rampstart`, `exhaustionenabled`, `fuelregrow`, `windbias`, `windupwindchance`, `windinfluence`, `firebreaks`, `treeregrowth`, `treeregrowthseconds`, `groundleashenabled`, `groundleashdistance`, `enabled`

Wind ones worth playing with: `fireset windinfluence 0` ignores wind entirely (old-style even spread), `1` is full effect (the default). `firestatus` shows the live wind strength the fire is currently feeling.

There are a few more diagnostic-only commands (`firelistprefabs`, `firecheckprefab`, `firepurgevfx`, `firegroundignite`, `firetreeregrow`, `firetreeregrowlist`) mostly meant for dev-side debugging — ask in here if you're curious what they do.

—

**If something breaks**
Please grab your `LogOutput.log` (BepInEx folder) and send it over, especially if you see a wall of red repeating errors. Screenshots of weird spread behavior are also genuinely useful — "this jumped way further than it should have" is easier to diagnose with a picture than a description. For wind specifically: a screenshot of a burn scar plus which way the wind was blowing is exactly the evidence we need.

Thanks for testing — this thing has had a real rough-and-tumble development process (ask if you want the story), so any weirdness you catch now saves everyone a headache later.
