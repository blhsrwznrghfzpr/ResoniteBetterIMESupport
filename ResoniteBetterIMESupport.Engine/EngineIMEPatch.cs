using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using HarmonyLib;
using Renderite.Shared;
using ResoniteBetterIMESupport.Shared;
using System.Collections.Generic;

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
    static int _suppressKeyboardTypeDeltaUpdates;
    static int _suppressStandardTypeDeltaWhileImeActiveUpdates;
    static readonly Queue<string> PendingSuppressedTypeDeltas = new();

    public static bool HasActiveComposition => _isTypingUnsettled && HasCompositionRange;

    public static bool IsTypingUnsettled => HasActiveComposition;

    public static bool ShouldSuppressTextEditorKey(Key key) =>
        HasActiveComposition && (key == Key.Backspace || key == Key.Delete);

    public static bool ShouldSuppressTextEditorDeletion => HasActiveComposition && IsCompositionVisualSelectionActive;

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
        _suppressKeyboardTypeDeltaUpdates = 0;
        _suppressStandardTypeDeltaWhileImeActiveUpdates = 0;
        PendingSuppressedTypeDeltas.Clear();
    }

    public static void ClearEditingText()
    {
        _editingText = null;
        _stringChanged = false;
        _isTypingUnsettled = false;
        _composition = string.Empty;
        _compositionStart = -1;
        _compositionCaretOffset = -1;
        _suppressKeyboardTypeDeltaUpdates = 0;
        _suppressStandardTypeDeltaWhileImeActiveUpdates = 0;
        PendingSuppressedTypeDeltas.Clear();
    }

    public static bool ConsumeStringChanged()
    {
        if (!_stringChanged)
            return false;

        _stringChanged = false;
        return true;
    }

    public static bool TryFilterKeyboardTypeDelta(string typeDelta, out string filteredTypeDelta)
    {
        filteredTypeDelta = typeDelta;

        if (typeDelta.Length == 0)
            return false;

        if (IsTextEditorControlTypeDelta(typeDelta))
            return false;

        if (TryConsumePendingSuppressedTypeDelta(typeDelta, out var pendingSuppressedTypeDelta))
        {
            filteredTypeDelta = typeDelta.Remove(pendingSuppressedTypeDelta.Start, pendingSuppressedTypeDelta.Length);
            if (filteredTypeDelta.Length > 0 && !IsTextEditorControlTypeDelta(filteredTypeDelta) && ShouldSuppressStandardTypeDeltaWhileImeActive(filteredTypeDelta))
                filteredTypeDelta = string.Empty;

            DebugLog($"Suppressing pending IME TypeDelta: typeDelta=\"{EscapeForLog(typeDelta)}\", filtered=\"{EscapeForLog(filteredTypeDelta)}\", {DebugState}");
            return true;
        }

        if (ShouldSuppressStandardTypeDeltaWhileImeActive(typeDelta))
        {
            filteredTypeDelta = string.Empty;
            DebugLog($"Suppressing standard TypeDelta while IME is handled by mod path: typeDelta=\"{EscapeForLog(typeDelta)}\", {DebugState}");
            return true;
        }

        if (_suppressKeyboardTypeDeltaUpdates <= 0)
            return false;

        _suppressKeyboardTypeDeltaUpdates--;
        DebugLog($"Suppressing keyboard TypeDelta after IME pipe handling: typeDelta=\"{EscapeForLog(typeDelta)}\", {DebugState}");
        filteredTypeDelta = string.Empty;
        return true;
    }

    static bool IsTextEditorControlTypeDelta(string typeDelta) =>
        typeDelta.Length == 1 && (typeDelta[0] == '\b' || typeDelta[0] == '\n' || typeDelta[0] == '\r');

    static bool ShouldSuppressStandardTypeDeltaWhileImeActive(string typeDelta)
    {
        if (HasCompositionRange && typeDelta == _composition)
            return true;

        if (HasActiveComposition)
            return true;

        if (_suppressStandardTypeDeltaWhileImeActiveUpdates <= 0)
            return false;

        _suppressStandardTypeDeltaWhileImeActiveUpdates--;
        return true;
    }

    public static bool ApplyKeyboardStateComposition(bool active, string composition, int selectionStart, int selectionLength, string committedText)
    {
        var text = _editingText;
        var consumedCommittedText = false;

        if (text == null)
            return false;

        var committedCurrentComposition = HasCompositionRange && committedText == _composition;

        if (committedText.Length > 0)
        {
            CommitText(committedText);
            consumedCommittedText = true;
        }

        if (active && committedCurrentComposition && composition == committedText)
        {
            DebugLog($"ApplyKeyboardStateComposition ignored stale active composition after commit: committed=\"{EscapeForLog(committedText)}\", composition=\"{EscapeForLog(composition)}\", {DebugState}");
            return true;
        }

        if (!active)
        {
            if (!HasCompositionRange && _composition.Length == 0)
                return consumedCommittedText;

            if (HasCompositionRange)
            {
                DeleteCompositionRange();
                _stringChanged = true;
            }

            HasSelection = false;
            _isTypingUnsettled = false;
            _composition = string.Empty;
            _compositionStart = -1;
            _compositionCaretOffset = -1;
            DebugLog($"ApplyKeyboardStateComposition inactive: committed=\"{EscapeForLog(committedText)}\", {DebugState}");
            return consumedCommittedText;
        }

        var caretOffset = ClampCompositionCaretOffset(selectionStart + selectionLength, composition.Length);
        var message = new ImePipeMessage(composition, string.Empty, caretOffset);
        ApplyMessage(text, message);
        return consumedCommittedText;
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

        MarkStandardTypeDeltaAsImeHandled();

        if (message.CommittedText.Length > 0)
            AddPendingSuppressedTypeDelta(message.CommittedText);
        else
            MarkKeyboardTypeDeltaForSuppression();

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

        var committedCurrentComposition = HasCompositionRange && message.CommittedText == _composition;

        if (message.CommittedText.Length > 0)
        {
            CommitText(message.CommittedText, markKeyboardTypeDeltaForSuppression: false);
            DebugLog($"ApplyMessage committed text before new composition: committed=\"{EscapeForLog(message.CommittedText)}\", message={DescribeMessage(message)}, {DebugState}");

            if (committedCurrentComposition && message.Composition == message.CommittedText)
            {
                DebugLog($"ApplyMessage ignored stale composition after committed text: message={DescribeMessage(message)}, {DebugState}");
                return;
            }
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

        if (IsLikelyImplicitCommitBeforeNewComposition(message))
        {
            var committedComposition = _composition;
            CommitText(committedComposition, markKeyboardTypeDeltaForSuppression: false);
            AddPendingSuppressedTypeDelta(committedComposition);
            DebugLog($"ApplyMessage implicit composition commit before new composition: committed=\"{EscapeForLog(committedComposition)}\", message={DescribeMessage(message)}, {DebugState}");
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

    static void CommitText(string committedText, bool markKeyboardTypeDeltaForSuppression = true)
    {
        if (committedText.Length == 0)
            return;

        if (markKeyboardTypeDeltaForSuppression)
            MarkKeyboardTypeDeltaForSuppression();

        if (HasCompositionRange && committedText == _composition)
        {
            CaretPosition = _compositionStart + _composition.Length;
        }
        else
        {
            if (HasCompositionRange)
                DeleteCompositionRange();
            else if (HasSelection)
                DeleteSelection();

            InsertText(committedText);
            _stringChanged = true;
        }

        HasSelection = false;
        _isTypingUnsettled = false;
        _composition = string.Empty;
        _compositionStart = -1;
        _compositionCaretOffset = -1;
        DebugLog($"CommitText: committed=\"{EscapeForLog(committedText)}\", {DebugState}");
    }

    static void MarkKeyboardTypeDeltaForSuppression() => _suppressKeyboardTypeDeltaUpdates = Math.Max(_suppressKeyboardTypeDeltaUpdates, 8);

    static void MarkStandardTypeDeltaAsImeHandled() => _suppressStandardTypeDeltaWhileImeActiveUpdates = Math.Max(_suppressStandardTypeDeltaWhileImeActiveUpdates, 8);

    static void AddPendingSuppressedTypeDelta(string value)
    {
        if (value.Length == 0)
            return;

        PendingSuppressedTypeDeltas.Enqueue(value);

        while (PendingSuppressedTypeDeltas.Count > 8)
            PendingSuppressedTypeDeltas.Dequeue();
    }

    static bool TryConsumePendingSuppressedTypeDelta(string typeDelta, out PendingSuppressedTypeDelta suppressedTypeDelta)
    {
        suppressedTypeDelta = default;
        var count = PendingSuppressedTypeDeltas.Count;

        for (var i = 0; i < count; i++)
        {
            var pending = PendingSuppressedTypeDeltas.Dequeue();

            var index = typeDelta.IndexOf(pending, StringComparison.Ordinal);
            if (index >= 0)
            {
                suppressedTypeDelta = new PendingSuppressedTypeDelta(index, pending.Length);
                return true;
            }

            PendingSuppressedTypeDeltas.Enqueue(pending);
        }

        return false;
    }

    readonly struct PendingSuppressedTypeDelta
    {
        public readonly int Start;
        public readonly int Length;

        public PendingSuppressedTypeDelta(int start, int length)
        {
            Start = start;
            Length = length;
        }
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

    static bool HasCompositionRange
    {
        get
        {
            if (_editingText == null || _compositionStart < 0 || _composition.Length == 0 || _compositionStart + _composition.Length > _editingText.Text.Length)
                return false;

            return string.CompareOrdinal(_editingText.Text, _compositionStart, _composition, 0, _composition.Length) == 0;
        }
    }

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

    static bool IsLikelyImplicitCommitBeforeNewComposition(ImePipeMessage message)
    {
        if (!HasCompositionRange || message.CommittedText.Length > 0 || message.Composition.Length == 0 || _composition.Length <= 1)
            return false;

        if (message.CaretOffset > Math.Min(message.Composition.Length, 1))
            return false;

        if (message.Composition.Length > 2)
            return false;

        if (_composition.StartsWith(message.Composition, StringComparison.Ordinal) || message.Composition.StartsWith(_composition, StringComparison.Ordinal))
            return false;

        return IsLikelyCompositionStarter(message.Composition);
    }

    static bool IsLikelyCompositionStarter(string value)
    {
        foreach (var ch in value)
        {
            if (IsCjkUnifiedIdeograph(ch))
                continue;

            if (char.IsLetterOrDigit(ch) || IsFullwidthAscii(ch) || IsJapaneseKana(ch) || IsHangul(ch) || IsBopomofo(ch))
                return true;
        }

        return false;
    }

    static bool IsFullwidthAscii(char ch) => ch >= '\uFF01' && ch <= '\uFF5E';

    static bool IsJapaneseKana(char ch) =>
        (ch >= '\u3040' && ch <= '\u30FF')
        || (ch >= '\u31F0' && ch <= '\u31FF')
        || (ch >= '\uFF66' && ch <= '\uFF9D');

    static bool IsHangul(char ch) =>
        (ch >= '\u1100' && ch <= '\u11FF')
        || (ch >= '\u3130' && ch <= '\u318F')
        || (ch >= '\uA960' && ch <= '\uA97F')
        || (ch >= '\uAC00' && ch <= '\uD7AF')
        || (ch >= '\uD7B0' && ch <= '\uD7FF');

    static bool IsBopomofo(char ch) =>
        (ch >= '\u3100' && ch <= '\u312F')
        || (ch >= '\u31A0' && ch <= '\u31BF');

    static bool IsCjkUnifiedIdeograph(char ch) =>
        (ch >= '\u3400' && ch <= '\u4DBF')
        || (ch >= '\u4E00' && ch <= '\u9FFF')
        || (ch >= '\uF900' && ch <= '\uFAFF');

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

    static void DebugLog(string message) => EnginePlugin.LogDebugIme(message);
}
