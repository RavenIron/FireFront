using FireFront.Config;
using FireFront.Fire;
using FireFront.Utils;
using UnityEngine;

namespace FireFront.Commands
{
    /// <summary>
    /// Console commands:
    ///   ignite              - ignite the piece/tree/log under the crosshair
    ///   startfire [r]       - ignite everything burnable within r meters of the player (default 5)
    ///   stopfire            - extinguish the target under the crosshair
    ///   clearfires          - extinguish everything and empty the queue
    ///   firestatus          - print active/queued counts and current config
    ///   firedebug           - toggle verbose logging
    ///   fireset k v         - live config setter
    ///   firelistprefabs [f] - list registered prefab names containing filter (default "fire")
    ///   firetreeregrow      - force all pending tree regrowth to attempt now (skip the timer)
    ///   firetreeregrowlist  - list pending tree regrowth entries (prefab, position, time left, attempts)
    /// </summary>
    public static class FireDevCommands
    {
        public static void RegisterAll()
        {
            new Terminal.ConsoleCommand("ignite",
                "FireFront: ignite the piece/tree/log under the crosshair",
                args => Ignite(args));

            new Terminal.ConsoleCommand("startfire",
                "FireFront: startfire [radius] - ignite burnable things around the player",
                args => StartFire(args));

            new Terminal.ConsoleCommand("stopfire",
                "FireFront: extinguish the target under the crosshair",
                args => StopFire(args));

            new Terminal.ConsoleCommand("clearfires",
                "FireFront: extinguish all fires and clear the queue",
                args => ClearFires(args));

            new Terminal.ConsoleCommand("firestatus",
                "FireFront: show burning/queue counts and config",
                args => FireStatus(args));

            new Terminal.ConsoleCommand("firedebug",
                "FireFront: toggle verbose logging",
                args => FireDebug(args));

            new Terminal.ConsoleCommand("fireset",
                "FireFront: fireset <burnduration|spreadradius|maxburning|queuesize|spreadinterval|trees|vfx|procedural|groundenabled|groundcellsize|groundradius|groundburnduration|groundmax|groundvfxmax|grounddamagemax|firehurts|firehurtsplayeronly|firehurtsradius|firedamage|firetickinterval|extinguishradius|rainsuppress|rainmultiplier|scorchmarks|scorchlifetime|dirtpaint|dirtpaintradius|rampenabled|rampduration|rampstart|exhaustionenabled|fuelregrow|windbias|windupwindchance|windinfluence|dousingradius|persistfires|firebreaks|treeregrowth|treeregrowthseconds|groundleashenabled|groundleashdistance|enabled> <value>",
                args => FireSet(args));

            new Terminal.ConsoleCommand("firelistprefabs",
                "FireFront: firelistprefabs [filter] - list prefab names containing filter (default 'fire')",
                args => FireListPrefabs(args));

            new Terminal.ConsoleCommand("firepurgevfx",
                "FireFront: emergency cleanup - destroys every live vanilla Fire-class instance in the scene",
                args => FirePurgeVfx(args));

            new Terminal.ConsoleCommand("firecheckprefab",
                "FireFront: firecheckprefab <name> - inspect a prefab's components WITHOUT spawning it, to check if it's safe for vfx",
                args => FireCheckPrefab(args));

            new Terminal.ConsoleCommand("firegroundignite",
                "FireFront: firegroundignite [radius] - seed ground-fire cells around the player (default GroundSpreadRadius), for testing ground spread without needing a tree/piece",
                args => FireGroundIgnite(args));

            new Terminal.ConsoleCommand("fireinspecteffectarea",
                "FireFront: fireinspecteffectarea [maxdistance] - reads real field values off the nearest EffectArea (e.g. a campfire's burn zone), to verify the correct 'Burning' type before we configure our own",
                args => FireInspectEffectArea(args));

            new Terminal.ConsoleCommand("firetreeregrow",
                "FireFront: force every pending tree-regrowth entry to attempt right now instead of waiting out its timer",
                args => FireTreeRegrow(args));

            new Terminal.ConsoleCommand("firetreeregrowlist",
                "FireFront: list pending tree-regrowth entries (prefab, position, time left, attempts) — use to check for duplicates",
                args => FireTreeRegrowList(args));

            FireLogger.Info("Dev commands registered.");
        }

        // ---------------------------------------------------------------

