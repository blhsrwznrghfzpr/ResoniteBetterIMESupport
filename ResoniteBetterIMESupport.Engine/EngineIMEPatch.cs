using Elements.Core;
using FrooxEngine;
using InterprocessLib;
using Renderite.Shared;
using ResoniteBetterIMESupport.Shared;

namespace ResoniteBetterIMESupport.Engine;

static class EngineIMEPatch
{
    static IText? _editingText;
    static Messenger? _messenger;
    static bool _stringChanged;
    static bool _isTypingUnsettled;
    static int _compositionStart = -1;
    static int _compositionLength;
    static int _compositionVisualCaret = -1;
    public static bool IsTypingUnsettled => _isTypingUnsettled;

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
        _isTypingUnsettled = false;
        ClearCompositionRange();
        var textValue = text.Text ?? string.Empty;
        DebugLog($"SetEditingText: textLength={textValue.Length}, caret={text.CaretPosition}, selectionStart={text.SelectionStart}");
    }

    public static void ClearEditingText()
    {
        DebugLog($"ClearEditingText before: {DebugState}");

        _editingText = null;
        _stringChanged = false;
        _isTypingUnsettled = false;
        ClearCompositionRange();
    }

    public static bool ConsumeStringChanged()
    {
        if (!_stringChanged)
            return false;

        _stringChanged = false;
        return true;
    }

    public static bool ShouldSuppressTextEditorKey(Key key) =>
        _isTypingUnsettled
        && (key == Key.UpArrow
            || key == Key.DownArrow
            || key == Key.RightArrow
            || key == Key.LeftArrow
            || key == Key.Delete
            || key == Key.Backspace);

    public static void LogSuppressedTextEditorKey(Key key, string source)
    {
        DebugLog($"Suppressed TextEditor key from {source}: key={key}, {DebugState}");
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

        ApplyComposition(message.Composition, message.CompositionCursor, message.HasCommittedResult);
    }

    static void ApplyComposition(string composition, int compositionCursor, bool hasCommittedResult)
    {
        if (_editingText == null)
            return;

        if (hasCommittedResult && HasCompositionRange)
        {
            DeleteCompositionRange();
            _isTypingUnsettled = false;
            if (composition.Length == 0)
            {
                HasSelection = false;
                DebugLog($"ApplyComposition cleared composition after committed result: {DebugState}");
                return;
            }

            DebugLog($"ApplyComposition deferred next composition after committed result: nextLength={composition.Length}, {DebugState}");
            return;
        }

        if (HasCompositionRange)
            DeleteCompositionRange();
        else if (HasSelection)
            DeleteSelection();

        if (composition.Length == 0)
        {
            ClearCompositionRange();
            HasSelection = false;
            _isTypingUnsettled = false;
            DebugLog($"ApplyComposition cleared composition: {DebugState}");
            return;
        }

        var compositionStart = CaretPosition;
        InsertText(composition);
        _compositionStart = compositionStart;
        _compositionLength = composition.Length;
        _compositionVisualCaret = ToTextCaretPosition(compositionStart, composition.Length, compositionCursor);
        HasSelection = false;
        _isTypingUnsettled = true;
        _stringChanged = true;
        DebugLog($"ApplyComposition replaced composition: visualCaret={_compositionVisualCaret}, {DebugState}");
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

    static void DeleteSelection()
    {
        if (_editingText == null || SelectionLength == 0)
            return;

        var start = MathX.Min(SelectionStart, CaretPosition);
        _editingText.Text = _editingText.Text.Remove(start, SelectionLength);
        HasSelection = false;
        CaretPosition = start;
    }

    static void DeleteCompositionRange()
    {
        if (_editingText == null || !HasCompositionRange)
            return;

        var start = MathX.Clamp(_compositionStart, 0, _editingText.Text.Length);
        var length = MathX.Min(_compositionLength, _editingText.Text.Length - start);
        if (length > 0)
            _editingText.Text = _editingText.Text.Remove(start, length);

        ClearCompositionRange();
        HasSelection = false;
        CaretPosition = start;
    }

    static int NormalizeCompositionCursor(int compositionCursor, int compositionLength) =>
        compositionCursor < 0 ? compositionLength : ClampInclusive(compositionCursor, 0, compositionLength);

    static int ToTextCaretPosition(int compositionStart, int compositionLength, int compositionCursor)
    {
        var normalizedCursor = NormalizeCompositionCursor(compositionCursor, compositionLength);
        return compositionStart + normalizedCursor;
    }

    static int ClampInclusive(int value, int min, int max)
    {
        if (value < min)
            return min;

        return value > max ? max : value;
    }

    static bool HasCompositionRange => _compositionStart >= 0 && _compositionLength > 0;

    static void ClearCompositionRange()
    {
        _compositionStart = -1;
        _compositionLength = 0;
        _compositionVisualCaret = -1;
    }

    public static bool TryGetCompositionVisualRange(
        string text,
        out int compositionStart,
        out int compositionLength)
    {
        compositionStart = -1;
        compositionLength = 0;

        if (_editingText == null
            || !HasCompositionRange
            || !string.Equals(_editingText.Text, text, StringComparison.Ordinal))
        {
            return false;
        }

        compositionStart = _compositionStart;
        compositionLength = _compositionLength;
        return true;
    }

    public static bool TryGetCompositionVisualCaret(string text, out int visualCaret)
    {
        visualCaret = -1;
        if (_editingText == null
            || !HasCompositionRange
            || !string.Equals(_editingText.Text, text, StringComparison.Ordinal))
        {
            return false;
        }

        visualCaret = ClampInclusive(_compositionVisualCaret, _compositionStart, _compositionStart + _compositionLength);
        return true;
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

            return MathX.Clamp(_editingText.SelectionStart, -1, _editingText.Text.Length);
        }
        set
        {
            if (_editingText == null)
                return;

            _editingText.SelectionStart = MathX.Clamp(value, 0, _editingText.Text.Length + 1);
        }
    }

    static int SelectionLength => !HasSelection ? 0 : MathX.Abs(CaretPosition - SelectionStart);

    static string BuildDebugState()
    {
        if (_editingText == null)
            return "state={editingText=null}";

        return $"state={{textLength={_editingText.Text.Length}, caret={CaretPosition}, selectionStart={SelectionStart}, selectionLength={SelectionLength}, compositionStart={_compositionStart}, compositionLength={_compositionLength}, compositionVisualCaret={_compositionVisualCaret}, typingUnsettled={_isTypingUnsettled}, text=\"{EscapeForLog(_editingText.Text)}\"}}";
    }

    static string EscapeForLog(string value) =>
        value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

    static void DebugLog(string message) => EnginePlugin.LogDebugIme(message);
}
