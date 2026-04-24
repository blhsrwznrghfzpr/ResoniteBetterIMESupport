using HarmonyLib;
using Renderite.Shared;
using System.Reflection;

namespace ResoniteBetterIMESupport.Renderer.Patches;

[HarmonyPatch]
static class KeyboardDriverStartPatch
{
    static MethodBase TargetMethod() => AccessTools.Method(KeyboardDriverIMEPatch.KeyboardDriverType, "Start");

    static void Postfix(object __instance) => KeyboardDriverIMEPatch.Subscribe(__instance);
}

[HarmonyPatch]
static class KeyboardDriverUpdateStatePatch
{
    static MethodBase TargetMethod() => AccessTools.Method(KeyboardDriverIMEPatch.KeyboardDriverType, "UpdateState");

    static void Prefix(object __instance, out int __state)
    {
        __state = KeyboardDriverIMEPatch.OnUpdateStateStarting(__instance);
    }

    static void Postfix(object __instance, KeyboardState state, int __state)
    {
        KeyboardDriverIMEPatch.OnUpdateStateFinished(__instance, __state);

        if (KeyboardDriverIMEPatch.ShouldSuppressTypeDelta(__instance) && __state >= 0)
        {
            KeyboardDriverIMEPatch.LogSuppressedTypeDelta(__instance, __state);
            KeyboardDriverIMEPatch.TrimTypeDelta(__instance, __state);
        }

        if (KeyboardDriverIMEPatch.HasComposition(__instance))
            KeyboardDriverIMEPatch.RemoveIMEEditingKeys(state);
    }
}

[HarmonyPatch]
static class KeyboardDriverHandleOutputStatePatch
{
    static MethodBase TargetMethod() => AccessTools.Method(KeyboardDriverIMEPatch.KeyboardDriverType, "HandleOutputState");

    static void Postfix(object __instance, OutputState output) =>
        KeyboardDriverIMEPatch.HandleKeyboardInputActive(__instance, output.keyboardInputActive);
}
