using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using HarmonyLib;
using InterprocessLib;
using Renderite.Shared;
using ResoniteBetterIMESupport.Shared;
using System.Reflection;

namespace ResoniteBetterIMESupport.Engine;

static class EngineIMEPatch
{
    static IText? _editingText;
    static Messenger? _messenger;
    static readonly MethodInfo? _setTypeDeltaMethod = AccessTools.PropertySetter(typeof(InputInterface), nameof(InputInterface.TypeDelta));
    static bool _stringChanged;
    static bool _isComposing;
    static string _compositionText = string.Empty;
    static int _compositionStart = -1;
    static int _compositionCaretOffset = -1;
    public static bool IsTypingUnsettled => HasActiveComposition;

    static bool HasActiveComposition => _isComposing && HasCompositionRange;

    public static string DebugState => BuildDebugState();

    public static void Start()
    {
        Stop();
        var queueName = ImeInterprocessQueue.GetQueueName();
        DebugLog($"Starting IME receiver: ownerId=\"{ImeInterprocessChannel.OwnerId}\", messageId=\"{ImeInterprocessChannel.MessageId}\", queueName=\"{queueName}\"");
        _messenger = new Messenger(ImeInterprocessChannel.OwnerId, true, queueName);
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
            ClearEditingText();
            return;
        }

