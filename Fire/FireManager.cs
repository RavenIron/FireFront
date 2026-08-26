using System.Collections.Generic;
using FireFront.Config;
using FireFront.Utils;
using UnityEngine;

namespace FireFront.Fire
{
    /// <summary>
    /// Owns all FireFront state. Two parallel systems that interact each cycle:
    ///
    ///   OBJECT fire: _burning (ZDOID -> BurningState: expiry/position/prefab,
    ///   captured once at ignition) + _queue (FIFO overflow, also ZDOID-keyed).
    ///   ZDOID-keyed rather than Component-keyed: a dedicated server can tear
    ///   down and later re-instantiate a target's GameObject at any point
    ///   independent of anything we do (confirmed via live testing), so a
    ///   Component reference can't be trusted to stay valid for a whole burn
    ///   duration. A live Component is only resolved just-in-time, at the two
    ///   moments that actually need one: the real kill at expiry, and
    ///   extinguish. Everything else (spread checks, VFX, damage) runs off
    ///   the cached position, no resolution needed. Targets are WearNTear/
    ///   TreeBase/TreeLog Components, dispatched by type in ValheimBridge.
    ///   Killing a target touches the real game world (destroys a GameObject,
    ///   claims ZDO ownership, etc).
    ///
    ///   GROUND fire: _groundBurning (grid cell -> expiry), pure position/timer
    ///   bookkeeping with NO real GameObject, NO ZNetView, NO vanilla object
    ///   interaction at all — zero corruption risk. This is what lets fire
    ///   cross open grassy gaps that are wider than an object could bridge:
    ///   grass itself has no game object to ignite (it's GPU-instanced visual
    ///   clutter), so ground cells are a stand-in that let the FIRE travel
    ///   even where there's nothing real for it to sit on.
    ///
    /// Cycle (every SpreadCheckInterval seconds):
    ///   1. Prune stale object entries (targets destroyed by other means)
    ///   2. Expire object burn timers -> kill targets
    ///   3. Promote queued objects into freed capacity (FIFO)
    ///   4. Expire ground cell timers
    ///   5. Spread pass: every burning object or ground cell tries to ignite
    ///      nearby objects AND nearby ground cells within its radius
    /// </summary>
    /// <summary>Per-cell state: when it goes out, and the Y it ignited at (fixed for its
    /// lifetime so a spawned VFX doesn't jitter as EstimateGroundY's inputs change).</summary>
    internal struct GroundCellState
    {
        public float ExpireAt;
        public float Y;
    }

    public class FireManager : MonoBehaviour
    {
        public static FireManager Instance { get; private set; }

        /// <summary>
        /// Everything _burning needs to know about a fire, captured ONCE at
        /// ignition. Position/PrefabName never change after that — pieces and
        /// trees are static, they don't move — so caching them here means VFX,
        /// damage zones, and spread checks never need to resolve a live
        /// Component at all, only the kill/extinguish moment does. This is the
        /// fix for a confirmed bug: a force-created object's GameObject can be
        /// torn down by the server's own housekeeping at any point, independent
        /// of anything we do to it (tried claiming ownership — didn't help),
        /// so a Component reference can't be trusted to stay valid for an
        /// entire burn duration on a dedicated server.
        /// </summary>
        private struct BurningState
        {
            public float ExpireAt;
            public Vector3 Position;
            public string PrefabName;
            public int KillAttempts; // bounded retry if resolving a live Component at expiry fails
        }

        private readonly Dictionary<ZDOID, BurningState> _burning = new Dictionary<ZDOID, BurningState>();

        // ZDOID-keyed, same reasoning as _burning: if a later re-resolution
        // returns a DIFFERENT Component reference for the same fire (the
        // server tore down and recreated the object), a Component-keyed
        // dictionary would never find the original VFX to remove it, leaking
        // it. Spawned once at ignition at the cached position, unparented —
        // not attached to the target's transform — so it's fully independent
        // of whatever happens to the underlying vanilla instance afterward.
        private readonly Dictionary<ZDOID, GameObject> _vfx = new Dictionary<ZDOID, GameObject>();

        // Non-authoritative VFX shown on peers that are NOT the server, driven by
        // FireEvent broadcasts rather than local simulation. Deliberately a
        // separate dictionary from _vfx (which is the server's real, damage-
        // capable fire) — this mirror is visual only, no damage zone, so a
        // client watching someone else's fire can't accidentally deal damage
        // from a copy that only exists locally on their own machine.
        private readonly Dictionary<Component, GameObject> _remoteVfx = new Dictionary<Component, GameObject>();
        private bool _fireRpcsRegistered;

        // Ground-fire sync: server-side accumulation of cells that started/
        // stopped burning since the last batched flush (see FlushGroundFireSync),
        // and the client-side visual-only mirror those flushes drive.
        private readonly List<(GroundCellKey key, float y)> _groundIgnitedSinceFlush = new List<(GroundCellKey, float)>();
        private readonly List<GroundCellKey> _groundExpiredSinceFlush = new List<GroundCellKey>();
        private readonly Dictionary<GroundCellKey, GameObject> _remoteGroundVfx = new Dictionary<GroundCellKey, GameObject>();
        private float _nextGroundSyncFlush;
        private const float GroundSyncFlushInterval = 1f;

        // Headless dedicated servers have no interactive console to type
        // 'firestatus' into — this logs the same status line automatically so
        // there's still visibility into what the server-side simulation is
        // actually doing, without needing console access.
        private float _nextStatusHeartbeat;
        private const float StatusHeartbeatInterval = 15f;
        private readonly FireQueue _queue = new FireQueue();

        private readonly Dictionary<GroundCellKey, GroundCellState> _groundBurning = new Dictionary<GroundCellKey, GroundCellState>();
        private readonly Dictionary<GroundCellKey, GameObject> _groundVfx = new Dictionary<GroundCellKey, GameObject>();

        // Cells that have already burned out during the CURRENT fire event and won't
        // reignite until the fire dies out entirely (cleared alongside _fireStartTime
        // reset and in ClearAll). Without this, a fully-consumed cell was free to be
        // re-ignited by a neighbor the very next cycle, producing an endless churn
        // over the same small footprint instead of an advancing front.
        // Cells that have already burned out recently and won't reignite until
        // GroundFuelRegrowSeconds has passed (value = the Time.time they become
        // eligible again). Time-bounded rather than "until the whole fire dies" —
        // a long player-sustained fire that never fully goes out would otherwise
        // grow this collection without limit for the entire session (observed:
        // 9,780+ entries and climbing in a single extended test, never pruned).
        // Pruned each cycle in ExpireGroundTimers alongside the existing sweeps.
        private readonly Dictionary<GroundCellKey, float> _groundExhausted = new Dictionary<GroundCellKey, float>();

        // Cells that have already been really painted (UseVanillaDirtPaint) at
        // least once. Real terrain paint is permanent/persisted-to-disk, so
        // repainting an already-dirt cell is both redundant and, since exhaustion
        // became time-bounded, a genuine leak: a sustained fire can reignite the
        // same cell repeatedly, and each burnout used to spawn a NEW permanent
        // ZNetView-backed piece on top of the last one at the same spot. Painted
        // once, never repainted — deliberately NOT cleared on fire-death (unlike
        // _groundExhausted), since the real paint itself never went away either.
        private readonly HashSet<GroundCellKey> _groundPainted = new HashSet<GroundCellKey>();

        // Burned-cell positions waiting for the next batched real-dirt paint flush.
        // Painting per-burnout was a find+paint+save round trip per cell; batching
        // once per second groups cells by zone TerrainComp and saves once per comp.
        private readonly List<Vector3> _pendingPaint = new List<Vector3>();
        private float _nextPaintFlush;
        private const float PaintFlushInterval = 1f;

        // TTL cache for IsClearedOrCultivated terrain lookups, keyed by ground
        // cell. The firebreak line check samples terrain at half-cell steps along
        // every origin→destination pair during radius ignition — during a heavy
        // burn that's tens of thousands of reflected Heightmap lookups per second
        // re-sampling the same ground (measured 50-60 FPS lost). Terrain paint
        // only changes when someone actively cultivates, so short-TTL caching is
        // safe: worst case, a line cultivated while fire is actively approaching
        // takes up to TTL seconds to register as a break.
        private readonly Dictionary<GroundCellKey, (float expiresAt, bool isBreak)> _firebreakCache =
            new Dictionary<GroundCellKey, (float, bool)>();
        private readonly List<GroundCellKey> _firebreakCacheScratch = new List<GroundCellKey>();
        private float _nextFirebreakCachePrune;
        private const float FirebreakCacheTtl = 5f;

        /// <summary>
        /// One tree pending regrowth: captured at the moment the original tree
        /// finished burning down (name + position), replayed once RegrowAt is
        /// reached. In-memory only — does not survive a server restart.
        /// </summary>
        /// <summary>
        /// An ignite request whose ZDOID resolved to a real, known ZDO but had no
        /// local GameObject instantiated yet on the server — confirmed via a live
        /// dedicated-server test to be a real, recurring race: a freshly-connected
        /// player's surroundings can take a few seconds for the server's
        /// ZNetScene to finish instantiating, even though ZDOMan already has the
        /// object's data. Retried on a short backoff instead of giving up on the
        /// first attempt, same reasoning as the tree-regrowth retry queue below.
        /// </summary>
        private struct PendingIgniteResolution
        {
            public ZDOID Id;
            public float RetryAt;
            public int Attempts;
        }

