using FireFront.Commands;
using HarmonyLib;

namespace FireFront.Patches
{
    /// <summary>
    /// Registers FireFront dev commands once the game console initializes.
    /// Signature verified: Terminal.InitTerminal()
    /// </summary>
    [HarmonyPatch(typeof(Terminal), nameof(Terminal.InitTerminal))]
    public static class TerminalInitPatch
    {
        private static bool _registered;

        [HarmonyPostfix]
        public static void Postfix()
        {
            if (_registered) return;
            _registered = true;
            FireDevCommands.RegisterAll();
        }
    }
}
