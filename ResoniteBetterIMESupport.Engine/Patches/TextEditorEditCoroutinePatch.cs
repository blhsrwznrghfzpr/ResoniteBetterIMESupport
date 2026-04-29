using FrooxEngine;
using HarmonyLib;
using Renderite.Shared;
using ResoniteBetterIMESupport.Shared;
using System.Reflection;
using System.Reflection.Emit;

namespace ResoniteBetterIMESupport.Engine.Patches;

[HarmonyPatch]
static class TextEditorEditCoroutinePatch
{
    static MethodBase TargetMethod()
    {
        var nestedType = typeof(TextEditor)
            .GetNestedTypes(BindingFlags.Instance | BindingFlags.NonPublic)
            .FirstOrDefault(type => type.Name.StartsWith("<EditCoroutine>", StringComparison.Ordinal));

        if (nestedType == null)
            throw new InvalidOperationException("TextEditor.EditCoroutine state machine type was not found.");

        return AccessTools.Method(nestedType, "MoveNext") ?? throw new InvalidOperationException("TextEditor.EditCoroutine.MoveNext was not found.");
    }

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = instructions.ToList();
        var getKeyRepeatMethod = AccessTools.Method(typeof(InputInterface), nameof(InputInterface.GetKeyRepeat));
        int[] arrowKeys =
        {
            (int)Key.UpArrow,
            (int)Key.DownArrow,
            (int)Key.RightArrow,
            (int)Key.LeftArrow,
        };

        for (var i = 0; i < codes.Count; i++)
        {
            if (i + 1 < codes.Count
                && codes[i].opcode == OpCodes.Ldloc_3
                && codes[i + 1].opcode == OpCodes.Brfalse_S)
            {
                codes.Insert(i + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TextEditorEditCoroutinePatch), nameof(IsStringChanged))));
                break;
            }

            if (i > 0
                && codes[i].Calls(getKeyRepeatMethod)
                && codes[i - 1].opcode == OpCodes.Ldc_I4
                && arrowKeys.Contains((int)codes[i - 1].operand))
            {
                codes[i] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TextEditorEditCoroutinePatch), nameof(GetKeyRepeat)));
            }
        }

        return codes;
    }

    static bool IsStringChanged(bool original) => EngineIMEPatch.ConsumeStringChanged() || original;

    static bool GetKeyRepeat(InputInterface inputInterface, Key key)
    {
        if (EngineIMEPatch.IsTypingUnsettled)
            return false;

        return inputInterface.GetKeyRepeat(key);
    }
}