        _editingText = text;
        _stringChanged = false;
        _isComposing = false;
        _compositionText = string.Empty;
        _compositionStart = -1;
        _compositionCaretOffset = -1;
        var textValue = text.Text ?? string.Empty;
        DebugLog($"SetEditingText: textLength={textValue.Length}, caret={text.CaretPosition}, selectionStart={text.SelectionStart}");
    }

    public static void ClearEditingText()
    {
        DebugLog($"ClearEditingText before: {DebugState}");
        _editingText = null;
        _stringChanged = false;
        _isComposing = false;
        _compositionText = string.Empty;
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

    public static bool ShouldSuppressTextEditorKey(Key key) =>
        HasActiveComposition;

    public static void LogSuppressedTextEditorKey(Key key)
    {
        DebugLog($"Suppressed standard key repeat while IME handled input is active: key={key}, {DebugState}");
    }

    public static bool TryGetCompositionCaretVisual(TextEditingVisuals visuals, out int caretPosition, out colorX caretColor)
    {
        caretPosition = -1;
        caretColor = default;

        if (!HasCompositionRange)
            return false;

        var compositionEnd = _compositionStart + _compositionText.Length;
        if (visuals.selectionStart != _compositionStart || visuals.caretPosition != compositionEnd)
            return false;

        caretPosition = _compositionStart + ClampCompositionCaretOffset(_compositionCaretOffset, _compositionText.Length);
        caretColor = visuals.caretColor;
        return true;
    }

    static void OnMessage(ImeInterprocessMessage message)
    {
        var text = _editingText;
        if (text == null)
        {
            DebugLog($"OnMessage ignored with no editing target: {message}");
            return;
        }

        text.RunSynchronously(() => ApplyMessage(text, message), true);
    }

    static void ApplyMessage(IText targetText, ImeInterprocessMessage message)
    {
        if (!ReferenceEquals(targetText, _editingText))
            return;

        DebugLog($"ApplyMessage begin: {message}, {DebugState}");

        switch (message.Kind)
        {
            case ImeMessageKind.UpdateComposition:
                ApplyComposition(message.Composition, message.CommittedText, message.CaretOffset);
                break;
            case ImeMessageKind.CommitComposition:
                CommitComposition(message.CommittedText);
                break;
            case ImeMessageKind.CancelComposition:
                CancelComposition();
                break;
        }
    }

    static void ApplyComposition(string composition, string committedText, int caretOffset)
    {
        if (_editingText == null)
            return;

        if (committedText.Length > 0)
            CommitComposition(committedText);

        if (composition.Length == 0)
        {
            return;
        }

        if (HasCompositionRange && composition == _compositionText)
        {
            _compositionCaretOffset = ClampCompositionCaretOffset(caretOffset, composition.Length);
            SetCompositionVisuals();
            MarkTextChangeDirty();
            _isComposing = true;
            DebugLog($"ApplyComposition caret-only update: {DebugState}");
            return;
        }

        if (HasCompositionRange)
            DeleteCompositionRange();
        else if (HasSelection)
            DeleteSelection();

        _compositionStart = CaretPosition;
        InsertText(composition);
        _compositionText = composition;
        _compositionCaretOffset = ClampCompositionCaretOffset(caretOffset, composition.Length);
        SetCompositionVisuals();
        MarkTextChangeDirty();
        _stringChanged = true;
        _isComposing = true;
        DebugLog($"ApplyComposition replaced composition: {DebugState}");
    }

    static void CommitComposition(string committedText = "")
    {
        if (_editingText == null)
            return;

        if (committedText.Length > 0 && (!HasCompositionRange || !string.Equals(committedText, _compositionText, StringComparison.Ordinal)))
        {
            if (HasCompositionRange)
                DeleteCompositionRange();
            else if (HasSelection)
                DeleteSelection();

            InsertText(committedText);
            HasSelection = false;
            MarkTextChangeDirty();
            _stringChanged = true;
        }
        else if (HasCompositionRange)
        {
            CaretPosition = _compositionStart + _compositionText.Length;
            HasSelection = false;
            MarkTextChangeDirty();
        }

        if (committedText.Length > 0)
            SuppressCommittedTypeDeltaPrefix(committedText);

        _isComposing = false;
        _compositionText = string.Empty;
        _compositionStart = -1;
        _compositionCaretOffset = -1;
        DebugLog($"CommitComposition: {DebugState}");
    }

    static void CancelComposition()
    {
        if (_editingText == null)
            return;

        if (HasCompositionRange)
        {
            DeleteCompositionRange();
            _stringChanged = true;
        }

        HasSelection = false;
        MarkTextChangeDirty();
        _isComposing = false;
        _compositionText = string.Empty;
        _compositionStart = -1;
        _compositionCaretOffset = -1;
        DebugLog($"CancelComposition: {DebugState}");
    }

    static void SetCompositionVisuals()
    {
        if (!HasCompositionRange)
            return;

        SelectionStart = _compositionStart;
        CaretPosition = _compositionStart + _compositionText.Length;
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

        _editingText.Text = _editingText.Text.Substring(0, CaretPosition)
            + value
            + _editingText.Text.Substring(CaretPosition, _editingText.Text.Length - CaretPosition);
        CaretPosition += value.Length;
    }

    static void DeleteCompositionRange()
    {
        if (_editingText == null || !HasCompositionRange)
            return;

        _editingText.Text = _editingText.Text.Remove(_compositionStart, _compositionText.Length);
        CaretPosition = _compositionStart;
        HasSelection = false;
    }

    static void DeleteSelection()
    {
        if (_editingText == null || SelectionLength == 0)
            return;

        var start = MathX.Min(SelectionStart, CaretPosition);
        _editingText.Text = _editingText.Text.Remove(start, SelectionLength);
        CaretPosition = start;
        HasSelection = false;
    }

    static void SuppressCommittedTypeDeltaPrefix(string committedText)
    {
        if (_editingText == null || committedText.Length == 0)
            return;

        var inputInterface = _editingText.World?.InputInterface;
        if (inputInterface == null)
            return;

        var typeDelta = inputInterface.TypeDelta ?? string.Empty;
        if (!typeDelta.StartsWith(committedText, StringComparison.Ordinal))
            return;

        var trimmedTypeDelta = typeDelta.Substring(committedText.Length);
        _setTypeDeltaMethod?.Invoke(inputInterface, new object?[] { trimmedTypeDelta });
        DebugLog($"Suppressed engine TypeDelta prefix after IME commit: removedLength={committedText.Length}, remainingLength={trimmedTypeDelta.Length}");
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
            if (value > 0
                && value < _editingText.Text.Length
                && char.GetUnicodeCategory(_editingText.Text, value) == System.Globalization.UnicodeCategory.Surrogate)
            {
                value += MathX.Sign(value - _editingText.CaretPosition);
            }

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
            if (_editingText == null
                || _compositionStart < 0
                || _compositionText.Length == 0
                || _compositionStart + _compositionText.Length > _editingText.Text.Length)
            {
                return false;
            }

            return string.CompareOrdinal(_editingText.Text, _compositionStart, _compositionText, 0, _compositionText.Length) == 0;
        }
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

        return $"state={{textLength={_editingText.Text.Length}, caret={CaretPosition}, selectionStart={SelectionStart}, selectionLength={SelectionLength}, compositionStart={_compositionStart}, compositionLength={_compositionText.Length}, compositionCaretOffset={_compositionCaretOffset}, composing={_isComposing}, hasCompositionRange={HasCompositionRange}, text=\"{EscapeForLog(_editingText.Text)}\", composition=\"{EscapeForLog(_compositionText)}\"}}";
    }

    static string EscapeForLog(string value) =>
        value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

    static void DebugLog(string message) => EnginePlugin.LogDebugIme(message);
}
