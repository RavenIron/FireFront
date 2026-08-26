using FireFront.Fire;
using HarmonyLib;

namespace FireFront.Patches
{
    /// <summary>
    /// Keeps FireFront state clean when a piece is removed by ANY means —
    /// player deconstruct, structural collapse, other damage, zone unload.
    /// Signature verified: WearNTear.OnDestroy()
    /// </summary>
    [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.OnDestroy))]
    public static class WearNTearOnDestroyPatch
    {
        [HarmonyPostfix]
        public static void Postfix(WearNTear __instance)
        {
            FireManager.Instance?.HandleTargetRemoved(__instance);
        }
    }
}