        private static void Ignite(Terminal.ConsoleEventArgs args)
        {
            if (!RequireAdmin(args)) return;

            Component target = ValheimBridge.RaycastBurnable();
            if (target == null) { Say(args, "No burnable target under crosshair."); return; }
            if (!ValheimBridge.IsBurnable(target)) { Say(args, $"Not burnable: {ValheimBridge.NameOf(target)}"); return; }

            if (ValheimBridge.IsServer())
            {
                FireManager.Instance.TryIgnite(target);
                Say(args, FireManager.Instance.IsBurning(target)
                    ? $"Ignited: {ValheimBridge.NameOf(target)}"
                    : $"Queued or dropped (cap full): {ValheimBridge.NameOf(target)}");
            }
            else
            {
                // Calling TryIgnite directly here would populate THIS client's own
                // _burning dict — but Update()'s simulation loop only runs on the
                // server now, so that fire would ignite visually and then never
                // expire, while its StartBurning broadcast tells every other peer
                // a fire started that the server has no record of. Route through
                // the same RPC real ignition already uses instead.
                ZDOID? id = ValheimBridge.ZDOIDOf(target);
                if (id.HasValue)
                {
                    // The typist is the igniter — commands run where they are typed.
                    ValheimBridge.SendIgniteRequestToServer(id.Value, ValheimBridge.LocalPlayerId());
                    Say(args, $"Sent ignite request to server: {ValheimBridge.NameOf(target)}");
                }
                else
                {
                    Say(args, $"Couldn't resolve a ZDOID for {ValheimBridge.NameOf(target)} — can't forward to server.");
                }
            }
        }

        private static void StartFire(Terminal.ConsoleEventArgs args)
        {
            if (!RequireAdmin(args)) return;

            if (!ValheimBridge.IsServer())
            {
                Say(args, "startfire only works run from the server (or client-hosted/single-player) — " +
                          "it ignites many targets at once with no single ZDOID to forward to the server.");
                return;
            }

            Vector3? posOrNull = ValheimBridge.LocalPlayerPosition();
            if (posOrNull == null) { Say(args, "No local player."); return; }
            Vector3 pos = posOrNull.Value;

            float radius = args.TryParameterFloat(1, 5f);
            float radiusSqr = radius * radius;

            int hit = 0;

            var pieces = ValheimBridge.AllPieces;
            for (int i = 0; i < pieces.Count; i++)
                hit += TryIgniteIfInRange(pieces[i], pos, radiusSqr);

            if (FireConfig.BurnTreesAndLogs.Value)
            {
                var trees = Object.FindObjectsOfType<TreeBase>();
                for (int i = 0; i < trees.Length; i++)
                    hit += TryIgniteIfInRange(trees[i], pos, radiusSqr);

                var logs = Object.FindObjectsOfType<TreeLog>();
                for (int i = 0; i < logs.Length; i++)
                    hit += TryIgniteIfInRange(logs[i], pos, radiusSqr);
            }

            Say(args, $"startfire: attempted {hit} targets within {radius}m. {FireManager.Instance.StatusLine()}");
        }

        private static int TryIgniteIfInRange(Component target, Vector3 pos, float radiusSqr)
        {
            if (!ValheimBridge.IsBurnable(target)) return 0;
            if ((ValheimBridge.PositionOf(target) - pos).sqrMagnitude > radiusSqr) return 0;
            FireManager.Instance.TryIgnite(target);
            return 1;
        }

        private static void StopFire(Terminal.ConsoleEventArgs args)
        {
            if (!RequireAdmin(args)) return;

            Component target = ValheimBridge.RaycastBurnable();
            if (target == null) { Say(args, "No target under crosshair."); return; }

            if (ValheimBridge.IsServer())
            {
                FireManager.Instance.Extinguish(target);
                Say(args, $"Extinguished: {ValheimBridge.NameOf(target)}");
            }
            else
            {
                ZDOID? id = ValheimBridge.ZDOIDOf(target);
                Vector3? posOrNull = ValheimBridge.LocalPlayerPosition();
                if (id.HasValue && posOrNull.HasValue)
                {
                    // radius 0 — only extinguish the targeted object, don't also
                    // clear ground fire near the player (that's what the real
                    // extinguish key does; this command targets one thing).
                    ValheimBridge.SendExtinguishRequestToServer(id.Value, posOrNull.Value, 0f);
                    Say(args, $"Sent extinguish request to server: {ValheimBridge.NameOf(target)}");
                }
                else
                {
                    Say(args, $"Couldn't resolve target/position — can't forward to server.");
                }
            }
        }

