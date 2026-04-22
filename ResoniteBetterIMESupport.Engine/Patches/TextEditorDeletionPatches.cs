using FrooxEngine;
using HarmonyLib;

namespace ResoniteBetterIMESupport.Engine.Patches;

[HarmonyPatch(typeof(TextEditor), "DeleteSelection")]
static class TextEditorDeleteSelectionPatch
{
    static bool Prefix()
    {
        var suppress = EngineIMEPatch.ShouldSuppressTextEditorDeletion;
        EnginePlugin.Log.LogInfo($"[IME debug] TextEditor.DeleteSelection Prefix suppress={suppress}, {EngineIMEPatch.DebugState}");
        return !suppress;
    }
}

[HarmonyPatch(typeof(TextEditor), "Delete")]
static class TextEditorDeletePatch
{
    static bool Prefix()
    {
        var suppress = EngineIMEPatch.ShouldSuppressTextEditorDeletion;
        EnginePlugin.Log.LogInfo($"[IME debug] TextEditor.Delete Prefix suppress={suppress}, {EngineIMEPatch.DebugState}");
        return !suppress;
    }
}

[HarmonyPatch(typeof(TextEditor), "Backspace")]
static class TextEditorBackspacePatch
{
    static bool Prefix()
    {
        var suppress = EngineIMEPatch.ShouldSuppressTextEditorDeletion;
        EnginePlugin.Log.LogInfo($"[IME debug] TextEditor.Backspace Prefix suppress={suppress}, {EngineIMEPatch.DebugState}");
        return !suppress;
    }
}
