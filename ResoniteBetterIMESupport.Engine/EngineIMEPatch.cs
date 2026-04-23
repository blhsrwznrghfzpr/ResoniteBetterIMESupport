using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using HarmonyLib;
using InterprocessLib;
using Renderite.Shared;
using ResoniteBetterIMESupport.Shared;
using System.Collections.Generic;

namespace ResoniteBetterIMESupport.Engine;

static class EngineIMEPatch
{
    static IText? _editingText;
    static Messenger? _messenger;
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

    public static bool ShouldSuppressScreenModeToggle => HasActiveComposition;

    public static bool ShouldSuppressTextEditorKey(Key key) =>
        HasActiveComposition && (key == Key.Backspace || key == Key.Delete);

    public static bool ShouldSuppressTextEditorDeletion => HasActiveComposition && IsCompositionVisualSelectionActive;

    public static string DebugState => BuildDebugState();

    public static void Start()
    {
        Stop();
        DebugLog($"Starting IME InterprocessLib receiver: ownerId=\"{ImeInterprocessChannel.OwnerId}\", messageId=\"{ImeInterprocessChannel.MessageId}\"");
        _messenger = new Messenger(ImeInterprocessChannel.OwnerId);
        _messenger.ReceiveObject<ImeInterprocessMessage>(ImeInterprocessChannel.MessageId, OnMessage);
    }

    public static void Stop()
    {
        _messenger?.Dispose();
        _messenger = null;
    }

    public static void SetEditingText(IText? text)
    {
        if (text == null)
        {
            DebugLog("SetEditingText ignored null target.");
            ClearEditingText();
            return;
        }

        var textValue = text.Text ?? string.Empty;
        DebugLog($"SetEditingText: textLength={textValue.Length}, caret={text.CaretPosition}, selectionStart={text.SelectionStart}");
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
        DebugLog($"ClearEditingText before: {DebugState}");
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

    static void OnMessage(ImeInterprocessMessage message)
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

    static void ApplyMessage(IText targetText, ImeInterprocessMessage message)
    {
        if (!ReferenceEquals(_editingText, targetText))
        {
            DebugLog($"ApplyMessage ignored: editing target changed, message={DescribeMessage(message)}, {DebugState}");
            return;
        }

        DebugLog($"ApplyMessage begin: message={DescribeMessage(message)}, {DebugState}");

        MarkStandardTypeDeltaAsImeHandled();

        if (message.Composition.Length == 0
            && HasCompositionRange
            && IsLikelyFocusLossAccumulatedCommit(message.CommittedText, _composition))
        {
            AddPendingSuppressedTypeDelta(message.CommittedText);
            MarkKeyboardTypeDeltaForSuppression();
            KeepCompositionTextAndClearState();
            DebugLog($"ApplyMessage canceled accumulated focus-loss commit: message={DescribeMessage(message)}, {DebugState}");
            return;
        }

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
            else if (HasCompositionRange && message.CommittedText.Length == 0 && message.CaretOffset < 0)
            {
                KeepCompositionTextAndClearState();
                DebugLog($"ApplyMessage kept composition text on focus-loss cancel: message={DescribeMessage(message)}, {DebugState}");
                return;
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

    static void KeepCompositionTextAndClearState()
    {
        if (HasCompositionRange)
            CaretPosition = _compositionStart + _composition.Length;

        HasSelection = false;
        _isTypingUnsettled = false;
        _composition = string.Empty;
        _compositionStart = -1;
        _compositionCaretOffset = -1;
        _stringChanged = true;
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

    static bool IsLikelyImplicitCommitBeforeNewComposition(ImeInterprocessMessage message)
    {
        if (!HasCompositionRange || message.CommittedText.Length > 0 || message.Composition.Length == 0)
            return false;

        if (message.EditAction == ImeEditAction.Backspace || message.EditAction == ImeEditAction.Delete)
            return false;

        if (_composition.Length <= 1)
            return false;

        if (message.CaretOffset > Math.Min(message.Composition.Length, 1))
            return false;

        if (message.Composition.Length > 2)
            return false;

        if (_composition.StartsWith(message.Composition, StringComparison.Ordinal) || message.Composition.StartsWith(_composition, StringComparison.Ordinal))
            return false;

        return true;
    }

    static bool IsLikelyFocusLossAccumulatedCommit(string committedText, string previousComposition)
    {
        if (committedText.Length == 0 || previousComposition.Length == 0)
            return false;

        if (committedText.Length <= Math.Max(previousComposition.Length * 2, previousComposition.Length + 8))
            return false;

        var repeats = CountOccurrences(committedText, previousComposition);
        if (repeats >= 2)
            return true;

        var prefixLength = Math.Min(previousComposition.Length, committedText.Length);
        return string.CompareOrdinal(committedText, 0, previousComposition, 0, prefixLength) == 0
            && committedText.Length > previousComposition.Length * 3;
    }

    static int CountOccurrences(string value, string pattern)
    {
        var count = 0;
        var index = 0;

        while (index < value.Length)
        {
            index = value.IndexOf(pattern, index, StringComparison.Ordinal);
            if (index < 0)
                return count;

            count++;
            index += pattern.Length;
        }

        return count;
    }

    static string BuildDebugState()
    {
        if (_editingText == null)
            return "state={editingText=null}";

        return $"state={{textLength={_editingText.Text.Length}, caret={CaretPosition}, selectionStart={SelectionStart}, selectionLength={SelectionLength}, compositionStart={_compositionStart}, compositionLength={_composition.Length}, compositionCaretOffset={_compositionCaretOffset}, typingUnsettled={_isTypingUnsettled}, hasCompositionRange={HasCompositionRange}, visualSelectionActive={IsCompositionVisualSelectionActive}, text=\"{EscapeForLog(_editingText.Text)}\", composition=\"{EscapeForLog(_composition)}\"}}";
    }

    static string DescribeMessage(ImeInterprocessMessage message) =>
        $"{{composition=\"{EscapeForLog(message.Composition)}\", committed=\"{EscapeForLog(message.CommittedText)}\", caretOffset={message.CaretOffset}, editAction={message.EditAction}}}";

    static string EscapeForLog(string value) =>
        value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

    static void DebugLog(string message) => EnginePlugin.LogDebugIme(message);
}
