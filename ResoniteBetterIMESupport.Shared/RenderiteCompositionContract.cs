using Renderite.Shared;
using System.Reflection;

namespace ResoniteBetterIMESupport.Shared;

internal static class RenderiteCompositionContract
{
    static readonly Type KeyboardStateType = typeof(KeyboardState);

    static readonly FieldInfo? CompositionActiveField = KeyboardStateType.GetField("compositionActive");
    static readonly FieldInfo? CompositionTextField = KeyboardStateType.GetField("compositionText");
    static readonly FieldInfo? CompositionSelectionStartField = KeyboardStateType.GetField("compositionSelectionStart");
    static readonly FieldInfo? CompositionSelectionLengthField = KeyboardStateType.GetField("compositionSelectionLength");
    static readonly FieldInfo? CompositionCandidatesField = KeyboardStateType.GetField("compositionCandidates");
    static readonly FieldInfo? CompositionCandidateIndexField = KeyboardStateType.GetField("compositionCandidateIndex");

    public static bool IsSupported =>
        CompositionActiveField != null
        && CompositionTextField != null
        && CompositionSelectionStartField != null
        && CompositionSelectionLengthField != null
        && CompositionCandidatesField != null
        && CompositionCandidateIndexField != null;

    public static bool TryGet(
        object keyboardState,
        out bool active,
        out string composition,
        out int selectionStart,
        out int selectionLength)
    {
        active = false;
        composition = string.Empty;
        selectionStart = 0;
        selectionLength = 0;

        if (!IsSupported)
            return false;

        active = CompositionActiveField!.GetValue(keyboardState) is bool activeValue && activeValue;
        composition = active ? CompositionTextField!.GetValue(keyboardState) as string ?? string.Empty : string.Empty;
        selectionStart = CompositionSelectionStartField!.GetValue(keyboardState) is int startValue ? startValue : 0;
        selectionLength = CompositionSelectionLengthField!.GetValue(keyboardState) is int lengthValue ? lengthValue : 0;
        return true;
    }

    public static void Set(object keyboardState, string composition, int caretOffset)
    {
        if (!IsSupported)
            return;

        var active = !string.IsNullOrEmpty(composition);
        var clampedCaretOffset = active
            ? Math.Max(0, Math.Min(caretOffset < 0 ? composition.Length : caretOffset, composition.Length))
            : 0;

        CompositionActiveField!.SetValue(keyboardState, active);
        CompositionTextField!.SetValue(keyboardState, active ? composition : string.Empty);
        CompositionSelectionStartField!.SetValue(keyboardState, clampedCaretOffset);
        CompositionSelectionLengthField!.SetValue(keyboardState, 0);

        if (CompositionCandidatesField!.GetValue(keyboardState) is System.Collections.IList candidates)
            candidates.Clear();

        CompositionCandidateIndexField!.SetValue(keyboardState, -1);
    }
}