        private readonly List<PendingIgniteResolution> _pendingIgniteResolutions = new List<PendingIgniteResolution>();
        private readonly List<int> _igniteResolutionScratchIndices = new List<int>();

        private struct PendingRegrowth
        {
            public string PrefabName;
            public Vector3 Position;
            public float RegrowAt;
            public int Attempts; // capped so a permanently-blocked spot (e.g. player built
                                 // over it) doesn't retry forever, just gives up eventually
        }

        private readonly List<PendingRegrowth> _pendingRegrowth = new List<PendingRegrowth>();
        private readonly List<int> _regrowthScratchIndices = new List<int>();
        private int _treesRegrownCount; // running total of successful spawns, for firestatus visibility

        // Reused each cycle to avoid per-frame allocation.
        private readonly List<ZDOID> _scratch = new List<ZDOID>();
        private readonly List<Component> _candidates = new List<Component>();
        private readonly List<GroundCellKey> _groundScratch = new List<GroundCellKey>();
        private readonly List<GroundCellKey> _exhaustedScratch = new List<GroundCellKey>();

        private float _nextCycle;
        private float _fireStartTime = -1f;

        // Where the current fire event first ignited (object or ground), captured
        // once and reused as the leash center for GroundMaxSpreadDistance. Same
        // single-global-event simplification as _fireStartTime — see
        // GroundMaxSpreadDistance's config description.
        private Vector3? _fireOrigin;

        // One-shot latch for the wind-intensity fallback notice — see
        // IgniteAdjacentGroundCells. Deliberately never reset: an EnvMan that
        // can't report wind strength won't start doing so mid-session, and this
        // sits on the hot spread path.
        private bool _windIntensityFallbackLogged;

        public int BurningCount => _burning.Count;
        public int QueuedCount => _queue.Count;
        public int GroundBurningCount => _groundBurning.Count;

        /// <summary>
        /// External read surface: append the world position of every active fire — burning
        /// objects and burning ground cells — to <paramref name="into"/>.
        ///
        /// PUBLIC CROSS-MOD CONTRACT (added 0.17.2). Ragnarok's Wrath resolves this method by
        /// reflection to raise per-zone Scorch where fires burn, so it stays a soft dependency
        /// that tolerates either mod being absent. Renaming it, changing its signature, or
        /// making it instance-state-dependent in a new way is a breaking change for that mod
        /// even though nothing in THIS repo references it.
        ///
        /// Positions come from the same caches the simulation itself trusts: a burning object's
        /// position is captured once at ignition (see BurningState — live Components can be torn
        /// down under us on a dedicated server), and a ground cell's from CellCenter at its
        /// ignition Y. Meaningful on the simulation authority only: clients hold visual-only
        /// mirrors and will report an empty (or partial) picture by design.
        /// </summary>
        public void CollectActiveFirePositions(List<Vector3> into)
        {
            if (into == null) return;

            foreach (KeyValuePair<ZDOID, BurningState> kv in _burning)
                into.Add(kv.Value.Position);

            foreach (KeyValuePair<GroundCellKey, GroundCellState> kv in _groundBurning)
                into.Add(CellCenter(kv.Key, kv.Value.Y));
        }

        private void Awake()
        {
            Instance = this;
        }

        private void Update()
        {
            if (!_fireRpcsRegistered && ZRoutedRpc.instance != null)
            {
                _fireRpcsRegistered = true;
                ValheimBridge.RegisterFireRpcs(HandleIgniteRequest, HandleFireEventBroadcast, HandleGroundFireSync, HandleExtinguishRequest);
            }

            if (FireConfig.ExtinguishKey.Value.IsDown())
            {
                TryPlayerExtinguish();
            }

            // Everything below this point is the actual fire simulation, and it
            // must only ever run on the server. Ignition already only populates
            // _burning/_groundBurning server-side (clients forward a request via
            // RPC instead — see the Harmony ignition patches), so this loop was
            // already a no-op on clients in practice, operating on permanently
            // empty collections. But that was incidental, not enforced — any
            // future code path that adds to those collections locally (a dev
            // command, a bug) would silently start a second, un-networked
            // simulation with no warning. Gate it explicitly instead of relying
            // on that accident.
            if (!ValheimBridge.IsServer()) return;

            if (!FireConfig.Enabled.Value) return;
            if (Time.time < _nextCycle) return;
            _nextCycle = Time.time + FireConfig.SpreadCheckInterval.Value;

            PruneStale();
            ExpireTimers();
            PromoteFromQueue();
            ExpireGroundTimers();
            ProcessTreeRegrowth();
            ProcessPendingIgniteResolutions();
            FlushPendingPaint();
            FlushGroundFireSync();
            PruneFirebreakCache();
            LogStatusHeartbeat();

            if (_burning.Count == 0 && _groundBurning.Count == 0)
            {
                _fireStartTime = -1f; // fully out — next ignition ramps up fresh
                _fireOrigin = null;
                _nextSpreadDiagnosticLog = 0f; // next fire reports its candidate counts on its first cycle
                _groundExhausted.Clear();
            }

            SpreadPass();
        }

        /// <summary>
        /// 0 (just started) to 1 (fully ramped). A brand-new fire starts at
        /// FireRampStartFraction and climbs linearly to 1.0 over
        /// FireRampDurationSeconds. Returns 1.0 outright if ramping is disabled.
        /// </summary>
        private float GetRampFraction()
        {
            if (!FireConfig.FireRampEnabled.Value) return 1f;
            if (_fireStartTime < 0f) return FireConfig.FireRampStartFraction.Value;

            float elapsed = Time.time - _fireStartTime;
            float t = Mathf.Clamp01(elapsed / FireConfig.FireRampDurationSeconds.Value);
            return Mathf.Lerp(FireConfig.FireRampStartFraction.Value, 1f, t);
        }

        /// <summary>
        /// Player-facing manual extinguish: whatever's under the crosshair
        /// (if burning) plus any ground fire within ExtinguishGroundRadius of
        /// the player. This is the in-game equivalent of the stopfire/clearfires
        /// console commands, bound to a real key instead.
        /// </summary>
        private void TryPlayerExtinguish()
        {
            Component target = ValheimBridge.RaycastBurnable();
            Vector3? posOrNull = ValheimBridge.LocalPlayerPosition();
            if (posOrNull == null) return;

            if (ValheimBridge.IsServer())
            {
                bool didSomething = false;

                if (target != null && IsBurning(target))
                {
                    Extinguish(target);
                    didSomething = true;
                }

                int cleared = ExtinguishGroundNear(posOrNull.Value, FireConfig.ExtinguishGroundRadius.Value);
                if (cleared > 0) didSomething = true;

                if (didSomething) ValheimBridge.ShowPlayerMessage("Fire extinguished");
            }
            else
            {
                // Same authority problem ignition had: this used to remove from
                // the CALLER's own _burning/_groundBurning, which are always
                // empty on a real client now (nothing populates them locally
                // anymore) — so pressing the key silently did nothing. Forward
                // the request to the server instead. Shown optimistically here
                // rather than waiting for confirmation — same tradeoff as any
                // client-side input feedback in a networked game.
                ZDOID targetId = (target != null) ? (ValheimBridge.ZDOIDOf(target) ?? ZDOID.None) : ZDOID.None;
                ValheimBridge.SendExtinguishRequestToServer(targetId, posOrNull.Value, FireConfig.ExtinguishGroundRadius.Value);
                ValheimBridge.ShowPlayerMessage("Fire extinguished");
            }
        }

        /// <summary>Server-side handler for a client's extinguish-key press.</summary>
        private void HandleExtinguishRequest(long sender, ZDOID targetId, Vector3 playerPos, float groundRadius)
        {
            if (!ValheimBridge.IsServer()) return;

            if (!targetId.Equals(ZDOID.None))
            {
                Component target = ValheimBridge.ComponentFromZdoid(targetId);
                if (target != null && IsBurning(target)) Extinguish(target);
            }

            ExtinguishGroundNear(playerPos, groundRadius);
        }

        /// <summary>Removes every ground cell within radius of a position. Returns how many were cleared.</summary>
        public int ExtinguishGroundNear(Vector3 origin, float radius)
        {
            float radiusSqr = radius * radius;
            _groundScratch.Clear();

            foreach (KeyValuePair<GroundCellKey, GroundCellState> kv in _groundBurning)
            {
                Vector3 center = CellCenter(kv.Key, kv.Value.Y);
                if ((center - origin).sqrMagnitude <= radiusSqr)
                {
                    _groundScratch.Add(kv.Key);
                }
            }

            foreach (GroundCellKey key in _groundScratch)
            {
                float y = _groundBurning[key].Y;
                _groundBurning.Remove(key);
                RemoveGroundVfxFor(key);
                LeaveScorchMark(key, y);
                _groundExpiredSinceFlush.Add(key);
            }

            return _groundScratch.Count;
        }