        private static void ClearFires(Terminal.ConsoleEventArgs args)
        {
            if (!RequireAdmin(args)) return;

            if (!ValheimBridge.IsServer())
            {
                Say(args, "clearfires only works run from the server (or client-hosted/single-player).");
                return;
            }

            FireManager.Instance.ClearAll();
            Say(args, "All fires cleared.");
        }

        private static void FireStatus(Terminal.ConsoleEventArgs args)
        {
            Say(args, FireManager.Instance.StatusLine());
        }

        private static void FireDebug(Terminal.ConsoleEventArgs args)
        {
            FireConfig.VerboseLogging.Value = !FireConfig.VerboseLogging.Value;
            Say(args, $"Verbose logging: {FireConfig.VerboseLogging.Value}");
        }

        private static void FireSet(Terminal.ConsoleEventArgs args)
        {
            if (args.Length < 3)
            {
                Say(args, "Usage: fireset <burnduration|spreadradius|maxburning|queuesize|spreadinterval|trees|vfx|procedural|groundenabled|groundcellsize|groundradius|groundburnduration|groundmax|groundvfxmax|grounddamagemax|firehurts|firehurtsplayeronly|firehurtsradius|firedamage|firetickinterval|extinguishradius|rainsuppress|rainmultiplier|scorchmarks|scorchlifetime|dirtpaint|dirtpaintradius|rampenabled|rampduration|rampstart|exhaustionenabled|fuelregrow|windbias|windupwindchance|windinfluence|dousingradius|persistfires|firebreaks|treeregrowth|treeregrowthseconds|groundleashenabled|groundleashdistance|enabled> <value>");
                return;
            }

            string key = args[1].ToLowerInvariant();
            string raw = args[2];

            switch (key)
            {
                case "burnduration":
                    if (float.TryParse(raw, out float bd)) { FireConfig.BurnDurationSeconds.Value = bd; Ok(args, key, bd); }
                    else Bad(args, raw);
                    break;
                case "spreadradius":
                    if (float.TryParse(raw, out float sr)) { FireConfig.SpreadRadius.Value = sr; Ok(args, key, FireConfig.SpreadRadius.Value); }
                    else Bad(args, raw);
                    break;
                case "maxburning":
                    if (int.TryParse(raw, out int mb)) { FireConfig.MaxConcurrentBurning.Value = mb; Ok(args, key, mb); }
                    else Bad(args, raw);
                    break;
                case "queuesize":
                    if (int.TryParse(raw, out int qs)) { FireConfig.QueueSize.Value = qs; Ok(args, key, FireConfig.QueueSize.Value); }
                    else Bad(args, raw);
                    break;
                case "spreadinterval":
                    if (float.TryParse(raw, out float si)) { FireConfig.SpreadCheckInterval.Value = si; Ok(args, key, si); }
                    else Bad(args, raw);
                    break;
                case "trees":
                    if (bool.TryParse(raw, out bool tr)) { FireConfig.BurnTreesAndLogs.Value = tr; Ok(args, key, tr); }
                    else Bad(args, raw);
                    break;
                case "vfx":
                    FireConfig.VfxPrefabName.Value = raw;
                    Ok(args, key, string.IsNullOrEmpty(raw) ? "(disabled)" : raw);
                    break;
                case "procedural":
                    if (bool.TryParse(raw, out bool pr)) { FireConfig.UseProceduralVfx.Value = pr; Ok(args, key, pr); }
                    else Bad(args, raw);
                    break;
                case "groundenabled":
                    if (bool.TryParse(raw, out bool ge)) { FireConfig.GroundSpreadEnabled.Value = ge; Ok(args, key, ge); }
                    else Bad(args, raw);
                    break;
                case "groundcellsize":
                    if (float.TryParse(raw, out float gcs)) { FireConfig.GroundCellSize.Value = gcs; Ok(args, key, FireConfig.GroundCellSize.Value); }
                    else Bad(args, raw);
                    break;
                case "groundradius":
                    if (float.TryParse(raw, out float gr)) { FireConfig.GroundSpreadRadius.Value = gr; Ok(args, key, FireConfig.GroundSpreadRadius.Value); }
                    else Bad(args, raw);
                    break;
                case "groundburnduration":
                    if (float.TryParse(raw, out float gbd)) { FireConfig.GroundBurnDurationSeconds.Value = gbd; Ok(args, key, gbd); }
                    else Bad(args, raw);
                    break;
                case "groundmax":
                    if (int.TryParse(raw, out int gm)) { FireConfig.GroundMaxConcurrent.Value = gm; Ok(args, key, gm); }
                    else Bad(args, raw);
                    break;
                case "groundvfxmax":
                    if (int.TryParse(raw, out int gvm)) { FireConfig.GroundVfxMaxConcurrent.Value = gvm; Ok(args, key, gvm); }
                    else Bad(args, raw);
                    break;
                case "grounddamagemax":
                    if (int.TryParse(raw, out int gdm)) { FireConfig.GroundDamageMaxConcurrent.Value = gdm; Ok(args, key, gdm); }
                    else Bad(args, raw);
                    break;
                case "firehurts":
                    if (bool.TryParse(raw, out bool fh)) { FireConfig.FireHurtsEnabled.Value = fh; Ok(args, key, fh); }
                    else Bad(args, raw);
                    break;
                case "firehurtsplayeronly":
                    if (bool.TryParse(raw, out bool fhp)) { FireConfig.FireHurtsPlayerOnly.Value = fhp; Ok(args, key, fhp); }
                    else Bad(args, raw);
                    break;
                case "firehurtsradius":
                    if (float.TryParse(raw, out float fhr)) { FireConfig.FireHurtsObjectRadius.Value = fhr; Ok(args, key, FireConfig.FireHurtsObjectRadius.Value); }
                    else Bad(args, raw);
                    break;
                case "firedamage":
                    if (float.TryParse(raw, out float fd)) { FireConfig.FireDamagePerTick.Value = fd; Ok(args, key, FireConfig.FireDamagePerTick.Value); }
                    else Bad(args, raw);
                    break;
                case "firetickinterval":
                    if (float.TryParse(raw, out float fti)) { FireConfig.FireDamageTickInterval.Value = fti; Ok(args, key, FireConfig.FireDamageTickInterval.Value); }
                    else Bad(args, raw);
                    break;
                case "extinguishradius":
                    if (float.TryParse(raw, out float exr)) { FireConfig.ExtinguishGroundRadius.Value = exr; Ok(args, key, FireConfig.ExtinguishGroundRadius.Value); }
                    else Bad(args, raw);
                    break;
                case "rainsuppress":
                    if (bool.TryParse(raw, out bool rs)) { FireConfig.RainSuppressesGroundFire.Value = rs; Ok(args, key, rs); }
                    else Bad(args, raw);
                    break;
                case "rainmultiplier":
                    if (float.TryParse(raw, out float rm)) { FireConfig.RainGroundBurnDurationMultiplier.Value = rm; Ok(args, key, FireConfig.RainGroundBurnDurationMultiplier.Value); }
                    else Bad(args, raw);
                    break;
                case "scorchmarks":
                    if (bool.TryParse(raw, out bool sm)) { FireConfig.ScorchMarksEnabled.Value = sm; Ok(args, key, sm); }
                    else Bad(args, raw);
                    break;
                case "dirtpaint":
                    if (bool.TryParse(raw, out bool dp)) { FireConfig.UseVanillaDirtPaint.Value = dp; Ok(args, key, dp); }
                    else Bad(args, raw);
                    break;
                case "dirtpaintradius":
                    if (float.TryParse(raw, out float dpr)) { FireConfig.DirtPaintRadius.Value = dpr; Ok(args, key, FireConfig.DirtPaintRadius.Value); }
                    else Bad(args, raw);
                    break;
                case "scorchlifetime":
                    if (float.TryParse(raw, out float sl)) { FireConfig.ScorchMarkLifetimeSeconds.Value = sl; Ok(args, key, FireConfig.ScorchMarkLifetimeSeconds.Value); }
                    else Bad(args, raw);
                    break;
                case "rampenabled":
                    if (bool.TryParse(raw, out bool re)) { FireConfig.FireRampEnabled.Value = re; Ok(args, key, re); }
                    else Bad(args, raw);
                    break;
                case "rampduration":
                    if (float.TryParse(raw, out float rd)) { FireConfig.FireRampDurationSeconds.Value = rd; Ok(args, key, FireConfig.FireRampDurationSeconds.Value); }
                    else Bad(args, raw);
                    break;
                case "rampstart":
                    if (float.TryParse(raw, out float rst)) { FireConfig.FireRampStartFraction.Value = rst; Ok(args, key, FireConfig.FireRampStartFraction.Value); }
                    else Bad(args, raw);
                    break;
                case "enabled":
                    if (bool.TryParse(raw, out bool en)) { FireConfig.Enabled.Value = en; Ok(args, key, en); }
                    else Bad(args, raw);
                    break;
                case "exhaustionenabled":
                    if (bool.TryParse(raw, out bool exhen)) { FireConfig.GroundFuelExhaustionEnabled.Value = exhen; Ok(args, key, exhen); }
                    else Bad(args, raw);
                    break;
                case "fuelregrow":
                    if (float.TryParse(raw, out float fregrow)) { FireConfig.GroundFuelRegrowSeconds.Value = fregrow; Ok(args, key, FireConfig.GroundFuelRegrowSeconds.Value); }
                    else Bad(args, raw);
                    break;
                case "windbias":
                    if (bool.TryParse(raw, out bool wb)) { FireConfig.WindSpreadBiasEnabled.Value = wb; Ok(args, key, wb); }
                    else Bad(args, raw);
                    break;
                case "windupwindchance":
                    if (float.TryParse(raw, out float wuc)) { FireConfig.WindUpwindIgniteChance.Value = wuc; Ok(args, key, FireConfig.WindUpwindIgniteChance.Value); }
                    else Bad(args, raw);
                    break;
                case "windinfluence":
                    if (float.TryParse(raw, out float wi)) { FireConfig.WindInfluence.Value = wi; Ok(args, key, FireConfig.WindInfluence.Value); }
                    else Bad(args, raw);
                    break;
                case "dousingradius":
                    if (float.TryParse(raw, out float dbr)) { FireConfig.DousingBombRadius.Value = dbr; Ok(args, key, FireConfig.DousingBombRadius.Value); }
                    else Bad(args, raw);
                    break;
                case "persistfires":
                    if (bool.TryParse(raw, out bool pf)) { FireConfig.PersistFiresEnabled.Value = pf; Ok(args, key, pf); }
                    else Bad(args, raw);
                    break;
                case "firebreaks":
                    if (bool.TryParse(raw, out bool fb)) { FireConfig.GroundFirebreaksEnabled.Value = fb; Ok(args, key, fb); }
                    else Bad(args, raw);
                    break;
                case "treeregrowth":
                    if (bool.TryParse(raw, out bool tre)) { FireConfig.TreeRegrowthEnabled.Value = tre; Ok(args, key, tre); }
                    else Bad(args, raw);
                    break;
                case "treeregrowthseconds":
                    if (float.TryParse(raw, out float trs)) { FireConfig.TreeRegrowthSeconds.Value = trs; Ok(args, key, FireConfig.TreeRegrowthSeconds.Value); }
                    else Bad(args, raw);
                    break;
                case "groundleashenabled":
                    if (bool.TryParse(raw, out bool gle)) { FireConfig.GroundMaxSpreadDistanceEnabled.Value = gle; Ok(args, key, gle); }
                    else Bad(args, raw);
                    break;
                case "groundleashdistance":
                    if (float.TryParse(raw, out float gld)) { FireConfig.GroundMaxSpreadDistance.Value = gld; Ok(args, key, FireConfig.GroundMaxSpreadDistance.Value); }
                    else Bad(args, raw);
                    break;
                default:
                    Say(args, $"Unknown key: {key}");
                    break;
            }
        }

