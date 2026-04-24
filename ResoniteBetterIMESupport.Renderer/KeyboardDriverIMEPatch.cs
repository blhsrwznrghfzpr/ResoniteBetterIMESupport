using HarmonyLib;
using InterprocessLib;
using Renderite.Shared;
using ResoniteBetterIMESupport.Shared;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using IMECompositionString = UnityEngine.InputSystem.LowLevel.IMECompositionString;
using RenderiteKey = Renderite.Shared.Key;
using RenderiteKeyboardState = Renderite.Shared.KeyboardState;

namespace ResoniteBetterIMESupport.Renderer;

static class KeyboardDriverIMEPatch
{
    static readonly ConditionalWeakTable<object, DriverState> States = new();
    static readonly HashSet<object> ActiveDrivers = new();

    static FieldInfo? _typeDeltaField;
    static bool _inputUpdateHooked;
    static bool _messengerIdentityLogged;
    static Messenger? _messenger;

    public static Type KeyboardDriverType =>
        AccessTools.TypeByName("KeyboardDriver") ?? throw new InvalidOperationException("KeyboardDriver type was not found.");

    public static FieldInfo TypeDeltaField =>
        _typeDeltaField ??= AccessTools.Field(KeyboardDriverType, "typeDelta") ?? throw new InvalidOperationException("KeyboardDriver.typeDelta field was not found.");

    public static DriverState GetState(object driver) => States.GetOrCreateValue(driver);

    public static StringBuilder? GetTypeDelta(object driver) => (StringBuilder?)TypeDeltaField.GetValue(driver);

    public static void InitializeMessaging()
    {
        if (_messenger != null)
            return;

        var queueName = ImeInterprocessQueue.GetQueueName();
        _messenger = new Messenger(ImeInterprocessChannel.OwnerId, false, queueName);
        LogMessengerIdentityOnce();
    }

    public static void DisposeMessaging()
    {
        _messenger?.Dispose();
        _messenger = null;
    }

    public static void Subscribe(object driver)
    {
        var keyboard = Keyboard.current;
        if (keyboard == null)
        {
            RendererPlugin.Logger.LogWarning("Keyboard.current is null. IME composition support was not attached.");
            return;
        }

        var state = GetState(driver);
        if (state.CompositionHandler != null)
            return;

        InitializeMessaging();
        state.CompositionHandler = composition => OnIMECompositionChange(driver, composition);
        keyboard.onIMECompositionChange += state.CompositionHandler;
        ActiveDrivers.Add(driver);
        HookInputUpdate();
    }

    public static void Unsubscribe(object driver)
    {
        var state = GetState(driver);
        if (state.CompositionHandler == null)
            return;

        var keyboard = Keyboard.current;
        if (keyboard != null)
            keyboard.onIMECompositionChange -= state.CompositionHandler;

        ActiveDrivers.Remove(driver);
        state.CompositionHandler = null;
        ClearComposition(state);
    }

    public static bool HasComposition(object driver) => GetState(driver).ImeComposition.Length > 0;

    public static bool ShouldSuppressTypeDelta(object driver)
    {
        var state = GetState(driver);
        return state.PendingSuppressedCommittedText.Length > 0;
    }

    public static void LogSuppressedTypeDelta(object driver, int previousLength)
    {
        var typeDelta = GetTypeDelta(driver);
        if (typeDelta == null || typeDelta.Length <= previousLength)
            return;

        var suppressed = typeDelta.ToString(previousLength, typeDelta.Length - previousLength);
        DebugLog($"Suppressed renderer TypeDelta while IME handled input is active: full=\"{EscapeForLog(typeDelta.ToString())}\", suppressed=\"{EscapeForLog(suppressed)}\", composition=\"{EscapeForLog(GetState(driver).ImeComposition)}\"");
    }

    public static void TrimTypeDelta(object driver, int length)
    {
        if (length < 0)
            return;

        var state = GetState(driver);
        var typeDelta = GetTypeDelta(driver);
        if (typeDelta == null || typeDelta.Length <= length)
            return;

        if (state.PendingSuppressedCommittedText.Length == 0)
            return;

        var appendedLength = typeDelta.Length - length;
        var appendedText = typeDelta.ToString(length, appendedLength);
        var pending = state.PendingSuppressedCommittedText;
        var matchedLength = GetSharedPrefixLength(appendedText, pending);
        if (matchedLength <= 0)
            return;

        typeDelta.Remove(length, matchedLength);
        state.PendingSuppressedCommittedText = matchedLength >= pending.Length
            ? string.Empty
            : pending.Substring(matchedLength);
    }

