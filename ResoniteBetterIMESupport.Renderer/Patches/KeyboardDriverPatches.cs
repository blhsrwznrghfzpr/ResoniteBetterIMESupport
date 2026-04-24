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
        var typeDelta = KeyboardDriverIMEPatch.GetTypeDelta(__instance);
        __state = typeDelta?.Length ?? -1;
    }

    static void Postfix(object __instance, KeyboardState state, int __state)
    {
        if (KeyboardDriverIMEPatch.ShouldSuppressTypeDelta(__instance) && __state >= 0)
        {
            KeyboardDriverIMEPatch.LogSuppressedTypeDelta(__instance, __state);
            KeyboardDriverIMEPatch.TrimTypeDelta(__instance, __state);
        }

        if (KeyboardDriverIMEPatch.HasComposition(__instance))
            KeyboardDriverIMEPatch.RemoveIMEEditingKeys(state);

        KeyboardDriverIMEPatch.OnUpdateStateFinished(__instance);
    }
}

[HarmonyPatch]
static class KeyboardDriverHandleOutputStatePatch
{
    static MethodBase TargetMethod() => AccessTools.Method(KeyboardDriverIMEPatch.KeyboardDriverType, "HandleOutputState");

    static void Postfix(object __instance, OutputState output) =>
        KeyboardDriverIMEPatch.HandleKeyboardInputActive(__instance, output.keyboardInputActive);
}