        private static void FireListPrefabs(Terminal.ConsoleEventArgs args)
        {
            string filter = args.Length >= 2 ? args[1] : "fire";
            var names = ValheimBridge.FindPrefabNamesContaining(filter);

            if (names.Count == 0)
            {
                Say(args, $"No registered prefabs matching '{filter}'.");
                return;
            }

            Say(args, $"{names.Count} prefabs matching '{filter}':");
            // Print in chunks so the console doesn't eat one giant line.
            for (int i = 0; i < names.Count; i++)
            {
                Say(args, $"  {names[i]}");
            }
        }

        private static void FirePurgeVfx(Terminal.ConsoleEventArgs args)
        {
            int destroyed = ValheimBridge.PurgeAllVanillaFireInstances();
            Say(args, $"Purged {destroyed} leaked vanilla Fire instance(s).");
        }

        private static void FireCheckPrefab(Terminal.ConsoleEventArgs args)
        {
            if (args.Length < 2)
            {
                Say(args, "Usage: firecheckprefab <exact prefab name>");
                return;
            }

            string name = args[1];
            var (found, hasZNetView, scripts) = ValheimBridge.InspectPrefab(name);

            if (!found)
            {
                Say(args, $"No prefab named '{name}' found.");
                return;
            }

            if (!hasZNetView && scripts.Count == 0)
            {
                Say(args, $"'{name}' looks SAFE: no ZNetView, no scripts. Fine to use as vfx.");
                return;
            }

            Say(args, $"'{name}' is RISKY — do not use as vfx without further checking:");
            Say(args, $"  ZNetView present: {hasZNetView}");
            if (scripts.Count > 0)
            {
                Say(args, $"  Scripts: {string.Join(", ", scripts)}");
            }
        }

