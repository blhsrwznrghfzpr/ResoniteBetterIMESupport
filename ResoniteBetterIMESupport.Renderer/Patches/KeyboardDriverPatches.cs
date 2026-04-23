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
        KeyboardDriverIMEPatch.GetState(__instance).LastUpdateTypeDeltaLength = __state;
    }

    static void Postfix(object __instance, KeyboardState state, int __state)
    {
        if (!KeyboardDriverIMEPatch.HasComposition(__instance))
            return;

        if (__state >= 0)
            KeyboardDriverIMEPatch.TrimTypeDelta(__instance, __state);

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
