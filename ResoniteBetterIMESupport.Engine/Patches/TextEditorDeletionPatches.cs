using FrooxEngine;
using HarmonyLib;

namespace ResoniteBetterIMESupport.Engine.Patches;

[HarmonyPatch(typeof(TextEditor), "DeleteSelection")]
static class TextEditorDeleteSelectionPatch
{
    static bool Prefix() => !EngineIMEPatch.ShouldSuppressTextEditorDeletion;
}

[HarmonyPatch(typeof(TextEditor), "Delete")]
static class TextEditorDeletePatch
{
    static bool Prefix() => !EngineIMEPatch.ShouldSuppressTextEditorDeletion;
}

[HarmonyPatch(typeof(TextEditor), "Backspace")]
static class TextEditorBackspacePatch
{
    static bool Prefix() => !EngineIMEPatch.ShouldSuppressTextEditorDeletion;
}
