using FireFront.Fire;
using FireFront.Utils;
using HarmonyLib;

namespace FireFront.Patches
{
    /// <summary>
    /// The ONLY ignition trigger. Vanilla fire damage (Ashlands Fire component,
    /// fire arrows, staff of embers, cinders, etc.) landing on a piece with
    /// m_burnable = true registers it with FireManager. No new ignition sources,
    /// no flammability changes, no drop logic changes — vanilla-preserving scope.
    ///
    /// PREFIX, not postfix: read the raw incoming fire damage before the
    /// original method has any chance to mutate/resist the shared HitData object.
    /// Pieces are killed via Destroy(), not by re-invoking RPC_Damage, so the
    /// suppression check isn't strictly needed here today — included anyway so
    /// this stays correct if that ever changes.
    ///
    /// Signature verified: WearNTear.RPC_Damage(long sender, HitData hit)
    /// </summary>
    [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.RPC_Damage))]
    public static class WearNTearRpcDamagePatch
    {
        [HarmonyPrefix]
        public static void Prefix(WearNTear __instance, HitData hit)
        {
            if (FireManager.Instance == null)
            {
                FireLogger.Debug("[IGNITE-TRACE] WearNTear.RPC_Damage: FireManager.Instance is null, bailing.");
                return;
            }
            if (ValheimBridge.SuppressIgnition.Contains(__instance))
            {
                FireLogger.Debug($"[IGNITE-TRACE] WearNTear.RPC_Damage on {ValheimBridge.NameOf(__instance)}: suppressed (already being killed by us).");
                return;
            }

            float fireDamage = ValheimBridge.FireDamageOf(hit);
            FireLogger.Debug($"[IGNITE-TRACE] WearNTear.RPC_Damage on {ValheimBridge.NameOf(__instance)}: fire damage = {fireDamage}");
            if (fireDamage <= 0f) return;

            // RPC_Damage runs on whichever peer currently owns this object's ZDO —
            // very often a nearby client, not the server (confirmed by a real
            // dedicated-server test where the server never learned about a fire
            // that a connected client's RPC_Damage had ignited locally). Only the
            // server should run FireManager's simulation; everyone else forwards
            // the request instead of igniting on their own copy.
            // The ATTACKER, not the RPC sender: RPC_Damage runs on the object's owner,
            // and the owner is not the arsonist when someone torches a piece in another
            // player's loaded area. 0 = natural/creature fire, attributed to nobody.
            long igniter = ValheimBridge.AttackerPlayerId(hit);

            if (ValheimBridge.IsServer())
            {
                FireLogger.Debug($"[IGNITE-TRACE] IsServer=True — igniting {ValheimBridge.NameOf(__instance)} directly (igniter={igniter}).");
                FireManager.Instance.TryIgnite(__instance, igniter);
            }
            else
            {
                ZDOID? id = ValheimBridge.ZDOIDOf(__instance);
                if (id.HasValue)
                {
                    FireLogger.Debug($"[IGNITE-TRACE] IsServer=False — forwarding ignite request for {ValheimBridge.NameOf(__instance)}, ZDOID={id.Value}.");
                    ValheimBridge.SendIgniteRequestToServer(id.Value, igniter);
                }
                else
                {
                    FireLogger.Debug($"[IGNITE-TRACE] IsServer=False — couldn't resolve ZDOID for {ValheimBridge.NameOf(__instance)}, request NOT sent.");
                }
            }
        }
    }
}