    public static void RemoveIMEEditingKeys(RenderiteKeyboardState state)
    {
        if (state.heldKeys == null)
            return;

        foreach (var key in ImeKeys.RendererEditingKeys)
            state.heldKeys.Remove(key);
    }

    public static void HandleKeyboardInputActive(object driver, bool keyboardInputActive)
    {
        var state = GetState(driver);
        var wasKeyboardInputActive = state.KeyboardInputActive;
        state.KeyboardInputActive = keyboardInputActive;

        if (keyboardInputActive)
            return;

        if (!wasKeyboardInputActive || state.ImeComposition.Length == 0)
        {
            ClearComposition(state);
            return;
        }

        DebugLog($"Keyboard input became inactive. Canceling composition=\"{EscapeForLog(state.ImeComposition)}\"");
        if (!TrySendMessage(ImeMessageKind.CancelComposition, string.Empty, -1))
            DebugLog("Cancel composition send failed.");

        ClearComposition(state);
    }

    static void HookInputUpdate()
    {
        if (_inputUpdateHooked)
            return;

        InputSystem.onAfterUpdate += OnAfterInputUpdate;
        _inputUpdateHooked = true;
    }

    static void OnAfterInputUpdate()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null || ActiveDrivers.Count == 0)
            return;

        foreach (var driver in ActiveDrivers)
            SynchronizeCompositionCaret(driver, keyboard);
    }

    static void OnIMECompositionChange(object driver, IMECompositionString composition)
    {
        var compositionText = composition.ToString();
        var state = GetState(driver);
        DebugLog($"OnIMECompositionChange: composition=\"{EscapeForLog(compositionText)}\", previous=\"{EscapeForLog(state.ImeComposition)}\", caretOffset={state.CompositionCaretOffset}");

        if (compositionText.Length == 0 && state.ImeComposition.Length > 0)
        {
            state.PendingImplicitCommittedText = state.ImeComposition;
            state.AwaitingImplicitCommitFollowup = true;
            state.PendingComposition = string.Empty;
            state.PendingCompositionCaretOffset = -1;
            state.HasPendingCompositionChange = false;
            DebugLog($"Queued implicit commit awaiting next composition: committed=\"{EscapeForLog(state.PendingImplicitCommittedText)}\"");
            return;
        }

        var nextCaretOffset = compositionText.Length == 0
            ? -1
            : GetNextCompositionCaretOffset(state.ImeComposition, compositionText, state.CompositionCaretOffset);
        state.PendingComposition = compositionText;
        state.PendingCompositionCaretOffset = nextCaretOffset;
        state.HasPendingCompositionChange = true;

        if (compositionText.Length == 0)
            return;

        FlushPendingImeFrame(driver);
    }

    public static void OnUpdateStateFinished(object driver)
    {
        FlushPendingImeFrame(driver);
    }

    public static void SynchronizeCompositionCaret(object driver, Keyboard keyboard)
    {
        var state = GetState(driver);
        if (state.ImeComposition.Length == 0)
        {
            state.PreviousHeldCaretKeys.Clear();
            return;
        }

        foreach (var key in ImeKeys.CaretKeys)
        {
            var isPressed = IsCaretKeyPressed(key, keyboard);
            var wasPressedThisFrame = IsCaretKeyPressedThisFrame(key, keyboard);
            var wasHeld = state.PreviousHeldCaretKeys.Contains(key);

            if ((wasPressedThisFrame || isPressed) && !wasHeld)
                MoveCompositionCaret(state, key);

            if (isPressed)
                state.PreviousHeldCaretKeys.Add(key);
            else
                state.PreviousHeldCaretKeys.Remove(key);
        }
    }

    static void MoveCompositionCaret(DriverState state, RenderiteKey key)
    {
        if (state.ImeComposition.Length == 0)
            return;

        var previousOffset = state.CompositionCaretOffset < 0 ? state.ImeComposition.Length : state.CompositionCaretOffset;
        var nextOffset = previousOffset;

        switch (key)
        {
            case RenderiteKey.LeftArrow:
                nextOffset--;
                break;
            case RenderiteKey.RightArrow:
                nextOffset++;
                break;
            case RenderiteKey.Home:
                nextOffset = 0;
                break;
            case RenderiteKey.End:
                nextOffset = state.ImeComposition.Length;
                break;
        }

        nextOffset = Math.Max(0, Math.Min(nextOffset, state.ImeComposition.Length));
        if (nextOffset == previousOffset)
            return;

        state.CompositionCaretOffset = nextOffset;
        DebugLog($"MoveCompositionCaret: key={key}, nextOffset={nextOffset}, composition=\"{EscapeForLog(state.ImeComposition)}\"");
        if (!TrySendMessage(ImeMessageKind.UpdateComposition, state.ImeComposition, state.CompositionCaretOffset))
            DebugLog("Caret move send failed.");
    }

    static void FlushPendingImeFrame(object driver)
    {
        var state = GetState(driver);
        if (!state.HasPendingCompositionChange)
            return;

        var typeDelta = GetTypeDelta(driver);
        var committedText = typeDelta == null ? string.Empty : ExtractCommittedText(typeDelta.ToString());
        var nextComposition = state.PendingComposition;
        var nextCaretOffset = state.PendingCompositionCaretOffset;
        var kind = nextComposition.Length == 0 ? ImeMessageKind.CommitComposition : ImeMessageKind.UpdateComposition;

        if (state.AwaitingImplicitCommitFollowup && state.PendingImplicitCommittedText.Length > 0)
        {
            if (committedText.Length == 0
                && kind == ImeMessageKind.UpdateComposition
                && ShouldCommitPendingImplicit(state.PendingImplicitCommittedText, nextComposition))
            {
                committedText = state.PendingImplicitCommittedText;
                DebugLog($"Sending composition with implicit commit: committed=\"{EscapeForLog(committedText)}\", composition=\"{EscapeForLog(nextComposition)}\"");
            }
            else
            {
                DebugLog($"Ignored pending implicit commit because next composition looked like a continuation: committed=\"{EscapeForLog(state.PendingImplicitCommittedText)}\", composition=\"{EscapeForLog(nextComposition)}\"");
            }
        }

        if (kind == ImeMessageKind.CommitComposition && state.ImeComposition.Length == 0 && committedText.Length == 0)
        {
            state.HasPendingCompositionChange = false;
            return;
        }

        if (kind == ImeMessageKind.UpdateComposition
            && committedText.Length == 0
            && string.Equals(nextComposition, state.ImeComposition, StringComparison.Ordinal)
            && nextCaretOffset == state.CompositionCaretOffset)
        {
            state.HasPendingCompositionChange = false;
            state.PendingComposition = string.Empty;
            state.PendingCompositionCaretOffset = -1;
            return;
        }

        if (!TrySendMessage(kind, nextComposition, nextCaretOffset, committedText))
        {
            DebugLog($"{kind} send failed.");
            return;
        }

        state.PendingSuppressedCommittedText = committedText;
        state.ImeComposition = nextComposition;
        state.CompositionCaretOffset = nextCaretOffset;
        state.HasPendingCompositionChange = false;
        state.PendingComposition = string.Empty;
        state.PendingCompositionCaretOffset = -1;
        state.PendingImplicitCommittedText = string.Empty;
        state.AwaitingImplicitCommitFollowup = false;

        if (nextComposition.Length == 0)
            ClearComposition(state);
    }

    static bool TrySendMessage(ImeMessageKind kind, string composition, int caretOffset, string committedText = "")
    {
        try
        {
            InitializeMessaging();
            _messenger!.SendObject(ImeInterprocessChannel.MessageId, new ImeInterprocessMessage
            {
                Kind = kind,
                Composition = composition,
                CommittedText = committedText,
                CaretOffset = caretOffset
            });
            return true;
        }
        catch (Exception ex)
        {
            DebugLog($"Interprocess send threw {ex.GetType().Name}: {EscapeForLog(ex.Message)}");
            DisposeMessaging();
            return false;
        }
    }

    static void LogMessengerIdentityOnce()
    {
        if (_messengerIdentityLogged)
            return;

        _messengerIdentityLogged = true;
        DebugLog($"Renderer IME sender: ownerId=\"{ImeInterprocessChannel.OwnerId}\", messageId=\"{ImeInterprocessChannel.MessageId}\", queueName=\"{ImeInterprocessQueue.GetQueueName()}\"");
    }

    static void ClearComposition(DriverState state)
    {
        state.ImeComposition = string.Empty;
        state.CompositionCaretOffset = -1;
        state.PreviousHeldCaretKeys.Clear();
        state.PendingComposition = string.Empty;
        state.PendingCompositionCaretOffset = -1;
        state.HasPendingCompositionChange = false;
        state.PendingImplicitCommittedText = string.Empty;
        state.AwaitingImplicitCommitFollowup = false;
    }

    static string ExtractCommittedText(string typeDelta)
    {
        if (string.IsNullOrEmpty(typeDelta))
            return string.Empty;

        var committedLength = 0;
        while (committedLength < typeDelta.Length && !char.IsControl(typeDelta[committedLength]))
            committedLength++;

        return committedLength == 0 ? string.Empty : typeDelta.Substring(0, committedLength);
    }

    static bool ShouldCommitPendingImplicit(string pendingCommittedText, string nextComposition)
    {
        if (pendingCommittedText.Length == 0 || nextComposition.Length == 0)
            return false;

        if (string.Equals(pendingCommittedText, nextComposition, StringComparison.Ordinal))
            return false;

        if (nextComposition.StartsWith(pendingCommittedText, StringComparison.Ordinal)
            || pendingCommittedText.StartsWith(nextComposition, StringComparison.Ordinal))
        {
            return false;
        }

        if (pendingCommittedText.Length == 1 && IsSingleLatinStarter(nextComposition))
            return false;

        return true;
    }

    static bool IsSingleLatinStarter(string composition) =>
        composition.Length == 1 && IsLatinLetter(composition[0]);

    static bool IsLatinLetter(char value) =>
        (value >= 'A' && value <= 'Z')
        || (value >= 'a' && value <= 'z')
        || (value >= '\uFF21' && value <= '\uFF3A')
        || (value >= '\uFF41' && value <= '\uFF5A');

    static int GetNextCompositionCaretOffset(string previousComposition, string nextComposition, int previousCaretOffset)
    {
        if (nextComposition.Length == 0)
            return -1;

        if (previousComposition.Length == 0 || previousCaretOffset < 0 || previousCaretOffset > previousComposition.Length)
            return nextComposition.Length;

        var insertedLength = nextComposition.Length - previousComposition.Length;
        if (insertedLength > 0)
        {
            var prefix = previousComposition.Substring(0, previousCaretOffset);
            var suffix = previousComposition.Substring(previousCaretOffset);
            if (nextComposition.StartsWith(prefix, StringComparison.Ordinal)
                && nextComposition.EndsWith(suffix, StringComparison.Ordinal))
            {
                return Math.Min(previousCaretOffset + insertedLength, nextComposition.Length);
            }
        }

        var prefixLength = GetSharedPrefixLength(previousComposition, nextComposition);
        var suffixLength = GetSharedSuffixLength(previousComposition, nextComposition, prefixLength);
        var previousChangedEnd = previousComposition.Length - suffixLength;
        var nextChangedEnd = nextComposition.Length - suffixLength;

        if (previousCaretOffset < prefixLength)
            return Math.Min(previousCaretOffset, nextComposition.Length);

        if (previousCaretOffset <= previousChangedEnd)
            return Math.Max(prefixLength, Math.Min(nextChangedEnd, nextComposition.Length));

        var deltaLength = nextComposition.Length - previousComposition.Length;
        return Math.Max(0, Math.Min(previousCaretOffset + deltaLength, nextComposition.Length));
    }

    static int GetSharedPrefixLength(string first, string second)
    {
        var length = Math.Min(first.Length, second.Length);
        for (var i = 0; i < length; i++)
            if (first[i] != second[i])
                return i;

        return length;
    }

    static int GetSharedSuffixLength(string first, string second, int prefixLength)
    {
        var maxLength = Math.Min(first.Length, second.Length) - prefixLength;
        for (var i = 0; i < maxLength; i++)
            if (first[first.Length - 1 - i] != second[second.Length - 1 - i])
                return i;

        return maxLength;
    }

    static bool IsCaretKeyPressedThisFrame(RenderiteKey key, Keyboard keyboard) =>
        GetCaretKeyControl(key, keyboard)?.wasPressedThisFrame == true;

    static bool IsCaretKeyPressed(RenderiteKey key, Keyboard keyboard) =>
        GetCaretKeyControl(key, keyboard)?.isPressed == true;

    static KeyControl? GetCaretKeyControl(RenderiteKey key, Keyboard keyboard)
    {
        switch (key)
        {
            case RenderiteKey.LeftArrow:
                return keyboard.leftArrowKey;
            case RenderiteKey.RightArrow:
                return keyboard.rightArrowKey;
            case RenderiteKey.Home:
                return keyboard.homeKey;
            case RenderiteKey.End:
                return keyboard.endKey;
            default:
                return null;
        }
    }

    public sealed class DriverState
    {
        public string ImeComposition = string.Empty;
        public int CompositionCaretOffset = -1;
        public bool KeyboardInputActive;
        public Action<IMECompositionString>? CompositionHandler;
        public string PendingComposition = string.Empty;
        public int PendingCompositionCaretOffset = -1;
        public bool HasPendingCompositionChange;
        public string PendingSuppressedCommittedText = string.Empty;
        public string PendingImplicitCommittedText = string.Empty;
        public bool AwaitingImplicitCommitFollowup;
        public readonly HashSet<RenderiteKey> PreviousHeldCaretKeys = new();
    }

    static string EscapeForLog(string value) =>
        value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

    static void DebugLog(string message) => RendererPlugin.LogDebugIme(message);
}