        private static void FireGroundIgnite(Terminal.ConsoleEventArgs args)
        {
            if (!RequireAdmin(args)) return;

            if (!ValheimBridge.IsServer())
            {
                Say(args, "firegroundignite only works run from the server (or client-hosted/single-player) — " +
                          "raw ground-area ignition has no single ZDOID to forward to the server.");
                return;
            }

            Vector3? posOrNull = ValheimBridge.LocalPlayerPosition();
            if (posOrNull == null) { Say(args, "No local player."); return; }

            float radius = args.TryParameterFloat(1, FireConfig.GroundSpreadRadius.Value);
            FireManager.Instance.IgniteGroundNear(posOrNull.Value, radius);
            Say(args, $"Seeded ground fire within {radius}m of player. {FireManager.Instance.StatusLine()}");
        }

        private static void FireInspectEffectArea(Terminal.ConsoleEventArgs args)
        {
            Vector3? posOrNull = ValheimBridge.LocalPlayerPosition();
            if (posOrNull == null) { Say(args, "No local player."); return; }

            float maxDistance = args.TryParameterFloat(1, 15f);
            string result = ValheimBridge.InspectNearestEffectArea(posOrNull.Value, maxDistance);
            Say(args, result);
        }

        private static void FireTreeRegrow(Terminal.ConsoleEventArgs args)
        {
            (int attempted, int stillPending) = FireManager.Instance.ForceTreeRegrowthNow();
            Say(args, $"firetreeregrow: forced {attempted} pending entries, {stillPending} still pending after attempt " +
                       "(blocked spots retry on backoff rather than failing permanently).");
        }

