using FrooxEngine;
using HarmonyLib;
using Renderite.Shared;
using ResoniteBetterIMESupport.Shared;
using System.Reflection;

namespace ResoniteBetterIMESupport.Engine.Patches;

[HarmonyPatch(typeof(InputInterface), nameof(InputInterface.ShowKeyboard))]
static class InputInterfaceShowKeyboardPatch
{
    static void Postfix(IText? targetText)
    {
        try
        {
            EngineIMEPatch.SetEditingText(targetText);
        }
        catch (Exception ex)
        {
            EnginePlugin.Log.LogError($"IME ShowKeyboard postfix failed. Leaving Resonite editing flow untouched.\n{ex}");
        }
    }
}

[HarmonyPatch(typeof(InputInterface), nameof(InputInterface.HideKeyboard))]
static class InputInterfaceHideKeyboardPatch
{
    static void Postfix()
    {
        try
        {
            EngineIMEPatch.ClearEditingText();
        }
        catch (Exception ex)
        {
            EnginePlugin.Log.LogError($"IME HideKeyboard postfix failed.\n{ex}");
        }
    }
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

        if (EngineIMEPatch.ApplyKeyboardStateComposition(active, composition, selectionStart, selectionLength, filteredTypeDelta, GetImeEditAction(keyboardState)))
            TypeDeltaSetter?.Invoke(__instance, new object[] { string.Empty });
    }

    static ImeEditAction GetImeEditAction(KeyboardState keyboardState)
    {
        if (keyboardState.heldKeys == null)
            return ImeEditAction.None;

        if (keyboardState.heldKeys.Contains(Key.Delete))
            return ImeEditAction.Delete;

        if (keyboardState.heldKeys.Contains(Key.Backspace))
            return ImeEditAction.Backspace;

        return ImeEditAction.None;
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