        // ---------------------------------------------------------------
        // Public API (patches + dev commands call these) — object fire
        // ---------------------------------------------------------------

        /// <summary>
        /// Request ignition. Respects vanilla burnability, the concurrent cap,
        /// and the overflow queue. Silent drop when both are full.
        /// </summary>
        public void TryIgnite(Component target)
        {
            // TEMPORARY DIAGNOSTIC — remove once the server-authority question is
            // settled. Purpose: FireManager has no ZNet.instance.IsServer() gating
            // anywhere, so every peer with this mod loaded runs its own independent
            // simulation from whatever triggers TryIgnite. If RPC_Damage (the only
            // ignition trigger) executes on every connected peer rather than just
            // the object's owner, this line will fire once per peer for a single
            // hit — confirming duplicate simulation. Compare counts across the
            // dedicated server's log and each connected client's log for the same
            // ignition event.
            bool? isServer = ZNet.instance != null ? ZNet.instance.IsServer() : (bool?)null;
            FireLogger.Debug($"[AUTHORITY-CHECK] TryIgnite called on {ValheimBridge.NameOf(target)} " +
                              $"— IsServer={(isServer.HasValue ? isServer.Value.ToString() : "ZNet.instance null")}, " +
                              $"peer={SystemInfo.deviceUniqueIdentifier}");

            if (!FireConfig.Enabled.Value) return;
            if (!ValheimBridge.IsAlive(target)) return;
            if (!ValheimBridge.IsBurnable(target)) return;

            ZDOID? idOrNull = ValheimBridge.ZDOIDOf(target);
            if (!idOrNull.HasValue) return; // can't track what we can't identify
            ZDOID id = idOrNull.Value;

            if (_burning.ContainsKey(id)) return;
            if (_queue.Contains(id)) return;

            int effectiveMax = Mathf.Max(1, Mathf.RoundToInt(FireConfig.MaxConcurrentBurning.Value * GetRampFraction()));

            if (_burning.Count < effectiveMax)
            {
                if (_fireStartTime < 0f) _fireStartTime = Time.time;
                if (_fireOrigin == null) _fireOrigin = ValheimBridge.PositionOf(target);
                StartBurning(target, id);
            }
            else if (_queue.TryEnqueue(id))
            {
                FireLogger.Debug($"Queued ({_queue.Count}/{_queue.Capacity}): {ValheimBridge.NameOf(target)}");
            }
            // else: queue full -> silent drop; spread re-attempts next cycle.
        }

        public bool IsBurning(Component target)
        {
            if (target == null) return false;
            ZDOID? id = ValheimBridge.ZDOIDOf(target);
            return id.HasValue && _burning.ContainsKey(id.Value);
        }

        public void Extinguish(Component target)
        {
            if (target == null) return;
            ZDOID? id = ValheimBridge.ZDOIDOf(target);
            if (!id.HasValue) return;

            if (_burning.Remove(id.Value))
                FireLogger.Debug($"Extinguished: {ValheimBridge.NameOf(target)}");
            _queue.Remove(id.Value);
            RemoveVfxFor(id.Value);
        }

        public void ClearAll()
        {
            int n = _burning.Count + _queue.Count + _groundBurning.Count;
            foreach (ZDOID id in _burning.Keys) RemoveVfxFor(id);
            foreach (GameObject instance in _groundVfx.Values) { if (instance != null) Destroy(instance); }
            foreach (GameObject instance in _remoteVfx.Values) { if (instance != null) Destroy(instance); }
            foreach (GameObject instance in _remoteGroundVfx.Values) { if (instance != null) Destroy(instance); }
            _remoteVfx.Clear();
            _remoteGroundVfx.Clear();
            _groundExpiredSinceFlush.AddRange(_groundBurning.Keys); // so the next flush tells clients to clear these too
            _pendingIgniteResolutions.Clear();
            _burning.Clear();
            _queue.Clear();
            _groundBurning.Clear();
            _groundVfx.Clear();
            _groundExhausted.Clear();
            _pendingRegrowth.Clear();
            _fireStartTime = -1f;
            _fireOrigin = null;
            _nextSpreadDiagnosticLog = 0f;
            FireLogger.Info($"Cleared all fires ({n} entries).");
        }

        /// <summary>Called by the OnDestroy patch (pieces only) when removed by any means.</summary>
        public void HandleTargetRemoved(Component target)
        {
            if (target == null) return;
            ZDOID? id = ValheimBridge.ZDOIDOf(target);
            if (!id.HasValue) return; // can't resolve at this exact moment — leave tracked, kill-time retry will sort it out
            if (!_burning.ContainsKey(id.Value)) return; // wasn't tracked, nothing to do

            if (ValheimBridge.ZdoExists(id.Value))
            {
                // ZDO still exists — this OnDestroy was just de-instantiation
                // (the server's own housekeeping tearing down the local
                // GameObject, NOT the object actually going away), the exact
                // thing that was silently breaking spread/destruction before.
                // Stay tracked; ExpireTimers force-creates it again via
                // ComponentFromZdoid when its timer is actually up.
                FireLogger.Debug($"[IGNITE-TRACE] HandleTargetRemoved: {ValheimBridge.NameOf(target)} de-instantiated " +
                                  "but its ZDO still exists — staying tracked, not a real destruction.");
                return;
            }

            // ZDO is actually gone — really destroyed (chopped down, burned
            // elsewhere, etc.). Now it's safe to stop tracking it.
            _burning.Remove(id.Value);
            _queue.Remove(id.Value);
            FireLogger.Debug($"[IGNITE-TRACE] HandleTargetRemoved: {ValheimBridge.NameOf(target)} really destroyed — removing from _burning.");
            RemoveVfxFor(id.Value);
        }

        private void LogStatusHeartbeat()
        {
            if (_burning.Count == 0 && _groundBurning.Count == 0) return; // nothing active, stay quiet
            if (Time.time < _nextStatusHeartbeat) return;
            _nextStatusHeartbeat = Time.time + StatusHeartbeatInterval;

            FireLogger.Info($"[HEARTBEAT] {StatusLine()}");
        }

        public string StatusLine()
        {
            return $"FireFront: burning {_burning.Count}/{FireConfig.MaxConcurrentBurning.Value}, " +
                   $"queued {_queue.Count}/{_queue.Capacity}, " +
                   $"ground {_groundBurning.Count}/{FireConfig.GroundMaxConcurrent.Value} (enabled {FireConfig.GroundSpreadEnabled.Value}, vfxcap {FireConfig.GroundVfxMaxConcurrent.Value}, dmgcap {FireConfig.GroundDamageMaxConcurrent.Value}, raining {ValheimBridge.IsRaining()}), " +
                   $"burn {FireConfig.BurnDurationSeconds.Value}s, " +
                   $"radius {FireConfig.SpreadRadius.Value}m, " +
                   $"groundradius {FireConfig.GroundSpreadRadius.Value}m, " +
                   $"interval {FireConfig.SpreadCheckInterval.Value}s, " +
                   $"trees {FireConfig.BurnTreesAndLogs.Value}, " +
                   $"vfx '{FireConfig.VfxPrefabName.Value}', procedural {FireConfig.UseProceduralVfx.Value}, " +
                   $"hurts {FireConfig.FireHurtsEnabled.Value} (playerOnly {FireConfig.FireHurtsPlayerOnly.Value}, {FireConfig.FireDamagePerTick.Value}dmg/{FireConfig.FireDamageTickInterval.Value}s), " +
                   $"dirtpaint {FireConfig.UseVanillaDirtPaint.Value}, " +
                   $"exhaustion {FireConfig.GroundFuelExhaustionEnabled.Value} (regrow {FireConfig.GroundFuelRegrowSeconds.Value}s), " +
                   $"treeregrowth {FireConfig.TreeRegrowthEnabled.Value} (after {FireConfig.TreeRegrowthSeconds.Value}s, pending {_pendingRegrowth.Count}), " +
                   $"pendingignite {_pendingIgniteResolutions.Count}, " +
                   $"firebreaks {FireConfig.GroundFirebreaksEnabled.Value}, " +
                   $"waterblocks {FireConfig.GroundWaterBlocksSpreadEnabled.Value}, " +
                   $"realpermanent (paintedcells {_groundPainted.Count}, regrowntrees {_treesRegrownCount}), " +
                   $"wind {FireConfig.WindSpreadBiasEnabled.Value} (upwindchance {FireConfig.WindUpwindIgniteChance.Value:F2}, " +
                   $"influence {FireConfig.WindInfluence.Value:F2}, live intensity {WindIntensityForStatus()}), " +
                   $"groundleash {FireConfig.GroundMaxSpreadDistanceEnabled.Value} ({FireConfig.GroundMaxSpreadDistance.Value}m), " +
                   $"ramp {(GetRampFraction() * 100f):F0}% (enabled {FireConfig.FireRampEnabled.Value}, start {(FireConfig.FireRampStartFraction.Value * 100f):F0}%, duration {FireConfig.FireRampDurationSeconds.Value}s), " +
                   $"enabled {FireConfig.Enabled.Value}";
        }

