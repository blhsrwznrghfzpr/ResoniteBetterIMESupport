using FrooxEngine;
using HarmonyLib;
using Renderite.Shared;

namespace ResoniteBetterIMESupport.Engine.Patches;

[HarmonyPatch(typeof(ScreenModeController), "OnCommonUpdate")]
static class ScreenModeControllerOnCommonUpdatePatch
{
    static bool Prefix(ScreenModeController __instance)
    {
        if (!EngineIMEPatch.ShouldSuppressScreenModeToggle || !__instance.InputInterface.GetKeyDown(Key.F8))
            return true;

        EnginePlugin.LogDebugIme($"ScreenModeController.OnCommonUpdate suppressed F8 screen mode toggle while IME composition is active. {EngineIMEPatch.DebugState}");
        return false;
    }
}
