using FrooxEngine;
using HarmonyLib;
using Renderite.Shared;

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