        /// <summary>
        /// Live wind strength for the status line, or "n/a" if it can't be read.
        /// Shown alongside the influence setting because the two MULTIPLY — a
        /// correctly-set influence with near-zero intensity still looks like wind
        /// bias doing nothing, and chasing that without the live number visible is
        /// exactly the kind of invisible-setting debugging this status line exists
        /// to prevent.
        /// </summary>
        private static string WindIntensityForStatus()
        {
            float? intensity = ValheimBridge.GetWindIntensity();
            return intensity.HasValue ? intensity.Value.ToString("F2") : "n/a";
        }

        // ---------------------------------------------------------------
        // Public API — ground fire
        // ---------------------------------------------------------------

        /// <summary>
        /// Ground-to-ground propagation for ONE cycle: only the 8 immediately
        /// adjacent cells, never the full GroundSpreadRadius. This is the fix
        /// for a real bug in 0.4.0 — using the full radius here caused every
        /// burning cell to flood its entire reach in a single cycle, and every
        /// newly-lit cell to do the same the next cycle, compounding into a
        /// near-instant area explosion (and torching far more trees than
        /// intended) instead of a gradual advancing front. GroundSpreadRadius
        /// still governs how far a cell can reach out to ignite a real nearby
        /// object — that direction isn't self-compounding since it's bounded
        /// by how many real objects exist nearby, not by cell count.
        /// </summary>
        private void IgniteAdjacentGroundCells(GroundCellKey originKey, float y)
        {
            if (FireConfig.RainSuppressesGroundFire.Value && ValheimBridge.IsRaining()) return;

            // Wind is global, not per-zone, so both reads happen once here per
            // spread call rather than once per neighbor.
            Vector3? wind = FireConfig.WindSpreadBiasEnabled.Value ? ValheimBridge.GetWindDirection() : null;
            Vector2 windXZ = wind.HasValue ? new Vector2(wind.Value.x, wind.Value.z).normalized : Vector2.zero;

            // How hard the directional bias is actually applied: the WindInfluence
            // config scaled by vanilla's live wind strength, so weather now changes
            // the front's SHAPE and not just which way it leans. GetWindIntensity
            // returns 0 only before EnvMan's first UpdateWind (its clamp floor in
            // play is 0.05) — treat that, and a failed read, as "no data" and fall
            // back to full strength so the bias behaves as it did pre-0.17.2.
            float? intensity = wind.HasValue ? ValheimBridge.GetWindIntensity() : null;
            bool intensityUsable = intensity.HasValue && intensity.Value > 0f;
            if (wind.HasValue && !intensityUsable && !_windIntensityFallbackLogged)
            {
                _windIntensityFallbackLogged = true;
                FireLogger.Debug($"Wind intensity unusable (read {(intensity.HasValue ? intensity.Value.ToString("F3") : "null")}); " +
                                 "wind bias applying WindInfluence at full strength.");
            }

            float strength = Mathf.Clamp01(FireConfig.WindInfluence.Value) *
                             (intensityUsable ? Mathf.Clamp01(intensity.Value) : 1f);
            bool haveWind = wind.HasValue && windXZ != Vector2.zero && strength > 0f;

            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (dx == 0 && dz == 0) continue;

                    if (haveWind)
                    {
                        // dot: -1 = directly upwind, +1 = directly downwind. Lerp the
                        // ignite chance between the configured upwind floor and a
                        // guaranteed downwind catch, so wind narrows the front's spread
                        // width without ever fully walling off the upwind side. That
                        // directional chance is then faded toward 1 (ignite everything,
                        // i.e. no bias at all) by strength, so WindInfluence 0 or dead
                        // calm reproduces the old unweighted behavior exactly.
                        float dot = Vector2.Dot(windXZ, new Vector2(dx, dz).normalized);
                        float directional = Mathf.Lerp(FireConfig.WindUpwindIgniteChance.Value, 1f, (dot + 1f) * 0.5f);
                        float chance = Mathf.Lerp(1f, directional, strength);
                        if (Random.value > chance) continue;
                    }

                    TryIgniteGroundCell(new GroundCellKey(originKey.X + dx, originKey.Z + dz), y);
                }
            }
        }

        /// <summary>
        /// Ignite every ground cell within radius of a world position. Used
        /// for object-to-ground seeding (a burning tree/piece lighting the
        /// ground around its own fixed position — bounded, not compounding)
        /// and by the firegroundignite dev command for direct testing.
        /// NOT used for ground-to-ground propagation — see
        /// IgniteAdjacentGroundCells for why.
        /// </summary>
        public void IgniteGroundNear(Vector3 origin, float radius)
        {
            if (!FireConfig.GroundSpreadEnabled.Value) return;

            float size = FireConfig.GroundCellSize.Value;
            int range = Mathf.CeilToInt(radius / size);
            float radiusSqr = radius * radius;
            GroundCellKey originKey = KeyOf(origin);

            for (int dx = -range; dx <= range; dx++)
            {
                for (int dz = -range; dz <= range; dz++)
                {
                    var key = new GroundCellKey(originKey.X + dx, originKey.Z + dz);
                    if (_groundBurning.ContainsKey(key)) continue;

                    Vector3 center = CellCenter(key, origin.y);
                    if ((center - origin).sqrMagnitude > radiusSqr) continue;

                    // Firebreak line check: TryIgniteGroundCell only tests the
                    // DESTINATION cell's terrain, so radius seeding could jump a
                    // narrow cultivated line entirely — fire on one side igniting
                    // grass on the far side without ever touching the break. Sample
                    // the terrain along the origin→destination line; if any point
                    // is cleared/cultivated, the ground path is broken and this
                    // cell can't be reached by ground-level spread from here.
                    if (FireConfig.GroundFirebreaksEnabled.Value && GroundPathCrossesFirebreak(origin, center)) continue;

                    TryIgniteGroundCell(key, origin.y);
                }
            }
        }

        /// <summary>
        /// True if the straight ground line between two points crosses cleared or
        /// cultivated terrain. Samples at half-cell steps so a break as narrow as
        /// one cultivator swipe can't fall between sample points. Origin and
        /// destination cells themselves are covered by their own per-cell checks.
        /// </summary>
        /// <summary>
        /// Cached IsClearedOrCultivated lookup, keyed by the ground cell containing
        /// the sample point. One reflected terrain query per cell per TTL window
        /// instead of per sample — the whole point, since line checks re-sample the
        /// same cells constantly during a burn.
        /// </summary>
        private bool IsFirebreakAt(Vector3 samplePoint)
        {
            GroundCellKey key = KeyOf(samplePoint);
            float now = Time.time;

            if (_firebreakCache.TryGetValue(key, out (float expiresAt, bool isBreak) cached) && now < cached.expiresAt)
            {
                return cached.isBreak;
            }

            bool isBreak = ValheimBridge.IsClearedOrCultivated(samplePoint);
            _firebreakCache[key] = (now + FirebreakCacheTtl, isBreak);
            return isBreak;
        }

        /// <summary>Periodic sweep of expired firebreak cache entries so it can't grow unbounded.</summary>
        private void PruneFirebreakCache()
        {
            float now = Time.time;
            if (now < _nextFirebreakCachePrune) return;
            _nextFirebreakCachePrune = now + FirebreakCacheTtl;

            if (_firebreakCache.Count == 0) return;
            _firebreakCacheScratch.Clear();
            foreach (KeyValuePair<GroundCellKey, (float expiresAt, bool isBreak)> kv in _firebreakCache)
            {
                if (now >= kv.Value.expiresAt) _firebreakCacheScratch.Add(kv.Key);
            }
            foreach (GroundCellKey key in _firebreakCacheScratch)
            {
                _firebreakCache.Remove(key);
            }
        }

        private bool GroundPathCrossesFirebreak(Vector3 from, Vector3 to)
        {
            float stepSize = FireConfig.GroundCellSize.Value * 0.5f;
            Vector3 delta = to - from;
            float distance = delta.magnitude;
            if (distance <= stepSize) return false; // adjacent — destination's own check suffices

            int steps = Mathf.CeilToInt(distance / stepSize);
            for (int i = 1; i < steps; i++)
            {
                Vector3 samplePoint = from + delta * (i / (float)steps);
                if (IsFirebreakAt(samplePoint)) return true;
            }
            return false;
        }

        private void TryIgniteGroundCell(GroundCellKey key, float y)
        {
            if (_groundBurning.ContainsKey(key)) return;
            if (FireConfig.GroundFuelExhaustionEnabled.Value &&
                _groundExhausted.TryGetValue(key, out float exhaustedUntil) && Time.time < exhaustedUntil) return;

            Vector3 approxCenter = CellCenter(key, y);

            // Real firebreak support: a dirt path or tilled/cultivated strip has
            // no grass fuel on it, so ground fire shouldn't cross it. Checked
            // before the ground-max/ramp bookkeeping below since this is a hard
            // "no fuel here" rule, not a capacity limit.
            if (FireConfig.GroundFirebreaksEnabled.Value && IsFirebreakAt(approxCenter)) return;

            // Leash: cell-to-adjacent-cell propagation (IgniteAdjacentGroundCells)
            // otherwise has NO distance limit at all, only a cap on how many cells
            // burn at once — a wind-driven front will happily march hundreds of
            // meters away from the origin, silently, since it's pure grid math
            // with no real GameObject required. Checked with the cheap inherited
            // y (not yet the real sampled height below) since only horizontal
            // distance matters here and this should reject before paying for a
            // raycast. Object-to-ground seeding (IgniteGroundNear) is already
            // bounded by GroundSpreadRadius and essentially never trips this.
            if (FireConfig.GroundMaxSpreadDistanceEnabled.Value && _fireOrigin.HasValue)
            {
                float maxDist = FireConfig.GroundMaxSpreadDistance.Value;
                if ((approxCenter - _fireOrigin.Value).sqrMagnitude > maxDist * maxDist) return;
            }

            int effectiveGroundMax = Mathf.Max(1, Mathf.RoundToInt(FireConfig.GroundMaxConcurrent.Value * GetRampFraction()));
            if (_groundBurning.Count >= effectiveGroundMax) return; // silent drop, natural retry next cycle

            if (_fireStartTime < 0f) _fireStartTime = Time.time;
            if (_fireOrigin == null) _fireOrigin = approxCenter;

            // y here is inherited from whatever ignited this cell (neighbor or
            // object) — only an approximation, used as a reasonable starting
            // point for the real height query below. Over many hops of
            // ground-to-ground spread across sloped terrain, a purely-inherited
            // Y drifts noticeably from the real surface (floating fire). Sample
            // the actual terrain height at this cell's real (x,z) instead, once,
            // at ignition time — same cheap "computed once" cost as before, just
            // accurate now.
            float realY = ValheimBridge.GetGroundHeight(approxCenter);

            // No grass grows on open water — without this, ground fire had no
            // way to tell "actual land" from "ocean/lake", and could spread
            // straight across a shoreline (confirmed: a small island's fire
            // spread clean through the water around it). Checked using the
            // REAL sampled height, not the approximate inherited y, since
            // that's the only value we can trust to be accurate here.
            if (FireConfig.GroundWaterBlocksSpreadEnabled.Value)
            {
                float waterLevel = ValheimBridge.GetWaterLevel();
                if (realY <= waterLevel)
                {
                    FireLogger.Debug($"[IGNITE-TRACE] TryIgniteGroundCell({key.X},{key.Z}): blocked, realY={realY:F2} <= waterLevel={waterLevel:F2}.");
                    return;
                }
            }

            float duration = FireConfig.GroundBurnDurationSeconds.Value;
            if (FireConfig.RainSuppressesGroundFire.Value && ValheimBridge.IsRaining())
            {
                duration *= FireConfig.RainGroundBurnDurationMultiplier.Value;
            }

            _groundBurning[key] = new GroundCellState { ExpireAt = Time.time + duration, Y = realY };
            FireLogger.Debug($"Ground ignited ({_groundBurning.Count}/{effectiveGroundMax}) at cell ({key.X},{key.Z})");
            SpawnGroundVfxFor(key, CellCenter(key, realY));
            _groundIgnitedSinceFlush.Add((key, realY));
        }

        private void SpawnGroundVfxFor(GroundCellKey key, Vector3 position)
        {
            if (_groundVfx.ContainsKey(key)) return;

            bool wantDamage = FireConfig.FireHurtsEnabled.Value;
            bool wantVisual = FireConfig.UseProceduralVfx.Value;
            if (!wantVisual && !wantDamage) return;

            // Overall cap on tracked ground effect objects (visual and/or damage-only
            // combined) — bounded by the higher of the two per-purpose caps, since a
            // damage-only object is cheap (just a polling FireBurnZone, no particles).
            int overallCap = Mathf.Max(FireConfig.GroundVfxMaxConcurrent.Value, FireConfig.GroundDamageMaxConcurrent.Value);
            if (_groundVfx.Count >= overallCap) return;

            // Visual has its OWN sub-cap, checked independently — this used to share
            // GroundVfxMaxConcurrent with damage entirely, meaning only ~30 of up to
            // 200 burning cells ever got a damage zone at all (objects have their own
            // separate, much higher cap and worked fine — that's why fire only hurt
            // near trees/pieces, never out in open ground).
            if (wantVisual)
            {
                int visualCount = 0;
                foreach (GameObject go in _groundVfx.Values)
                {
                    if (go != null && go.GetComponent<ParticleSystem>() != null) visualCount++;
                }
                if (visualCount >= FireConfig.GroundVfxMaxConcurrent.Value) wantVisual = false;
            }

            // Damage gets its own independent check against its own (much higher) cap.
            if (wantDamage)
            {
                int damageCount = 0;
                foreach (GameObject go in _groundVfx.Values)
                {
                    if (go != null && go.GetComponent<FireBurnZone>() != null) damageCount++;
                }
                if (damageCount >= FireConfig.GroundDamageMaxConcurrent.Value) wantDamage = false;
            }

            if (!wantVisual && !wantDamage) return;

            GameObject instance = wantVisual
                ? ValheimBridge.CreateProceduralGroundFireVfx(position)
                : new GameObject("FireFrontGroundDamageZone");
            if (!wantVisual) instance.transform.position = position;

            if (wantDamage)
            {
                ValheimBridge.AttachFireDamageZone(instance, FireConfig.GroundCellSize.Value * 0.5f,
                    FireConfig.FireHurtsPlayerOnly.Value, FireConfig.FireDamagePerTick.Value, FireConfig.FireDamageTickInterval.Value);
            }

            _groundVfx[key] = instance;
        }

        private void RemoveGroundVfxFor(GroundCellKey key)
        {
            if (_groundVfx.TryGetValue(key, out GameObject instance))
            {
                _groundVfx.Remove(key);
                if (instance != null) Destroy(instance);
            }
        }

        private GroundCellKey KeyOf(Vector3 pos)
        {
            float size = FireConfig.GroundCellSize.Value;
            return new GroundCellKey(Mathf.FloorToInt(pos.x / size), Mathf.FloorToInt(pos.z / size));
        }

        private Vector3 CellCenter(GroundCellKey key, float y)
        {
            float size = FireConfig.GroundCellSize.Value;
            return new Vector3((key.X + 0.5f) * size, y, (key.Z + 0.5f) * size);
        }

        // ---------------------------------------------------------------
        // Cycle steps — object fire
        // ---------------------------------------------------------------

        private void StartBurning(Component target, ZDOID id)
        {
            Vector3 position = ValheimBridge.PositionOf(target);
            _burning[id] = new BurningState
            {
                ExpireAt = Time.time + FireConfig.BurnDurationSeconds.Value,
                Position = position,
                PrefabName = ValheimBridge.PrefabNameOf(target)
            };
            FireLogger.Debug($"Ignited ({_burning.Count}/{FireConfig.MaxConcurrentBurning.Value}): {ValheimBridge.NameOf(target)}");
            SpawnVfxFor(id, position);

            // Only ever called on the server (TryIgnite is server-gated — see the
            // Harmony patches), so this is always the real fire starting. Tell
            // every connected peer so they can show it locally too.
            FireLogger.Debug($"[IGNITE-TRACE] StartBurning: broadcasting FireEvent(started=true) for {ValheimBridge.NameOf(target)}, ZDOID={id}.");
            ValheimBridge.BroadcastFireEvent(id, started: true);
        }

        private void SpawnVfxFor(ZDOID id, Vector3 position)
        {
            if (_vfx.ContainsKey(id)) return;

            bool wantVisual = FireConfig.UseProceduralVfx.Value || !string.IsNullOrEmpty(FireConfig.VfxPrefabName.Value);
            bool wantDamage = FireConfig.FireHurtsEnabled.Value;
            if (!wantVisual && !wantDamage) return;

            GameObject instance = null;
            if (FireConfig.UseProceduralVfx.Value)
            {
                instance = ValheimBridge.CreateProceduralFireVfx(position);
            }
            else if (!string.IsNullOrEmpty(FireConfig.VfxPrefabName.Value))
            {
                GameObject prefab = ValheimBridge.FindPrefabByName(FireConfig.VfxPrefabName.Value);
                if (prefab != null) instance = ValheimBridge.SpawnVfx(prefab, position);
            }

            // No visual configured or available, but damage is still wanted —
            // spawn a bare invisible container so FireHurtsEnabled doesn't
            // silently depend on visuals being on.
            if (instance == null && wantDamage)
            {
                instance = new GameObject("FireFrontDamageZone");
                instance.transform.position = position;
            }

            if (instance == null) return;

            if (wantDamage)
            {
                ValheimBridge.AttachFireDamageZone(instance, FireConfig.FireHurtsObjectRadius.Value,
                    FireConfig.FireHurtsPlayerOnly.Value, FireConfig.FireDamagePerTick.Value, FireConfig.FireDamageTickInterval.Value);
            }

            _vfx[id] = instance;
        }

        /// <summary>
        /// Server-side RPC handler: a client's RPC_Damage prefix couldn't ignite
        /// locally (it isn't the server) and forwarded the request here instead.
        /// Defensive IsServer() check even though only the server should ever
        /// receive this — ZRoutedRpc.Register runs the same handler on every peer
        /// that has it registered, and this is deliberately targeted at the
        /// server peer ID, but cheap to double-check.
        /// </summary>
        private void HandleIgniteRequest(long sender, ZDOID id)
        {
            if (!ValheimBridge.IsServer())
            {
                FireLogger.Debug($"[IGNITE-TRACE] HandleIgniteRequest received on a non-server peer for ZDOID={id} — ignoring (shouldn't normally happen).");
                return;
            }

            FireLogger.Debug($"[IGNITE-TRACE] Server received ignite request from peer {sender} for ZDOID={id}.");
            Component target = ValheimBridge.ComponentFromZdoid(id);
            if (target != null)
            {
                FireLogger.Debug($"[IGNITE-TRACE] Resolved ZDOID={id} to {ValheimBridge.NameOf(target)} — calling TryIgnite.");
                TryIgnite(target);
            }
            else
            {
                FireLogger.Debug($"[IGNITE-TRACE] Could NOT resolve ZDOID={id} on first attempt — queuing for retry " +
                                  "(likely just not instantiated in the server's ZNetScene yet).");
                _pendingIgniteResolutions.Add(new PendingIgniteResolution
                {
                    Id = id,
                    RetryAt = Time.time + IgniteResolutionRetryInterval,
                    Attempts = 0
                });
            }
        }

        private const float IgniteResolutionRetryInterval = 0.5f;
        private const int IgniteResolutionMaxAttempts = 20; // ~10 seconds total before giving up

        /// <summary>
        /// Server-side: retries resolving queued ignite requests whose ZDOID
        /// exists but had no local GameObject yet. Runs every cycle as part of
        /// the main server-only simulation loop.
        /// </summary>
        private void ProcessPendingIgniteResolutions()
        {
            if (_pendingIgniteResolutions.Count == 0) return;

            float now = Time.time;
            _igniteResolutionScratchIndices.Clear();

            for (int i = 0; i < _pendingIgniteResolutions.Count; i++)
            {
                PendingIgniteResolution entry = _pendingIgniteResolutions[i];
                if (now < entry.RetryAt) continue;

                Component target = ValheimBridge.ComponentFromZdoid(entry.Id);
                if (target != null)
                {
                    FireLogger.Debug($"[IGNITE-TRACE] Retry resolved ZDOID={entry.Id} to {ValheimBridge.NameOf(target)} " +
                                      $"after {entry.Attempts + 1} attempt(s) — calling TryIgnite.");
                    TryIgnite(target);
                    _igniteResolutionScratchIndices.Add(i);
                    continue;
                }

                entry.Attempts++;
                if (entry.Attempts >= IgniteResolutionMaxAttempts)
                {
                    FireLogger.Debug($"[IGNITE-TRACE] Gave up resolving ZDOID={entry.Id} after {entry.Attempts} attempts.");
                    _igniteResolutionScratchIndices.Add(i);
                }
                else
                {
                    entry.RetryAt = now + IgniteResolutionRetryInterval;
                    _pendingIgniteResolutions[i] = entry;
                }
            }

            for (int i = _igniteResolutionScratchIndices.Count - 1; i >= 0; i--)
            {
                _pendingIgniteResolutions.RemoveAt(_igniteResolutionScratchIndices[i]);
            }
        }

        /// <summary>
        /// Broadcast handler: the server just started or stopped a real fire.
        /// The server itself no-ops here — it already has the authoritative VFX
        /// from its own StartBurning/RemoveVfxFor. Every other peer spawns/
        /// removes a local, non-authoritative, no-damage copy so they can see
        /// the fire even though they aren't simulating it.
        /// </summary>
        private void HandleFireEventBroadcast(long sender, ZDOID id, bool started)
        {
            if (ValheimBridge.IsServer()) return;

            Component target = ValheimBridge.ComponentFromZdoid(id);
            if (target == null) return; // not loaded on this peer (out of range) — nothing to show

            if (started) SpawnRemoteVfxOnly(target);
            else RemoveRemoteVfxFor(target);
        }

        private void SpawnRemoteVfxOnly(Component target)
        {
            if (_remoteVfx.ContainsKey(target)) return;

            bool wantVisual = FireConfig.UseProceduralVfx.Value || !string.IsNullOrEmpty(FireConfig.VfxPrefabName.Value);
            if (!wantVisual) return;

            GameObject instance = null;
            if (FireConfig.UseProceduralVfx.Value)
            {
                instance = ValheimBridge.CreateProceduralFireVfx(ValheimBridge.PositionOf(target));
            }
            else
            {
                GameObject prefab = ValheimBridge.FindPrefabByName(FireConfig.VfxPrefabName.Value);
                if (prefab != null) instance = ValheimBridge.SpawnVfx(prefab, ValheimBridge.PositionOf(target));
            }

            if (instance != null) _remoteVfx[target] = instance;
        }

        private void RemoveRemoteVfxFor(Component target)
        {
            if (_remoteVfx.TryGetValue(target, out GameObject instance))
            {
                _remoteVfx.Remove(target);
                if (instance != null) Destroy(instance);
            }
        }

        private void RemoveVfxFor(ZDOID id)
        {
            if (_vfx.TryGetValue(id, out GameObject instance))
            {
                _vfx.Remove(id);
                if (instance != null) Destroy(instance);
            }

            // Covers burn-out, manual extinguish, ClearAll, and removal-by-other-
            // means — every path that stops a fire routes through here.
            ValheimBridge.BroadcastFireEvent(id, started: false);
        }

        private void PruneStale()
        {
            _scratch.Clear();
            foreach (ZDOID id in _burning.Keys)
            {
                // ZDO-existence poll, NOT a live-Component check. This is the
                // actual fix: the old IsAlive(Component) check treated "no
                // local GameObject right now" the same as "really destroyed" —
                // but a dedicated server can de-instantiate an object (ZDO
                // still exists) independent of whether it's actually gone.
                // Only remove things that are truly, permanently destroyed.
                if (!ValheimBridge.ZdoExists(id)) _scratch.Add(id);
            }
            foreach (ZDOID dead in _scratch)
            {
                _burning.Remove(dead);
                FireLogger.Debug($"[IGNITE-TRACE] PruneStale: removed {dead} (ZDO no longer exists — really destroyed).");
                RemoveVfxFor(dead);
            }
        }

        private const int KillResolutionMaxAttempts = 20; // ~15s at the 0.75s default cycle before giving up

        private void ExpireTimers()
        {
            _scratch.Clear();
            float now = Time.time;
            foreach (KeyValuePair<ZDOID, BurningState> kv in _burning)
            {
                if (now >= kv.Value.ExpireAt) _scratch.Add(kv.Key);
            }

            // Throttle: destroying many ZNetView objects in one tight burst
            // within a single frame can race with ZNetScene's own per-frame
            // bookkeeping (observed during the 0.4.0 ground-spread bug, which
            // killed ~15+ trees near-simultaneously and produced the same
            // ZNetScene.RemoveObjects NullReferenceException the 0.3.2 fix was
            // meant to prevent — that time from batch size, not a wrong API).
            // Cap how many actually get destroyed this cycle; anything past
            // the limit just stays in _burning past its nominal expiry and
            // gets caught on a later cycle instead of all in one frame.
            int killed = 0;
            foreach (ZDOID id in _scratch)
            {
                if (killed >= FireConfig.MaxKillsPerCycle.Value) break;

                BurningState state = _burning[id];

                // A live Component is only needed at this exact moment (to
                // actually call Destroy on) — resolved fresh here rather than
                // held onto for the whole burn duration, since we've confirmed
                // that reference can go stale independent of anything we do.
                Component target = ValheimBridge.ComponentFromZdoid(id);
                if (target == null)
                {
                    state.KillAttempts++;
                    if (state.KillAttempts >= KillResolutionMaxAttempts)
                    {
                        FireLogger.Debug($"[IGNITE-TRACE] ExpireTimers: gave up resolving {id} to kill after " +
                                          $"{state.KillAttempts} attempts — removing from _burning anyway (possible orphan).");
                        _burning.Remove(id);
                        RemoveVfxFor(id);
                    }
                    else
                    {
                        _burning[id] = state; // write back the incremented attempt count
                    }
                    continue;
                }

                _burning.Remove(id);
                FireLogger.Debug($"Burned down: {ValheimBridge.NameOf(target)}");

                if (FireConfig.TreeRegrowthEnabled.Value && ValheimBridge.KindOf(target) == BurnKind.Tree)
                {
                    _pendingRegrowth.Add(new PendingRegrowth
                    {
                        PrefabName = state.PrefabName,
                        Position = state.Position,
                        RegrowAt = Time.time + FireConfig.TreeRegrowthSeconds.Value
                    });
                }

                ValheimBridge.KillBurningTarget(target);
                RemoveVfxFor(id);
                killed++;
            }
        }

        private void PromoteFromQueue()
        {
            int effectiveMax = Mathf.Max(1, Mathf.RoundToInt(FireConfig.MaxConcurrentBurning.Value * GetRampFraction()));
            while (_burning.Count < effectiveMax)
            {
                ZDOID next = _queue.DequeueNextValid();
                if (next.Equals(ZDOID.None)) return;
                if (_burning.ContainsKey(next)) continue;

                Component target = ValheimBridge.ComponentFromZdoid(next);
                if (target == null || !ValheimBridge.IsAlive(target) || !ValheimBridge.IsBurnable(target)) continue;

                StartBurning(target, next);
            }
        }

        // ---------------------------------------------------------------
        // Cycle steps — ground fire
        // ---------------------------------------------------------------

        private void ExpireGroundTimers()
        {
            _groundScratch.Clear();
            float now = Time.time;
            foreach (KeyValuePair<GroundCellKey, GroundCellState> kv in _groundBurning)
            {
                if (now >= kv.Value.ExpireAt) _groundScratch.Add(kv.Key);
            }
            foreach (GroundCellKey key in _groundScratch)
            {
                float y = _groundBurning[key].Y;
                _groundBurning.Remove(key);
                if (FireConfig.GroundFuelExhaustionEnabled.Value)
                    _groundExhausted[key] = now + FireConfig.GroundFuelRegrowSeconds.Value;
                RemoveGroundVfxFor(key);
                LeaveScorchMark(key, y);
                FireLogger.Debug($"Ground burned out at cell ({key.X},{key.Z})");
                _groundExpiredSinceFlush.Add(key);
            }

            // Prune exhaustion entries whose regrow window has passed. Without this,
            // a long player-sustained fire (one that never fully dies) would keep
            // _groundExhausted growing for the entire session with no eviction —
            // this is what caused the runaway entry count.
            if (FireConfig.GroundFuelExhaustionEnabled.Value && _groundExhausted.Count > 0)
            {
                _exhaustedScratch.Clear();
                foreach (KeyValuePair<GroundCellKey, float> kv in _groundExhausted)
                {
                    if (now >= kv.Value) _exhaustedScratch.Add(kv.Key);
                }
                foreach (GroundCellKey key in _exhaustedScratch)
                {
                    _groundExhausted.Remove(key);
                }
            }
        }

        /// <summary>
        /// Sweeps pending tree regrowth entries and attempts to spawn any that are
        /// due. A spot blocked by IsAreaReady (e.g. something built there since)
        /// retries on a short backoff up to MaxRegrowthAttempts, then gives up —
        /// small-scope by design: no stump placeholder, no cross-restart
        /// persistence, same species only.
        /// </summary>
        /// <summary>
        /// Debug/test hook: forces every pending regrowth entry to attempt right
        /// now instead of waiting out its timer, then returns (attempted, stillPending)
        /// so a console command can report what happened without a 15-minute wait.
        /// </summary>
        /// <summary>Debug hook: snapshot of pending regrowth entries for console inspection.</summary>
        public List<string> DumpPendingRegrowth()
        {
            var lines = new List<string>();
            foreach (PendingRegrowth entry in _pendingRegrowth)
            {
                float secondsLeft = entry.RegrowAt - Time.time;
                lines.Add($"{entry.PrefabName} at {entry.Position} — " +
                          $"{(secondsLeft > 0 ? $"{secondsLeft:F0}s left" : "due")}, attempts {entry.Attempts}");
            }
            return lines;
        }

        public (int attempted, int stillPending) ForceTreeRegrowthNow()
        {
            int attempted = _pendingRegrowth.Count;
            for (int i = 0; i < _pendingRegrowth.Count; i++)
            {
                PendingRegrowth entry = _pendingRegrowth[i];
                entry.RegrowAt = Time.time;
                _pendingRegrowth[i] = entry;
            }
            ProcessTreeRegrowth();
            return (attempted, _pendingRegrowth.Count);
        }

        private void ProcessTreeRegrowth()
        {
            if (_pendingRegrowth.Count == 0) return;

            const float retryBackoffSeconds = 30f;
            const int maxAttempts = 20; // ~10 minutes of retrying a blocked spot before giving up

            float now = Time.time;
            _regrowthScratchIndices.Clear();

            for (int i = 0; i < _pendingRegrowth.Count; i++)
            {
                PendingRegrowth entry = _pendingRegrowth[i];
                if (now < entry.RegrowAt) continue;

                bool spawned = ValheimBridge.TrySpawnTree(entry.PrefabName, entry.Position);
                if (spawned)
                {
                    FireLogger.Debug($"Tree regrew: {entry.PrefabName} at {entry.Position}");
                    _treesRegrownCount++;
                    _regrowthScratchIndices.Add(i);
                    continue;
                }

                entry.Attempts++;
                if (entry.Attempts >= maxAttempts)
                {
                    FireLogger.Debug($"Tree regrowth gave up after {entry.Attempts} attempts " +
                                      $"(spot likely blocked): {entry.PrefabName} at {entry.Position}");
                    _regrowthScratchIndices.Add(i);
                }
                else
                {
                    entry.RegrowAt = now + retryBackoffSeconds;
                    _pendingRegrowth[i] = entry;
                }
            }

            // Remove completed/abandoned entries back-to-front so indices stay valid.
            for (int i = _regrowthScratchIndices.Count - 1; i >= 0; i--)
            {
                _pendingRegrowth.RemoveAt(_regrowthScratchIndices[i]);
            }
        }

        /// <summary>
        /// Flushes queued burned-cell positions to the batched real-dirt painter
        /// once per PaintFlushInterval. On total failure (e.g. no TerrainComp in
        /// the zone yet) the positions are dropped rather than retried forever —
        /// the procedural decal already covered the visual, real paint is a bonus.
        /// </summary>
        private void FlushPendingPaint()
        {
            if (_pendingPaint.Count == 0) return;
            if (Time.time < _nextPaintFlush) return;
            _nextPaintFlush = Time.time + PaintFlushInterval;

            int painted = ValheimBridge.TryPaintScorchedDirtBatch(_pendingPaint, FireConfig.DirtPaintRadius.Value);
            if (painted < _pendingPaint.Count)
            {
                FireLogger.Debug($"Paint flush: {painted}/{_pendingPaint.Count} positions painted " +
                                  "(rest had no TerrainComp or threw; dropped, decal already covers them).");
            }
            _pendingPaint.Clear();
        }

        /// <summary>
        /// Server-side: packs accumulated ground-fire ignite/expire deltas into a
        /// ZPackage and broadcasts once per second. Only the server should ever
        /// have anything in these lists — TryIgniteGroundCell/ExpireGroundTimers
        /// only run as part of the server-only simulation loop — but the empty-
        /// check makes this a no-op on clients regardless.
        /// </summary>
        private void FlushGroundFireSync()
        {
            if (_groundIgnitedSinceFlush.Count == 0 && _groundExpiredSinceFlush.Count == 0) return;
            if (Time.time < _nextGroundSyncFlush) return;
            _nextGroundSyncFlush = Time.time + GroundSyncFlushInterval;

            var pkg = new ZPackage();
            pkg.Write(_groundIgnitedSinceFlush.Count);
            foreach ((GroundCellKey key, float y) in _groundIgnitedSinceFlush)
            {
                pkg.Write(key.X);
                pkg.Write(key.Z);
                pkg.Write(y);
            }
            pkg.Write(_groundExpiredSinceFlush.Count);
            foreach (GroundCellKey key in _groundExpiredSinceFlush)
            {
                pkg.Write(key.X);
                pkg.Write(key.Z);
            }

            ValheimBridge.BroadcastGroundFireSync(pkg);
            _groundIgnitedSinceFlush.Clear();
            _groundExpiredSinceFlush.Clear();
        }

        /// <summary>
        /// Client-side: unpacks a batched ground-fire delta and spawns/removes
        /// local, non-authoritative, no-damage ground VFX to match. Server no-ops
        /// (it already has the real ground VFX from its own simulation).
        /// </summary>
        private void HandleGroundFireSync(long sender, ZPackage pkg)
        {
            if (ValheimBridge.IsServer()) return;

            int ignitedCount = pkg.ReadInt();
            int expiredCountPeek = 0; // logged after reading, just for the trace line below
            FireLogger.Debug($"[IGNITE-TRACE] HandleGroundFireSync received from peer {sender}: {ignitedCount} ignited entries.");
            for (int i = 0; i < ignitedCount; i++)
            {
                int x = pkg.ReadInt();
                int z = pkg.ReadInt();
                float y = pkg.ReadSingle();
                SpawnRemoteGroundVfxFor(new GroundCellKey(x, z), CellCenter(new GroundCellKey(x, z), y));
            }

            int expiredCount = pkg.ReadInt();
            expiredCountPeek = expiredCount;
            FireLogger.Debug($"[IGNITE-TRACE] HandleGroundFireSync: {expiredCountPeek} expired entries. " +
                              $"_remoteGroundVfx now holds {_remoteGroundVfx.Count} entries.");
            for (int i = 0; i < expiredCount; i++)
            {
                int x = pkg.ReadInt();
                int z = pkg.ReadInt();
                RemoveRemoteGroundVfxFor(new GroundCellKey(x, z));
            }
        }

        private void SpawnRemoteGroundVfxFor(GroundCellKey key, Vector3 position)
        {
            if (_remoteGroundVfx.ContainsKey(key)) return;
            if (!FireConfig.UseProceduralVfx.Value)
            {
                FireLogger.Debug($"[IGNITE-TRACE] SpawnRemoteGroundVfxFor({key.X},{key.Z}): UseProceduralVfx is false on THIS peer, skipping.");
                return;
            }

            GameObject instance = ValheimBridge.CreateProceduralGroundFireVfx(position);
            if (instance != null)
            {
                _remoteGroundVfx[key] = instance;
            }
            else
            {
                FireLogger.Debug($"[IGNITE-TRACE] SpawnRemoteGroundVfxFor({key.X},{key.Z}): CreateProceduralGroundFireVfx returned null.");
            }
        }

        private void RemoveRemoteGroundVfxFor(GroundCellKey key)
        {
            if (_remoteGroundVfx.TryGetValue(key, out GameObject instance))
            {
                _remoteGroundVfx.Remove(key);
                if (instance != null) Destroy(instance);
            }
        }

        private void LeaveScorchMark(GroundCellKey key, float y)
        {
            Vector3 position = CellCenter(key, y);

            if (FireConfig.UseVanillaDirtPaint.Value && !_groundPainted.Contains(key))
            {
                // Queue for the next batched flush (see FlushPendingPaint) rather
                // than painting immediately: paints are grouped by zone TerrainComp
                // and committed with one Save() per comp per flush. Marked painted
                // now so a reigniting neighbor can't double-queue the same cell
                // before the flush fires. Deliberately falls through to the decal
                // below: it gives instant visual feedback and covers the case where
                // the batched paint later fails (no TerrainComp in zone) — and it
                // self-expires via ScorchMarkLifetimeSeconds, so there's no
                // double-visual buildup once the real paint lands.
                _pendingPaint.Add(position);
                _groundPainted.Add(key);
            }

            if (!FireConfig.ScorchMarksEnabled.Value) return;
            float size = FireConfig.GroundCellSize.Value * 1.5f;
            ValheimBridge.SpawnScorchMark(position, size, FireConfig.ScorchMarkLifetimeSeconds.Value);
        }

        // ---------------------------------------------------------------
        // Spread — both directions, every cycle
        // ---------------------------------------------------------------

        private float _nextSpreadDiagnosticLog;
        private const float SpreadDiagnosticInterval = 5f;

        /// <summary>
        /// Periodic (not one-shot) visibility into whether structure-to-structure
        /// spread has any real candidates to work with at all. A one-time version
        /// of this caught WearNTear.AllPieces.Count=0 on the very first spread
        /// cycle after a force-created ignition — but a single reading can't
        /// distinguish "the vanilla list is broken here" from "it just hadn't
        /// been populated by Unity's Awake() yet on that exact frame." Logging
        /// every few seconds for as long as a fire burns answers that
        /// definitively: if the count stays 0 for the whole burn, that's a real
        /// platform-specific failure (same class as GetGroundHeight and
        /// GetCharacterLayerMask, both confirmed broken here already). If it
        /// becomes nonzero after the first reading or two, it was just a
        /// one-frame startup race, self-resolving and not worth chasing further.
        /// </summary>
        private void LogSpreadCandidateDiagnostic(float objRadiusSqr)
        {
            if (_burning.Count == 0) return;
            if (Time.time < _nextSpreadDiagnosticLog) return;
            _nextSpreadDiagnosticLog = Time.time + SpreadDiagnosticInterval;

            int pieceCount = ValheimBridge.AllPieces.Count;
            float nearestSqr = float.MaxValue;
            string nearestName = "<none>";
            int candidatesInRange = 0;

            foreach (BurningState burnerState in _burning.Values)
            {
                Vector3 origin = burnerState.Position;
                for (int i = 0; i < _candidates.Count; i++)
                {
                    Component candidate = _candidates[i];
                    if (candidate == null) continue;
                    float distSqr = (ValheimBridge.PositionOf(candidate) - origin).sqrMagnitude;
                    if (distSqr <= objRadiusSqr) candidatesInRange++;
                    if (distSqr < nearestSqr)
                    {
                        nearestSqr = distSqr;
                        nearestName = ValheimBridge.NameOf(candidate);
                    }
                }
                break; // one burner's worth is enough to answer the question
            }

            float nearestDist = nearestSqr == float.MaxValue ? -1f : Mathf.Sqrt(nearestSqr);
            FireLogger.Info($"[SPREAD-DIAGNOSTIC] WearNTear.AllPieces.Count={pieceCount}, " +
                             $"total candidates (pieces+trees+logs)={_candidates.Count}, " +
                             $"candidates within SpreadRadius of first burner={candidatesInRange}, " +
                             $"nearest candidate='{nearestName}' at {nearestDist:F2}m " +
                             $"(SpreadRadius={FireConfig.SpreadRadius.Value}m). " +
                             "If candidatesInRange is 0 and nearestDist is well outside SpreadRadius, " +
                             "there simply wasn't a burnable piece close enough — not a bug. If AllPieces.Count " +
                             "looks wrong (0, or missing pieces you know are placed), that's the real lead.");
        }

        private void SpreadPass()
        {
            if (_burning.Count == 0 && _groundBurning.Count == 0) return;

            float ramp = GetRampFraction();
            float effectiveSpreadRadius = FireConfig.SpreadRadius.Value * ramp;
            float objRadiusSqr = effectiveSpreadRadius * effectiveSpreadRadius;
            float groundRadius = FireConfig.GroundSpreadRadius.Value * ramp;

            BuildCandidateList();
            LogSpreadCandidateDiagnostic(objRadiusSqr);

            // --- object burners: ignite nearby objects + seed nearby ground cells ---
            // Uses the cached Position from BurningState directly — no live
            // Component resolution needed for the burner side of this check at
            // all, since position never changes for a static piece/tree. This
            // also avoids force-creating a burner's GameObject every single
            // spread cycle just to read a position we already have cached.
            _scratch.Clear();
            _scratch.AddRange(_burning.Keys);
            foreach (ZDOID burnerId in _scratch)
            {
                if (!_burning.TryGetValue(burnerId, out BurningState burnerState)) continue;
                Vector3 origin = burnerState.Position;

                for (int i = 0; i < _candidates.Count; i++)
                {
                    Component candidate = _candidates[i];
                    if (candidate == null) continue;
                    if (!ValheimBridge.IsBurnable(candidate)) continue;

                    ZDOID? candidateId = ValheimBridge.ZDOIDOf(candidate);
                    if (!candidateId.HasValue) continue;
                    if (candidateId.Value.Equals(burnerId)) continue;
                    if (_burning.ContainsKey(candidateId.Value)) continue;

                    if ((ValheimBridge.PositionOf(candidate) - origin).sqrMagnitude <= objRadiusSqr)
                    {
                        TryIgnite(candidate);
                    }
                }

                IgniteGroundNear(origin, groundRadius);
            }

            // --- ground burners: ignite nearby ground cells + nearby objects ---
            if (_groundBurning.Count > 0)
            {
                _groundScratch.Clear();
                _groundScratch.AddRange(_groundBurning.Keys);
                foreach (GroundCellKey key in _groundScratch)
                {
                    float y = _groundBurning[key].Y;
                    Vector3 origin = CellCenter(key, y);

                    IgniteAdjacentGroundCells(key, y);

                    for (int i = 0; i < _candidates.Count; i++)
                    {
                        Component candidate = _candidates[i];
                        if (candidate == null) continue;
                        if (!ValheimBridge.IsBurnable(candidate)) continue;

                        ZDOID? candidateId = ValheimBridge.ZDOIDOf(candidate);
                        if (!candidateId.HasValue) continue;
                        if (_burning.ContainsKey(candidateId.Value)) continue;

                        if ((ValheimBridge.PositionOf(candidate) - origin).sqrMagnitude <= groundRadius * groundRadius)
                        {
                            TryIgnite(candidate);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Rebuilds the full spread-candidate pool: all vanilla-tracked pieces,
        /// plus (if enabled) all currently-loaded trees and logs via a scene scan.
        /// The scan cost is bounded by what's actually instantiated nearby, but
        /// scales with SpreadCheckInterval — very low intervals with trees enabled
        /// in a dense forest is the case to watch if performance dips.
        /// </summary>
        private void BuildCandidateList()
        {
            _candidates.Clear();

            List<WearNTear> pieces = ValheimBridge.AllPieces;
            for (int i = 0; i < pieces.Count; i++) _candidates.Add(pieces[i]);

            if (FireConfig.BurnTreesAndLogs.Value)
            {
                TreeBase[] trees = Object.FindObjectsOfType<TreeBase>();
                for (int i = 0; i < trees.Length; i++) _candidates.Add(trees[i]);

                TreeLog[] logs = Object.FindObjectsOfType<TreeLog>();
                for (int i = 0; i < logs.Length; i++) _candidates.Add(logs[i]);
            }
        }
    }
}