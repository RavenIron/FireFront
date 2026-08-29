# FireFront — session handoff (updated 2026-08-29)

Resume point for the next working session. Read this before touching anything;
the memory notes in the assistant's store point here.

## Where everything stands

- **Repo**: `main` at **0.19.8**, pushed to github.com/RavenIron/FireFront,
  every version tagged through `v0.19.8`.
- **Deployed builds are MIXED as of 2026-08-29** — dedicated server on
  0.19.7, `Default` profile on 0.19.8, `raveniron` on an unaccounted-for
  hash. The game and server were running and locked their DLLs when 0.19.8
  went out. Redeploy and hash-verify before drawing any conclusion from a
  log. See the profile note below — the owner plays `Default`.
- **A "version string" grep of the DLL is NOT a version check.** FireFront's
  own log messages contain literals like `0.17.2` and `0.18.7`, so scanning a
  DLL for version-shaped strings returns a list, not an answer, and the
  newest entry is not necessarily the build. Hash against
  `bin\Release\net472\FireFront.dll`, or read the boot log line.
- **Testers**: at least one is on **0.19.3** (see item 4 — the log proved it),
  others may still be on the **0.18.0** Discord zip.
  `dist\RavenIron-FireFront-0.19.8.zip` is built and version-guard-checked,
  but do NOT ship it before item 4's burn test.
  **Owner's call 2026-08-28: no Discord post needed** — the regenerated
  split in `dist\DISCORD_POST_READY.txt` exists but is not to be shipped
  unless the owner asks.

## In flight — finish these first

