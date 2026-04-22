using BepInEx;
using BepInEx.Logging;
using BepInEx.NET.Common;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using HarmonyLib;
using Renderite.Shared;
using ResoniteBetterIMESupport.Shared;
using System.Reflection;
using System.Reflection.Emit;

namespace ResoniteBetterIMESupport.Engine;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class EnginePlugin : BasePlugin
{
    public const string PluginGuid = "dev.yoshi1123.resonite.ResoniteBetterIMESupport.Engine";
    public const string PluginName = "ResoniteBetterIMESupport.Engine";
    public const string PluginVersion = "3.0.0";

    internal static new ManualLogSource Log = null!;

    public override void Load()
    {
        Log = base.Log;
        EngineIMEPatch.Start();
        new Harmony(PluginGuid).PatchAll(Assembly.GetExecutingAssembly());
        Log.LogInfo("ResoniteBetterIMESupport.Engine loaded.");
    }
}

static class EngineIMEPatch
{
    static IText? _editingText;
    static ImePipeServer? _server;
    static bool _stringChanged;
    static bool _isTypingUnsettled;
    static string _composition = string.Empty;
    static int _compositionStart = -1;
    static int _compositionCaretOffset = -1;

    public static bool IsTypingUnsettled => _isTypingUnsettled;

    public static bool ShouldSuppressTextEditorKey(Key key) =>
        _isTypingUnsettled && (key == Key.Backspace || key == Key.Delete);

    public static bool ShouldSuppressTextEditorDeletion => _isTypingUnsettled;

    public static void Start()
    {
        _server = new ImePipeServer(OnMessage);
    }

    public static void SetEditingText(IText text)
    {
        _editingText = text;
        _stringChanged = false;
        _isTypingUnsettled = false;
        _composition = string.Empty;
        _compositionStart = -1;
        _compositionCaretOffset = -1;
    }

    public static void ClearEditingText()
    {
        _editingText = null;
        _stringChanged = false;
        _isTypingUnsettled = false;
        _composition = string.Empty;
        _compositionStart = -1;
        _compositionCaretOffset = -1;
    }

    public static bool ConsumeStringChanged()
    {
        if (!_stringChanged)
            return false;

        _stringChanged = false;
        return true;
    }

    public static bool TryGetCompositionCaretVisual(TextEditingVisuals visuals, out int caretPosition, out colorX caretColor)
    {
        caretPosition = -1;
        caretColor = default;

        if (!HasCompositionRange)
            return false;

        var compositionEnd = _compositionStart + _composition.Length;

        if (visuals.selectionStart != _compositionStart || visuals.caretPosition != compositionEnd)
            return false;

        var caretOffset = ClampCompositionCaretOffset(_compositionCaretOffset, _composition.Length);
        caretPosition = _compositionStart + caretOffset;
        caretColor = visuals.caretColor;
        return true;
    }

    static void OnMessage(ImePipeMessage message)
    {
        var text = _editingText;

        if (text == null)
            return;

        if (message.Composition == _composition && message.CommittedText.Length == 0 && message.CaretOffset == _compositionCaretOffset)
            return;

        text.RunSynchronously(() => ApplyMessage(message), true);
    }

    static void ApplyMessage(ImePipeMessage message)
    {
        if (_editingText == null)
            return;

        RestoreCompositionRangeIfTextEditorDeletedIt();

        if (message.Composition.Length == 0)
        {
            if (HasCompositionRange && message.CommittedText == _composition)
            {
                CaretPosition = _compositionStart + _composition.Length;
            }
            else
            {
                if (HasCompositionRange)
                    DeleteCompositionRange();

                if (message.CommittedText.Length > 0)
                {
                    InsertText(message.CommittedText);
                    _stringChanged = true;
                }
            }

            HasSelection = false;
            _isTypingUnsettled = false;
            _composition = string.Empty;
            _compositionStart = -1;
            _compositionCaretOffset = -1;
            return;
        }

        if (message.Composition == _composition && HasCompositionRange)
        {
            _compositionCaretOffset = ClampCompositionCaretOffset(message.CaretOffset, message.Composition.Length);
            SetCompositionVisuals();
            MarkTextChangeDirty();
            _isTypingUnsettled = true;
            return;
        }

        if (HasCompositionRange)
            DeleteCompositionRange();
        else if (HasSelection)
            DeleteSelection();

        _compositionStart = CaretPosition;
        InsertText(message.Composition);
        _composition = message.Composition;
        _compositionCaretOffset = ClampCompositionCaretOffset(message.CaretOffset, message.Composition.Length);
        SetCompositionVisuals();
        MarkTextChangeDirty();
        _isTypingUnsettled = true;
        _stringChanged = true;
    }

