using FireFront.Fire;
using FireFront.Utils;
using HarmonyLib;

namespace FireFront.Patches
{
    /// <summary>
    /// Ignition trigger for standing trees.
    ///
    /// PREFIX is required here (not postfix): TreeBase carries a per-species
    /// m_damageModifiers resistance table that RPC_Damage applies to the
    /// incoming HitData — a postfix would read an already-zeroed fire value.
    ///
    /// Suppression check is required too: killing a burned-out tree re-invokes
    /// this exact method with a synthetic lethal fire hit (see
    /// ValheimBridge.KillBurningTarget), which would otherwise re-ignite the
    /// tree we're trying to finish off.
    ///
    /// Signature verified: TreeBase.RPC_Damage(long sender, HitData hit)
    /// </summary>
    [HarmonyPatch(typeof(TreeBase), nameof(TreeBase.RPC_Damage))]
    public static class TreeBaseRpcDamagePatch
    {
        [HarmonyPrefix]
        public static void Prefix(TreeBase __instance, HitData hit)
        {
            if (FireManager.Instance == null)
            {
                FireLogger.Debug("[IGNITE-TRACE] TreeBase.RPC_Damage: FireManager.Instance is null, bailing.");
                return;
            }
            if (ValheimBridge.SuppressIgnition.Contains(__instance))
            {
                FireLogger.Debug($"[IGNITE-TRACE] TreeBase.RPC_Damage on {ValheimBridge.NameOf(__instance)}: suppressed (already being killed by us).");
                return;
            }

            float fireDamage = ValheimBridge.FireDamageOf(hit);
            FireLogger.Debug($"[IGNITE-TRACE] TreeBase.RPC_Damage on {ValheimBridge.NameOf(__instance)}: fire damage = {fireDamage}");
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