1. ~~**Relay verification, 3 of 5 commands outstanding.**~~ **DONE 2026-08-28
   — ALL FIVE RELAYABLE COMMANDS VERIFIED END-TO-END at 0.19.2 both sides.**
   `firestatus` and `firetreeregrowlist` were green on 0.19.0;
   `firegroundignite`, `startfire 10`, `clearfires` verified live today: zero
   "Admin only." refusals, `[RELAY] <cmd> from peer -428794145` on the server
   for each, and the matching `[server] ...` reply in the client log
   (`Seeded ground fire...`, `startfire: attempted 0 targets within 10m`,
   and `All fires cleared.` after clearing 8 entries). That "0 targets" was
   NOT harmless staging ground — it exposed a real bug, fixed in 0.19.3:
   startfire's target scan was still instance-only (AllPieces +
   FindObjectsOfType), which see nothing headless — the 0.17.4 root cause,
   never converted for this command. It now also sweeps the ZDO layer via
   `FireManager.IgniteBurnablesNear` (own scratch list — never clobbers the
   spread cycle's `_zdoCandidates` cache). **VERIFIED LIVE same day after a
   server bounce (0.19.3 both sides, load lines confirmed):** relayed
   `startfire 10` near real trees answered `attempted 3 targets within 10m`
   and the next heartbeat showed `burning 3/50` — three real fires on the
   headless server where the identical command had found zero. The
   doubled startfire in the server log was the owner running it twice (two
   separate client "sent to server" lines), not a double-send. The 0.19.1
   relay-before-local-gate fix is proven. Historical root cause kept for the
   record: vanilla's client-side `PlayerIsAdmin` exact-string match never
   matches a crossplay `Steam_7656...` id against a bare adminlist entry;
   the optional adminlist append (`Steam_76561198392625778`) remains a
   nice-to-have for vanilla's own commands, nothing of ours needs it.
2. ~~**Duplicate tree-regrowth entries.**~~ **DONE in 0.19.2 (2026-08-28):**
   position-keyed dedupe (`EnqueueRegrowth`, 0.5m radius, existing entry wins —
   its attempt count is real history) guards BOTH enqueue sites in
   `Fire/FireManager.cs` (burn-down and sidecar restore; a pre-fix sidecar can
   itself hold duplicates, and a deduped restore entry now counts as skipped,
   not restored). Builds clean; in-game verification pending — rerun
   `firetreeregrowlist` after the next burn and look for the
   `Regrowth dedupe:` debug line or simply no double entries.
3. **Ship to testers** — ON HOLD, owner said no Discord post needed
   (2026-08-28). The zip stays ready in dist\ if that changes.

4. **The tester frametime spike — DIAGNOSED, fix built, ONE TEST OUTSTANDING.**
   Resolved 2026-08-29 from the tester's own Player.log. Facts, replacing the
   earlier guesswork:
   - They run **0.19.3, hosting** (`IsServer=True`) — NOT the 0.18.0 Discord
     zip. The earlier "they're just on an old build" theory was WRONG; they
     already had every prior performance fix.
   - Their log: `total candidates (pieces+trees+logs)=2176` (only 131 of them
     pieces), `burning 50/50, queued 20/20, ground 46/50`. `SpreadPass`
     compared every candidate to every burner each 0.75s cycle — about a
     quarter of a million distance checks per cycle, each with a type
     dispatch and a ZDO lookup. Their measured spikes: 101.9-146.3ms. The
     arithmetic lands exactly on the symptom.
   - **0.19.8 is the fix**: a 16m spatial grid over both candidate lists, so a
     burner queries only the cells its reach touches. 0.19.5 had already cut
     ~5x of it by moving trees out of the instance list.
   **OUTSTANDING: 0.19.8 has never run in a game.** It is pushed, tagged and
   packaged, and it is the only thing shipped that day without live
   verification — and the riskiest, since it rewrites how spread finds
   targets. A mistake shows up as fire that burns but never spreads, which no
   compiler or packaging guard catches. DO NOT hand that zip to a tester
   before one burn: `startfire 10` near real trees, confirm the front MOVES
   and `zdoCandidates` stays non-zero. Owner deferred this test to a later
   session (they were mid-session on their own server).
   Also pending: a clean redeploy. As of that session the dedicated server was
   on 0.19.7, the `Default` profile on 0.19.8, and `raveniron` on an
   unaccounted-for hash — the game and server were running and locked their
   DLLs. Hash-verify against `bin\Release\net472\FireFront.dll` after copying.

## Operational facts that cost real time — do not relearn

- **The owner plays on the `Default` Gale profile, NOT `raveniron`.**
  Corrected 2026-08-29: an earlier note here said `raveniron`, and a whole
  session's client deploys went to the wrong profile before the mistake
  showed up (it was masked because every profile ended up byte-identical
  anyway — SHA256-checked). `Default` is the one whose config file gets
  written during play; it carries FireFront, Undertow, LetItGrow, Jotunn and
  ConfigurationManager. `raveniron` (FireFront + Ragnarok's Wrath + Server
  Devcommands) matches the dedicated server's plugin set. When in doubt, the
  profile whose `BepInEx\config\com.raveniron.firefront.cfg` has the newest
  mtime is the one that was just played.
- **Client deploys go through Gale, never hand-copies to
  `plugins\FireFront\`.** A hand-copied folder next to Gale's managed
  `RavenIronStudios-FireFront\` folder means two DLLs with one GUID and
  BepInEx loads whichever it finds first — this caused days of "wrong version
  loaded" chaos. Correct paths: Gale cache
  `%APPDATA%\com.kesomannen.gale\cache\RavenIronStudios-FireFront\<ver>\` and
  profile `...\profiles\<profile>\BepInEx\plugins\RavenIronStudios-FireFront\`.
  Gale's enable/disable state lives in its SQLite db (`data.sqlite3`) — don't
  edit it; worst case the user clicks enable in Gale's UI. `.old` suffixes on
  files = Gale's "disabled" convention.
- **Deploy ritual**: build Release → stop server (`Stop-Process`) → copy DLL
  to server + Gale profile (profile copy needs the game CLOSED; arm an
  until-loop background copy if it isn't) → restart server → verify by log
  strings ("All 8 FireFront RPCs", version line), never by trusting the copy.
- **Console commands run where typed.** `fireset`/`firestatus` and the five
  relayables forward to the server themselves since 0.18.3/0.18.8/0.19.0;
  `ignite`/`stopfire` forward by ZDOID (crosshair is local). Anything new that
  touches server state joins the `_relayable` whitelist in
  `Commands/FireDevCommands.cs` and inherits relay + server-side auth.
- **Server log**: `AppendLog = true` (accumulates across launches — slice from
  the LAST "Preloader started"). BepInEx header timestamps are unreliable;
  date by Unity log lines or mtime. Heartbeat every 15s while fire burns is
  the server's pulse; debug logging (`DebugLogging`, renamed from
  VerboseLogging in 0.18.7) is OFF by default and free when off.
- **The publicized DLL lies about visibility.** Compile-time public ≠ runtime
  public (`ObjectDB.m_itemByHash`, `ZNet` members). Reflect vanilla members,
  verify shapes with `ilspycmd` against `libs\assembly_valheim_publicized.dll`,
  and read the decompiled BODY, not the signature.
- **ffmpeg** is installed (winget, Gyan.FFmpeg) for analyzing tester clips;
  the Linux tester runs MangoHud under Proton — frametime-graph clips are
  gold, ask for them.

## Architecture landmarks from this arc (0.17.2 → 0.19.1)

Wind-strength-scaled spread; igniter attribution; ZDO-layer spread candidates
(instance scans see NOTHING headless — proven) with a 5s candidate cache;
Dousing Bomb (BombOoze clone, no Jotunn); fire persistence sidecar next to the
world save (remaining-seconds encoding, `_restoredRampAge` for the −1
sentinel); reconnect re-registration (ZRoutedRpc is per-connection — guard by
instance reference); BurnPlayerBuildings (creator-stamp gate, ZDO-readable);
douse immunity (extinguished = soaked 90s); spread maturity (front pace tied
to burn time — user-confirmed tuned); allocation-free debug logging
(interpolated-string handler, net472 attribute polyfill); generic command
relay (whitelist + server-side `PeerIsAdmin` + peer refPos standing in for
"local player").

All of it field-verified on the live dedicated server; the changelog carries
the evidence per version.
