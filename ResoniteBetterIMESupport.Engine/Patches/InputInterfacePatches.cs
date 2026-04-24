using FrooxEngine;
using HarmonyLib;
using Renderite.Shared;

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

[HarmonyPatch(typeof(InputInterface), nameof(InputInterface.GetKeyRepeat))]
static class InputInterfaceGetKeyRepeatPatch
{
    static void Postfix(Key key, ref bool __result)
    {
        if (!__result || !EngineIMEPatch.ShouldSuppressTextEditorKey(key))
            return;

        EngineIMEPatch.LogSuppressedTextEditorKey(key);
        __result = false;
    }
}
