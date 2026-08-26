using FireFront.Fire;
using FireFront.Utils;
using HarmonyLib;

namespace FireFront.Patches
{
    /// <summary>
    /// Ignition trigger for felled logs. PREFIX for the same reason as
    /// TreeBaseRpcDamagePatch. Logs are killed via their own Destroy(HitData)
    /// method, not by re-invoking RPC_Damage, so the suppression check isn't
    /// strictly needed here today — included anyway so this stays correct if
    /// that ever changes.
    ///
    /// Signature verified: TreeLog.RPC_Damage(long sender, HitData hit)
    /// </summary>
    [HarmonyPatch(typeof(TreeLog), nameof(TreeLog.RPC_Damage))]
    public static class TreeLogRpcDamagePatch
    {
        [HarmonyPrefix]
        public static void Prefix(TreeLog __instance, HitData hit)
        {
            if (FireManager.Instance == null)
            {
                FireLogger.Debug("[IGNITE-TRACE] TreeLog.RPC_Damage: FireManager.Instance is null, bailing.");
                return;
            }
            if (ValheimBridge.SuppressIgnition.Contains(__instance))
            {
                FireLogger.Debug($"[IGNITE-TRACE] TreeLog.RPC_Damage on {ValheimBridge.NameOf(__instance)}: suppressed (already being killed by us).");
                return;
            }

            float fireDamage = ValheimBridge.FireDamageOf(hit);
            FireLogger.Debug($"[IGNITE-TRACE] TreeLog.RPC_Damage on {ValheimBridge.NameOf(__instance)}: fire damage = {fireDamage}");
            if (fireDamage <= 0f) return;

            // See WearNTearRpcDamagePatch for why this branches on server
            // authority — RPC_Damage runs on whichever peer owns the ZDO, which
            // is very often a client, not the server.
            if (ValheimBridge.IsServer())
            {
                FireLogger.Debug($"[IGNITE-TRACE] IsServer=True — igniting {ValheimBridge.NameOf(__instance)} directly.");
                FireManager.Instance.TryIgnite(__instance);
            }
            else
            {
                ZDOID? id = ValheimBridge.ZDOIDOf(__instance);
                if (id.HasValue)
                {
                    FireLogger.Debug($"[IGNITE-TRACE] IsServer=False — forwarding ignite request for {ValheimBridge.NameOf(__instance)}, ZDOID={id.Value}.");
                    ValheimBridge.SendIgniteRequestToServer(id.Value);
                }
                else
                {
                    FireLogger.Debug($"[IGNITE-TRACE] IsServer=False — couldn't resolve ZDOID for {ValheimBridge.NameOf(__instance)}, request NOT sent.");
                }
            }
        }
    }
}
