using FrooxEngine;
using HarmonyLib;
using Renderite.Shared;
using System.Reflection;
using System.Reflection.Emit;

namespace ResoniteBetterIMESupport.Engine.Patches;

[HarmonyPatch(typeof(ScreenModeController), "OnCommonUpdate")]
static class ScreenModeControllerPatch
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var getKeyDownMethod = AccessTools.Method(typeof(InputInterface), nameof(InputInterface.GetKeyDown));
        var replacementMethod = AccessTools.Method(typeof(ScreenModeControllerPatch), nameof(GetKeyDown));

        foreach (var instruction in instructions)
        {
            if (instruction.Calls(getKeyDownMethod))
                yield return new CodeInstruction(OpCodes.Call, replacementMethod);
            else
                yield return instruction;
        }
    }

    static bool GetKeyDown(InputInterface inputInterface, Key key)
    {
        var isDown = inputInterface.GetKeyDown(key);
        if (!isDown || !EngineIMEPatch.ShouldSuppressScreenModeToggleKey(key))
            return isDown;

        EngineIMEPatch.LogSuppressedScreenModeToggleKey(key, "ScreenModeController.OnCommonUpdate");
        return false;
    }
}