        private static void FireTreeRegrowList(Terminal.ConsoleEventArgs args)
        {
            var lines = FireManager.Instance.DumpPendingRegrowth();
            if (lines.Count == 0) { Say(args, "No pending tree regrowth entries."); return; }

            Say(args, $"{lines.Count} pending regrowth entries:");
            foreach (string line in lines) Say(args, "  " + line);
        }

        // ---------------------------------------------------------------

        private static void Say(Terminal.ConsoleEventArgs args, string msg)
        {
            args.Context?.AddString(msg);
            FireLogger.Info(msg);
        }

        /// <summary>
        /// Gates the state-mutating dev commands (fireignite, stopfire, startfire,
        /// clearfires, firegroundignite) to admins/host only, now that this runs
        /// on a real shared server with other players connected. Deliberately
        /// does NOT gate the normal fire-arrow ignition path (RPC_Damage
        /// patches) — that's the mod working as intended for every player, not
        /// a debug tool. Returns true (allowed) if the check passes.
        /// </summary>
        private static bool RequireAdmin(Terminal.ConsoleEventArgs args)
        {
            if (ValheimBridge.IsLocalPlayerAdmin()) return true;
            Say(args, "Admin only.");
            return false;
        }

        private static void Ok(Terminal.ConsoleEventArgs args, string key, object val) =>
            Say(args, $"fireset {key} = {val}");

        private static void Bad(Terminal.ConsoleEventArgs args, string raw) =>
            Say(args, $"Couldn't parse value: {raw}");
    }
}