using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using HarmonyLib;
using Renderite.Shared;
using ResoniteBetterIMESupport.Shared;

namespace ResoniteBetterIMESupport.Engine;

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

    public static bool ShouldSuppressTextEditorDeletion => _isTypingUnsettled && IsCompositionVisualSelectionActive;

    public static string DebugState => BuildDebugState();

    public static void Start()
    {
        Stop();
        DebugLog($"Starting IME pipe server: {ImePipe.PipeDebugInfo}");
        _server = new ImePipeServer(OnMessage);
    }

    public static void Stop()
    {
        _server?.Dispose();
        _server = null;
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
        {
            DebugLog($"OnMessage ignored: no editing text, message={DescribeMessage(message)}");
            return;
        }

        if (message.Composition == _composition && message.CommittedText.Length == 0 && message.CaretOffset == _compositionCaretOffset)
        {
            DebugLog($"OnMessage ignored: unchanged, message={DescribeMessage(message)}, {DebugState}");
            return;
        }

        DebugLog($"OnMessage enqueue: message={DescribeMessage(message)}, {DebugState}");

        text.RunSynchronously(() => ApplyMessage(text, message), true);
    }

    static void ApplyMessage(IText targetText, ImePipeMessage message)
    {
        if (!ReferenceEquals(_editingText, targetText))
        {
            DebugLog($"ApplyMessage ignored: editing target changed, message={DescribeMessage(message)}, {DebugState}");
            return;
        }

        DebugLog($"ApplyMessage begin: message={DescribeMessage(message)}, {DebugState}");

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
            DebugLog($"ApplyMessage committed/cleared: message={DescribeMessage(message)}, {DebugState}");
            return;
        }

        if (message.Composition == _composition && HasCompositionRange)
        {
            _compositionCaretOffset = ClampCompositionCaretOffset(message.CaretOffset, message.Composition.Length);
            SetCompositionVisuals();
            MarkTextChangeDirty();
            _isTypingUnsettled = true;
            DebugLog($"ApplyMessage caret-only update: message={DescribeMessage(message)}, {DebugState}");
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
        DebugLog($"ApplyMessage composition replaced: message={DescribeMessage(message)}, {DebugState}");
    }

    static void SetCompositionVisuals()
    {
        if (!HasCompositionRange)
            return;

        SelectionStart = _compositionStart;
        CaretPosition = _compositionStart + _composition.Length;
    }

    static bool IsCompositionVisualSelectionActive =>
        HasCompositionRange
        && SelectionStart == _compositionStart
        && CaretPosition == _compositionStart + _composition.Length;

    static void RestoreCompositionRangeIfTextEditorDeletedIt()
    {
        if (_editingText == null || _compositionStart < 0 || _composition.Length == 0 || HasCompositionRange)
            return;

        var insertPosition = Math.Max(0, Math.Min(_compositionStart, _editingText.Text.Length));
        _editingText.Text = _editingText.Text.Insert(insertPosition, _composition);
        CaretPosition = insertPosition + _composition.Length;
        SelectionStart = insertPosition;
        DebugLog($"Restored deleted composition range at {insertPosition}, {DebugState}");
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

        DebugLog($"DeleteCompositionRange before: {DebugState}");
        _editingText.Text = _editingText.Text.Remove(_compositionStart, _composition.Length);
        HasSelection = false;
        CaretPosition = _compositionStart;
        DebugLog($"DeleteCompositionRange after: {DebugState}");
    }

    static void DeleteSelection()
    {
        if (_editingText == null || SelectionLength == 0)
            return;

        DebugLog($"DeleteSelection before: {DebugState}");
        var start = MathX.Min(SelectionStart, CaretPosition);
        _editingText.Text = _editingText.Text.Remove(start, SelectionLength);
        HasSelection = false;
        CaretPosition = start;
        DebugLog($"DeleteSelection after: {DebugState}");
    }

    static int ClampCompositionCaretOffset(int caretOffset, int compositionLength)
    {
        if (caretOffset < 0)
            return compositionLength;

        return Math.Max(0, Math.Min(caretOffset, compositionLength));
    }

    static string BuildDebugState()
    {
        if (_editingText == null)
            return "state={editingText=null}";

        return $"state={{textLength={_editingText.Text.Length}, caret={CaretPosition}, selectionStart={SelectionStart}, selectionLength={SelectionLength}, compositionStart={_compositionStart}, compositionLength={_composition.Length}, compositionCaretOffset={_compositionCaretOffset}, typingUnsettled={_isTypingUnsettled}, hasCompositionRange={HasCompositionRange}, visualSelectionActive={IsCompositionVisualSelectionActive}, text=\"{EscapeForLog(_editingText.Text)}\", composition=\"{EscapeForLog(_composition)}\"}}";
    }

    static string DescribeMessage(ImePipeMessage message) =>
        $"{{composition=\"{EscapeForLog(message.Composition)}\", committed=\"{EscapeForLog(message.CommittedText)}\", caretOffset={message.CaretOffset}}}";

    static string EscapeForLog(string value) =>
        value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

    static void DebugLog(string message) => EnginePlugin.Log.LogInfo($"[IME debug] {message}");
}
