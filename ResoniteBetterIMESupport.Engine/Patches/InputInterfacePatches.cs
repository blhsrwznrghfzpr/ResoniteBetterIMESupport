using FrooxEngine;
using HarmonyLib;
using Renderite.Shared;
using ResoniteBetterIMESupport.Shared;
using System.Reflection;

namespace ResoniteBetterIMESupport.Engine.Patches;

[HarmonyPatch(typeof(InputInterface), nameof(InputInterface.ShowKeyboard))]
static class InputInterfaceShowKeyboardPatch
{
    static void Postfix(IText targetText) => EngineIMEPatch.SetEditingText(targetText);
}

[HarmonyPatch(typeof(InputInterface), nameof(InputInterface.HideKeyboard))]
static class InputInterfaceHideKeyboardPatch
{
    static void Postfix() => EngineIMEPatch.ClearEditingText();
}

[HarmonyPatch]
static class InputInterfaceUpdateKeyboardStatePatch
{
    static readonly MethodInfo? TypeDeltaSetter = AccessTools.PropertySetter(typeof(InputInterface), nameof(InputInterface.TypeDelta));

    static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(InputInterface), "UpdateKeyboardState") ?? throw new InvalidOperationException("InputInterface.UpdateKeyboardState was not found.");

    static void Postfix(InputInterface __instance, KeyboardState keyboardState)
    {
        var typeDelta = keyboardState.typeDelta ?? string.Empty;
        var filteredTypeDelta = typeDelta;

        if (EngineIMEPatch.TryFilterKeyboardTypeDelta(typeDelta, out var replacementTypeDelta))
        {
            filteredTypeDelta = replacementTypeDelta;
            TypeDeltaSetter?.Invoke(__instance, new object[] { replacementTypeDelta });
        }

        if (!RenderiteCompositionContract.TryGet(keyboardState, out var active, out var composition, out var selectionStart, out var selectionLength))
            return;

        if (EngineIMEPatch.ApplyKeyboardStateComposition(active, composition, selectionStart, selectionLength, filteredTypeDelta))
            TypeDeltaSetter?.Invoke(__instance, new object[] { string.Empty });
    }
}

[HarmonyPatch(typeof(InputInterface), nameof(InputInterface.GetKeyRepeat))]
static class InputInterfaceGetKeyRepeatPatch
{
    static void Postfix(Key key, ref bool __result)
    {
        if (!__result || !EngineIMEPatch.ShouldSuppressTextEditorKey(key))
            return;

        __result = false;
    }
}
