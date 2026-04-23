using HarmonyLib;
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
    static readonly ImePipeClient PipeClient = new();

    static FieldInfo? _typeDeltaField;
    static bool _inputUpdateHooked;
    static bool _pipeIdentityLogged;

    public static Type KeyboardDriverType =>
        AccessTools.TypeByName("KeyboardDriver") ?? throw new InvalidOperationException("KeyboardDriver type was not found.");

    public static FieldInfo TypeDeltaField =>
        _typeDeltaField ??= AccessTools.Field(KeyboardDriverType, "typeDelta") ?? throw new InvalidOperationException("KeyboardDriver.typeDelta field was not found.");

    public static DriverState GetState(object driver) => States.GetOrCreateValue(driver);

    public static StringBuilder? GetTypeDelta(object driver) => (StringBuilder?)TypeDeltaField.GetValue(driver);

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

        LogPipeIdentityOnce();
        state.CompositionHandler = composition => OnIMECompositionChange(driver, composition);
        keyboard.onIMECompositionChange += state.CompositionHandler;
        ActiveDrivers.Add(driver);
        HookInputUpdate();
    }

    static void HookInputUpdate()
    {
        if (_inputUpdateHooked)
            return;

        InputSystem.onAfterUpdate += OnAfterInputUpdate;
        _inputUpdateHooked = true;
    }

    static void LogPipeIdentityOnce()
    {
        if (_pipeIdentityLogged)
            return;

        _pipeIdentityLogged = true;
        DebugLog($"Renderer IME pipe client: {ImePipe.PipeDebugInfo}");
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
        state.ImeComposition = string.Empty;
        state.CompositionCaretOffset = -1;
        state.SuppressEmptyCompositionEndUntilTimestamp = 0;
        state.PreviousHeldIMEEditingKeys.Clear();
    }

    public static void ClearComposition(object driver)
    {
        var state = GetState(driver);

        state.ImeComposition = string.Empty;
        state.CompositionCaretOffset = -1;
        state.SuppressEmptyCompositionEndUntilTimestamp = 0;
        state.PreviousHeldIMEEditingKeys.Clear();
    }

    public static void HandleKeyboardInputActive(object driver, bool keyboardInputActive)
    {
        var state = GetState(driver);
        var wasKeyboardInputActive = state.KeyboardInputActive;
        state.KeyboardInputActive = keyboardInputActive;

        if (keyboardInputActive)
        {
            state.IgnoreNextEmptyCompositionCommit = false;
            return;
        }

        if (wasKeyboardInputActive)
            CancelCompositionForInactiveKeyboard(driver);
    }

    public static void CancelCompositionForInactiveKeyboard(object driver)
    {
        var state = GetState(driver);

        if (state.ImeComposition.Length == 0)
        {
            ClearComposition(driver);
            return;
        }

        DebugLog($"CancelCompositionForInactiveKeyboard: composition=\"{EscapeForLog(state.ImeComposition)}\", caretOffset={state.CompositionCaretOffset}");
        if (!PipeClient.SendComposition(string.Empty, string.Empty, -1))
            DebugLog($"CancelCompositionForInactiveKeyboard pipe send failed: {ImePipe.PipeDebugInfo}");

        ClearPendingTypeDelta(driver);
        ClearComposition(driver);
        state.IgnoreNextEmptyCompositionCommit = true;
    }

    public static bool HasComposition(object driver) => !string.IsNullOrEmpty(GetState(driver).ImeComposition);

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
        var compositionString = composition.ToString();
        var state = GetState(driver);
        var typeDelta = GetTypeDelta(driver);
        var committedText = typeDelta?.ToString() ?? string.Empty;
        var backspaceKeyActive = IsCompositionBackspaceKeyActive();
        var deleteKeyActive = IsCompositionDeleteKeyActive();
        var editAction = GetImeEditAction(backspaceKeyActive, deleteKeyActive);
        DebugLog($"OnIMECompositionChange begin: composition=\"{EscapeForLog(compositionString)}\", committed=\"{EscapeForLog(committedText)}\", previousComposition=\"{EscapeForLog(state.ImeComposition)}\", caretOffset={state.CompositionCaretOffset}, typeDeltaLength={typeDelta?.Length ?? -1}, backspaceKeyActive={backspaceKeyActive}, deleteKeyActive={deleteKeyActive}");

        if (compositionString.Length == 0 && state.IgnoreNextEmptyCompositionCommit)
        {
            DebugLog($"OnIMECompositionChange ignoring empty composition commit after keyboard focus loss: committed=\"{EscapeForLog(committedText)}\"");
            state.IgnoreNextEmptyCompositionCommit = false;
            ClearPendingTypeDelta(driver);
            return;
        }

        if (compositionString.Length > 0)
            state.IgnoreNextEmptyCompositionCommit = false;

        if (compositionString.Length == 0
            && state.ImeComposition.Length > 0
            && IsLikelyFocusLossAccumulatedCommit(committedText, state.ImeComposition))
        {
            DebugLog($"OnIMECompositionChange treating accumulated focus-loss TypeDelta as cancel: committedLength={committedText.Length}, previousComposition=\"{EscapeForLog(state.ImeComposition)}\"");
            if (!PipeClient.SendComposition(string.Empty, string.Empty, -1))
                DebugLog($"OnIMECompositionChange focus-loss cancel pipe send failed: {ImePipe.PipeDebugInfo}");

            ClearPendingTypeDelta(driver);
            ClearComposition(driver);
            state.IgnoreNextEmptyCompositionCommit = true;
            return;
        }

        if ((backspaceKeyActive || deleteKeyActive) && state.ImeComposition.Length > 1)
            state.SuppressEmptyCompositionEndUntilTimestamp = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10;

        if (compositionString.Length == 0 && state.ImeComposition.Length > 0 && IsSuppressingCompositionEndAfterDeletion(state)
            && (committedText.Length == 0 || committedText == state.ImeComposition))
        {
            DebugLog("OnIMECompositionChange suppressing transient empty composition after deletion");
            state.SuppressEmptyCompositionEndUntilTimestamp = 0;
            ClearPendingTypeDelta(driver);
            return;
        }


        if (compositionString.Length > 0)
            state.CompositionCaretOffset = GetNextCompositionCaretOffset(state.ImeComposition, compositionString, state.CompositionCaretOffset, backspaceKeyActive);

        if (PipeClient.SendComposition(compositionString, committedText, state.CompositionCaretOffset, editAction))
        {
            DebugLog($"OnIMECompositionChange sent to pipe: composition=\"{EscapeForLog(compositionString)}\", committed=\"{EscapeForLog(committedText)}\", caretOffset={state.CompositionCaretOffset}");
            ClearPendingTypeDelta(driver);
            state.ImeComposition = compositionString;
            if (compositionString.Length == 0)
            {
                state.CompositionCaretOffset = -1;
                state.SuppressEmptyCompositionEndUntilTimestamp = 0;
            }
            return;
        }

        DebugLog($"OnIMECompositionChange pipe send failed, falling back to typeDelta: {ImePipe.PipeDebugInfo}");

        if (compositionString == state.ImeComposition)
            return;

        if (typeDelta == null)
        {
            RendererPlugin.Logger.LogWarning("KeyboardDriver.typeDelta is null. IME composition update was skipped.");
            return;
        }

        var pendingTextInput = TakeTypeDeltaSuffix(driver, state.LastUpdateTypeDeltaLength);
        if (compositionString.Length == 0)
        {
            if (pendingTextInput.Length == 0)
                pendingTextInput = TakeFullTypeDelta(driver);

            if (pendingTextInput.Length > 0 && pendingTextInput != state.ImeComposition)
                ReplaceComposition(typeDelta, state.ImeComposition, pendingTextInput);

            state.ImeComposition = string.Empty;
            state.CompositionCaretOffset = -1;
            state.SuppressEmptyCompositionEndUntilTimestamp = 0;
            return;
        }

        ReplaceComposition(typeDelta, state.ImeComposition, compositionString);
        state.ImeComposition = compositionString;
        state.CompositionCaretOffset = compositionString.Length;
    }

    static ImeEditAction GetImeEditAction(bool backspaceKeyActive, bool deleteKeyActive)
    {
        if (deleteKeyActive)
            return ImeEditAction.Delete;

        if (backspaceKeyActive)
            return ImeEditAction.Backspace;

        return ImeEditAction.None;
    }

    static bool IsCompositionBackspaceKeyActive()
    {
        var keyboard = Keyboard.current;

        if (keyboard == null)
            return false;

        return keyboard.backspaceKey.wasPressedThisFrame
            || keyboard.backspaceKey.isPressed;
    }

    static bool IsCompositionDeleteKeyActive()
    {
        var keyboard = Keyboard.current;

        if (keyboard == null)
            return false;

        return keyboard.deleteKey.wasPressedThisFrame
            || keyboard.deleteKey.isPressed;
    }

    static bool IsSuppressingCompositionEndAfterDeletion(DriverState state)
    {
        if (state.SuppressEmptyCompositionEndUntilTimestamp == 0)
            return false;

        if (Stopwatch.GetTimestamp() <= state.SuppressEmptyCompositionEndUntilTimestamp)
            return true;

        state.SuppressEmptyCompositionEndUntilTimestamp = 0;
        return false;
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

    static int GetNextCompositionCaretOffset(string previousComposition, string nextComposition, int previousCaretOffset, bool backspaceKeyActive)
    {
        if (nextComposition.Length == 0)
            return -1;

        if (previousComposition.Length == 0 || previousCaretOffset < 0 || previousCaretOffset == previousComposition.Length)
            return nextComposition.Length;

        var backspaceDeletionOffset = TryGetBackspaceDeletionOffset(previousComposition, nextComposition, previousCaretOffset, backspaceKeyActive);
        if (backspaceDeletionOffset >= 0)
            return backspaceDeletionOffset;

        var caretInsertionOffset = TryGetCaretInsertionOffset(previousComposition, nextComposition, previousCaretOffset);
        if (caretInsertionOffset >= 0)
            return caretInsertionOffset;

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

    static int TryGetBackspaceDeletionOffset(string previousComposition, string nextComposition, int previousCaretOffset, bool backspaceKeyActive)
    {
        if (!backspaceKeyActive)
            return -1;

        var removedLength = previousComposition.Length - nextComposition.Length;

        if (removedLength <= 0 || previousCaretOffset < removedLength)
            return -1;

        var removedStart = previousCaretOffset - removedLength;
        var expected = previousComposition.Remove(removedStart, removedLength);

        if (!string.Equals(expected, nextComposition, StringComparison.Ordinal))
            return -1;

        return removedStart;
    }

    static int TryGetCaretInsertionOffset(string previousComposition, string nextComposition, int previousCaretOffset)
    {
        var insertedLength = nextComposition.Length - previousComposition.Length;

        if (insertedLength <= 0)
            return -1;

        var prefix = previousComposition.Substring(0, previousCaretOffset);
        var suffix = previousComposition.Substring(previousCaretOffset);

        if (!nextComposition.StartsWith(prefix, StringComparison.Ordinal) || !nextComposition.EndsWith(suffix, StringComparison.Ordinal))
            return -1;

        return Math.Min(previousCaretOffset + insertedLength, nextComposition.Length);
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

    static void ClearPendingTypeDelta(object driver)
    {
        var typeDelta = GetTypeDelta(driver);

        if (typeDelta == null || typeDelta.Length == 0)
            return;

        typeDelta.Length = 0;
    }

    static void ReplaceComposition(StringBuilder typeDelta, string previousComposition, string nextComposition)
    {
        for (var i = 0; i < previousComposition.Length; i++)
            typeDelta.Append('\b');

        typeDelta.Append(nextComposition);
    }

    public static void RemoveIMEEditingKeys(RenderiteKeyboardState state)
    {
        if (state.heldKeys == null)
            return;

        foreach (var key in ImeKeys.RendererEditingKeys)
            state.heldKeys.Remove(key);
    }

    public static void SynchronizeCompositionCaret(object driver, Keyboard keyboard)
    {
        var driverState = GetState(driver);

        if (driverState.ImeComposition.Length == 0)
        {
            driverState.PreviousHeldIMEEditingKeys.Clear();
            return;
        }

        foreach (var key in ImeKeys.CaretKeys)
        {
            var isPressed = IsIMECaretKeyPressed(key, keyboard);
            var wasPressedThisFrame = IsIMECaretKeyPressedThisFrame(key, keyboard);
            var wasHeld = driverState.PreviousHeldIMEEditingKeys.Contains(key);

            if ((wasPressedThisFrame || isPressed) && !wasHeld)
                MoveCompositionCaret(driver, driverState, key);

            if (isPressed)
                driverState.PreviousHeldIMEEditingKeys.Add(key);
            else
                driverState.PreviousHeldIMEEditingKeys.Remove(key);
        }
    }

    static bool IsIMECaretKeyPressedThisFrame(RenderiteKey key, Keyboard keyboard)
    {
        var control = GetIMECaretKeyControl(key, keyboard);
        return control?.wasPressedThisFrame == true;
    }

    static bool IsIMECaretKeyPressed(RenderiteKey key, Keyboard keyboard)
    {
        var control = GetIMECaretKeyControl(key, keyboard);
        return control?.isPressed == true;
    }

    static KeyControl? GetIMECaretKeyControl(RenderiteKey key, Keyboard keyboard)
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
            case RenderiteKey.Backspace:
                return keyboard.backspaceKey;
            case RenderiteKey.Delete:
                return keyboard.deleteKey;
            default:
                return null;
        }
    }

    static void MoveCompositionCaret(object driver, DriverState state, RenderiteKey key)
    {
        var previousOffset = state.CompositionCaretOffset < 0 ? state.ImeComposition.Length : state.CompositionCaretOffset;
        var nextOffset = previousOffset;

        switch (key)
        {
            case RenderiteKey.LeftArrow:
            case RenderiteKey.Backspace:
                nextOffset--;
                break;
            case RenderiteKey.RightArrow:
            case RenderiteKey.Delete:
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
        {
            DebugLog($"MoveCompositionCaret no-op: key={key}, composition=\"{EscapeForLog(state.ImeComposition)}\", offset={previousOffset}");
            return;
        }

        state.CompositionCaretOffset = nextOffset;
        DebugLog($"MoveCompositionCaret send: key={key}, composition=\"{EscapeForLog(state.ImeComposition)}\", previousOffset={previousOffset}, nextOffset={nextOffset}");
        if (!PipeClient.SendComposition(state.ImeComposition, string.Empty, state.CompositionCaretOffset, ImeEditAction.None))
            DebugLog($"MoveCompositionCaret pipe send failed: {ImePipe.PipeDebugInfo}");
    }

    public static void TrimTypeDelta(object driver, int length)
    {
        if (length < 0)
            return;

        var typeDelta = GetTypeDelta(driver);

        if (typeDelta == null || typeDelta.Length <= length)
            return;

        typeDelta.Length = length;
    }

    public sealed class DriverState
    {
        public string ImeComposition = string.Empty;
        public int CompositionCaretOffset = -1;
        public int LastUpdateTypeDeltaLength = -1;
        public long SuppressEmptyCompositionEndUntilTimestamp;
        public bool KeyboardInputActive;
        public bool IgnoreNextEmptyCompositionCommit;
        public Action<IMECompositionString>? CompositionHandler;
        public readonly HashSet<RenderiteKey> PreviousHeldIMEEditingKeys = new();
    }

    static string TakeTypeDeltaSuffix(object driver, int length)
    {
        if (length < 0)
            return string.Empty;

        var typeDelta = GetTypeDelta(driver);

        if (typeDelta == null || typeDelta.Length <= length)
            return string.Empty;

        var suffix = typeDelta.ToString(length, typeDelta.Length - length);
        typeDelta.Length = length;
        return suffix;
    }

    static string TakeFullTypeDelta(object driver)
    {
        var typeDelta = GetTypeDelta(driver);

        if (typeDelta == null || typeDelta.Length == 0)
            return string.Empty;

        var value = typeDelta.ToString();
        typeDelta.Length = 0;
        return value;
    }

    static string EscapeForLog(string value) =>
        value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

    static void DebugLog(string message) => RendererPlugin.LogDebugIme(message);
}