    static void SetCompositionVisuals()
    {
        if (!HasCompositionRange)
            return;

        SelectionStart = _compositionStart;
        CaretPosition = _compositionStart + _composition.Length;
    }

    static void RestoreCompositionRangeIfTextEditorDeletedIt()
    {
        if (_editingText == null || _compositionStart < 0 || _composition.Length == 0 || HasCompositionRange)
            return;

        var insertPosition = Math.Max(0, Math.Min(_compositionStart, _editingText.Text.Length));
        _editingText.Text = _editingText.Text.Insert(insertPosition, _composition);
        CaretPosition = insertPosition + _composition.Length;
        SelectionStart = insertPosition;
    }

    static void MarkTextChangeDirty()
    {
        if (_editingText == null)
            return;

        AccessTools.Method(_editingText.GetType(), "MarkChangeDirty")?.Invoke(_editingText, null);
    }

    static void InsertText(string value)
    {
        if (_editingText == null || value.Length == 0)
            return;

        _editingText.Text = _editingText.Text.Substring(0, CaretPosition) + value + _editingText.Text.Substring(CaretPosition, _editingText.Text.Length - CaretPosition);
        CaretPosition += value.Length;
    }

    static bool HasSelection
    {
        get => _editingText != null && _editingText.SelectionStart != -1;
        set
        {
            if (_editingText == null)
                return;

            _editingText.SelectionStart = value ? CaretPosition : -1;
        }
    }

    static int CaretPosition
    {
        get
        {
            if (_editingText == null)
                return -1;

            return MathX.Clamp(_editingText.CaretPosition, -1, _editingText.Text.Length + 1);
        }
        set
        {
            if (_editingText == null)
                return;

            value = MathX.Clamp(value, 0, _editingText.Text.Length + 1);

            if (value > 0 && value < _editingText.Text.Length && char.GetUnicodeCategory(_editingText.Text, value) == System.Globalization.UnicodeCategory.Surrogate)
                value += MathX.Sign(value - _editingText.CaretPosition);

            _editingText.CaretPosition = value;
        }
    }

    static int SelectionStart
    {
        get
        {
            if (_editingText == null)
                return -1;

            return MathX.Clamp(_editingText.SelectionStart, -1, _editingText.Text.Length + 1);
        }
        set
        {
            if (_editingText == null)
                return;

            _editingText.SelectionStart = MathX.Clamp(value, 0, _editingText.Text.Length + 1);
        }
    }

    static int SelectionLength => !HasSelection ? 0 : MathX.Abs(CaretPosition - SelectionStart);

    static bool HasCompositionRange => _editingText != null && _compositionStart >= 0 && _composition.Length > 0 && _compositionStart + _composition.Length <= _editingText.Text.Length;

    static void DeleteCompositionRange()
    {
        if (_editingText == null || !HasCompositionRange)
            return;

        _editingText.Text = _editingText.Text.Remove(_compositionStart, _composition.Length);
        HasSelection = false;
        CaretPosition = _compositionStart;
    }

    static void DeleteSelection()
    {
        if (_editingText == null || SelectionLength == 0)
            return;

        var start = MathX.Min(SelectionStart, CaretPosition);
        _editingText.Text = _editingText.Text.Remove(start, SelectionLength);
        HasSelection = false;
        CaretPosition = start;
    }

    static int ClampCompositionCaretOffset(int caretOffset, int compositionLength)
    {
        if (caretOffset < 0)
            return compositionLength;

        return Math.Max(0, Math.Min(caretOffset, compositionLength));
    }

}

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
        var suppressedEditingKeys = new HashSet<int>
        {
            (int)Key.UpArrow,
            (int)Key.DownArrow,
            (int)Key.RightArrow,
            (int)Key.LeftArrow,
            (int)Key.Backspace,
            (int)Key.Delete
        };
        for (var i = 0; i < codes.Count; i++)
        {
            if (codes[i].opcode != OpCodes.Ldloc_3 || i + 1 >= codes.Count || codes[i + 1].opcode != OpCodes.Brfalse_S)
                continue;

            codes.Insert(i + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TextEditorEditCoroutinePatch), nameof(IsStringChanged))));
            break;
        }

        for (var i = 1; i < codes.Count; i++)
        {
            if (!codes[i].Calls(getKeyRepeatMethod) || codes[i - 1].opcode != OpCodes.Ldc_I4)
                continue;

            if (!suppressedEditingKeys.Contains((int)codes[i - 1].operand))
                continue;

            codes[i] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TextEditorEditCoroutinePatch), nameof(GetKeyRepeat)));
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

[HarmonyPatch(typeof(TextEditor), "DeleteSelection")]
static class TextEditorDeleteSelectionPatch
{
    static bool Prefix()
    {
        if (!EngineIMEPatch.ShouldSuppressTextEditorDeletion)
            return true;

        return false;
    }
}

