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
static class KeyboardDriverTextInputPatch
{
    static MethodBase TargetMethod() => AccessTools.Method(KeyboardDriverIMEPatch.KeyboardDriverType, "Current_onTextInput");

    static bool Prefix(object __instance, char obj) => KeyboardDriverIMEPatch.ShouldAllowTextInput(__instance, obj);
}

[HarmonyPatch]
static class KeyboardDriverHandleOutputStatePatch
{
    static MethodBase TargetMethod() => AccessTools.Method(KeyboardDriverIMEPatch.KeyboardDriverType, "HandleOutputState");

    static void Postfix(object __instance, OutputState output) =>
        KeyboardDriverIMEPatch.HandleKeyboardInputActive(__instance, output.keyboardInputActive);
}
