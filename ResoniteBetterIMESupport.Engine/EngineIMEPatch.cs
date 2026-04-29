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
        var textValue = text.Text ?? string.Empty;
        DebugLog($"SetEditingText: textLength={textValue.Length}, caret={text.CaretPosition}, selectionStart={text.SelectionStart}");
    }

    public static void ClearEditingText()
    {
        DebugLog($"ClearEditingText before: {DebugState}");

        _editingText = null;
        _stringChanged = false;
        _isTypingUnsettled = false;
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

        ApplyComposition(message.Composition);
    }

    static void ApplyComposition(string composition)
    {
        if (_editingText == null)
            return;

        if (HasSelection)
            DeleteSelection();

        if (composition.Length == 0)
        {
            HasSelection = false;
            _isTypingUnsettled = false;
            DebugLog($"ApplyComposition cleared composition: {DebugState}");
            return;
        }

        SelectionStart = CaretPosition;
        InsertText(composition);
        _isTypingUnsettled = true;
        _stringChanged = true;
        DebugLog($"ApplyComposition replaced composition: {DebugState}");
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

        return $"state={{textLength={_editingText.Text.Length}, caret={CaretPosition}, selectionStart={SelectionStart}, selectionLength={SelectionLength}, typingUnsettled={_isTypingUnsettled}, text=\"{EscapeForLog(_editingText.Text)}\"}}";
    }

    static string EscapeForLog(string value) =>
        value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

    static void DebugLog(string message) => EnginePlugin.LogDebugIme(message);
}