[HarmonyPatch(typeof(TextEditor), "Delete")]
static class TextEditorDeletePatch
{
    static bool Prefix()
    {
        if (!EngineIMEPatch.ShouldSuppressTextEditorDeletion)
            return true;

        return false;
    }
}

[HarmonyPatch(typeof(TextEditor), "Backspace")]
static class TextEditorBackspacePatch
{
    static bool Prefix()
    {
        if (!EngineIMEPatch.ShouldSuppressTextEditorDeletion)
            return true;

        return false;
    }
}

[HarmonyPatch(typeof(GlyphAtlasMeshGenerator), nameof(GlyphAtlasMeshGenerator.Generate))]
static class GlyphAtlasMeshGeneratorGeneratePatch
{
    public readonly struct CaretVisualState
    {
        public CaretVisualState(int caretPosition, colorX caretColor)
        {
            CaretPosition = caretPosition;
            CaretColor = caretColor;
        }

        public int CaretPosition { get; }

        public colorX CaretColor { get; }

        public bool IsActive => CaretPosition >= 0;
    }

    static void Prefix(ref TextEditingVisuals textEditVisuals, out CaretVisualState __state)
    {
        if (EngineIMEPatch.TryGetCompositionCaretVisual(textEditVisuals, out var caretPosition, out var caretColor))
        {
            __state = new CaretVisualState(caretPosition, caretColor);
            textEditVisuals.caretColor = colorX.Clear;
            return;
        }

        __state = default;
    }

    static void Postfix(StringRenderTree renderTree, float2 offset, MeshX meshx, AtlasSubmeshMapper submeshMapper, CaretVisualState __state)
    {
        if (!__state.IsActive || renderTree == null)
            return;

        var glyphIndex = MatchStringPositionToGlyph(renderTree, __state.CaretPosition, out var afterGlyph);

        if (glyphIndex < 0 && renderTree.GlyphLayoutLength > 0)
            return;

        var submesh = submeshMapper(default(AtlasData));
        StringLine line;
        float x;

        if (renderTree.GlyphLayoutLength > 0)
        {
            var glyph = renderTree.GetRenderGlyph(glyphIndex);
            line = renderTree.GetLine(glyph.line);
            x = afterGlyph ? glyph.rect.xmax : glyph.rect.xmin;
        }
        else
        {
            line = renderTree.GetLine(0);
            x = 0f;
        }

        var height = line.LineHeight * line.LineHeightMultiplier * 0.7f;
        var start = new float2(x + line.Position.x, 0f - line.Position.y + height * 0.5f - line.Descender);
        InsertLine(meshx, submesh, start, start + new float2(line.LineHeight * 0.04f), __state.CaretColor.ToProfile(meshx.Profile), height, offset);
    }

    static int MatchStringPositionToGlyph(StringRenderTree renderTree, int stringPosition, out bool afterGlyph)
    {
        afterGlyph = false;

        if (stringPosition < 0)
            return stringPosition;

        if (renderTree.GlyphLayoutLength == 0)
            return 0;

        for (var i = 0; i < renderTree.GlyphLayoutLength; i++)
        {
            var glyph = renderTree.GetRenderGlyph(i);

            if (glyph.stringIndex == stringPosition)
                return i;

            if (glyph.stringIndex > stringPosition)
                return Math.Max(0, i - 1);
        }

        afterGlyph = true;
        return renderTree.GlyphLayoutLength - 1;
    }

    static void InsertLine(MeshX mesh, TriangleSubmesh submesh, float2 startPoint, float2 endPoint, color color, float thickness, float2 offset)
    {
        mesh.IncreaseVertexCount(4);
        var start = mesh.VertexCount - 4;
        var halfThickness = thickness * 0.5f;

        mesh.RawPositions[start] = startPoint - new float2(0f, halfThickness) + offset;
        mesh.RawPositions[start + 1] = startPoint + new float2(0f, halfThickness) + offset;
        mesh.RawPositions[start + 2] = endPoint + new float2(0f, halfThickness) + offset;
        mesh.RawPositions[start + 3] = endPoint - new float2(0f, halfThickness) + offset;
        mesh.RawUV0s[start] = new float2(0f, 0f);
        mesh.RawUV0s[start + 1] = new float2(0f, 1f);
        mesh.RawUV0s[start + 2] = new float2(1f, 1f);
        mesh.RawUV0s[start + 3] = new float2(1f);

        for (var i = 0; i < 4; i++)
        {
            var vertex = start + i;
            mesh.RawColors[vertex] = color;
            mesh.RawNormals[vertex] = new float3(0f, 0f, 0f);
        }

        submesh.AddQuadAsTriangles(start, start + 1, start + 2, start + 3);
    }
}
