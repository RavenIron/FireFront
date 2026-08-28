# FireFront — session handoff (2026-08-28, updated same day)

Resume point for the next working session. Read this before touching anything;
the memory notes in the assistant's store point here.

## Where everything stands

- **Repo**: `main` at **0.19.2** (regrowth dedupe — see below). 0.19.1
  (tag `v0.19.1`) is pushed to github.com/RavenIron/FireFront.
- **Server AND client are both at 0.19.2 on disk** (deployed 2026-08-28 with
  both processes stopped, verified by version strings in the copied DLLs —
  not yet booted, so no log line has confirmed it loading). Client is the
  Gale profile **`raveniron`** — NOT `Default`; the profile also carries
  Ragnarok's Wrath and Server Devcommands.
- **Testers**: still on the **0.18.0** zip from the original Discord post.
  `dist\RavenIron-FireFront-0.19.2.zip` is built and version-guard-checked.
  **Owner's call 2026-08-28: no Discord post needed** — the regenerated
  split in `dist\DISCORD_POST_READY.txt` exists but is not to be shipped
  unless the owner asks.

## In flight — finish these first

1. **Relay verification, 3 of 5 commands outstanding.** The 0.19.0 generic
   command relay tested green for `firestatus` and `firetreeregrowlist`
   (multi-line replies confirmed end-to-end). `firegroundignite`, `startfire`,
   `clearfires` were blocked by the client-side "Admin only." gate — root
   cause: vanilla's client-side `PlayerIsAdmin` does an exact-string match, and
   a crossplay Steam id stringifies as `Steam_7656...` which never equals the
   bare adminlist entry. Fixed in 0.19.1 by relaying BEFORE the local gate
   (the server's `PeerIsAdmin` is the real, prefix-tolerant authorization).
   **Next session: have the user relaunch (client now has 0.19.2) and rerun
   those three; watch for `[RELAY]` lines in the server log and `[server]`
   replies in the client log.** Optional user one-liner that also fixes
   vanilla's own check: append `Steam_76561198392625778` to
   `C:\Users\donfr\AppData\LocalLow\IronGate\Valheim\adminlist.txt` (the
   assistant is permission-blocked from that file).
2. ~~**Duplicate tree-regrowth entries.**~~ **DONE in 0.19.2 (2026-08-28):**
   position-keyed dedupe (`EnqueueRegrowth`, 0.5m radius, existing entry wins —
   its attempt count is real history) guards BOTH enqueue sites in
   `Fire/FireManager.cs` (burn-down and sidecar restore; a pre-fix sidecar can
   itself hold duplicates, and a deduped restore entry now counts as skipped,
   not restored). Builds clean; in-game verification pending — rerun
   `firetreeregrowlist` after the next burn and look for the
   `Regrowth dedupe:` debug line or simply no double entries.
3. **Ship to testers** — ON HOLD, owner said no Discord post needed
   (2026-08-28). The 0.19.2 zip stays ready in dist\ if that changes.

## Operational facts that cost real time — do not relearn

- **Client deploys go through Gale, never hand-copies to
  `plugins\FireFront\`.** A hand-copied folder next to Gale's managed
  `RavenIronStudios-FireFront\` folder means two DLLs with one GUID and
  BepInEx loads whichever it finds first — this caused days of "wrong version
  loaded" chaos. Correct paths: Gale cache
  `%APPDATA%\com.kesomannen.gale\cache\RavenIronStudios-FireFront\<ver>\` and
  profile `...\profiles\raveniron\BepInEx\plugins\RavenIronStudios-FireFront\`.
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
