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
        var deleteKeyActive = IsCompositionDeleteKeyActive();

        if (deleteKeyActive && state.ImeComposition.Length > 1)
            state.SuppressEmptyCompositionEndUntilTimestamp = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10;

        if (compositionString.Length == 0 && state.ImeComposition.Length > 0 && IsSuppressingCompositionEndAfterDeletion(state)
            && (committedText.Length == 0 || committedText == state.ImeComposition))
        {
            state.SuppressEmptyCompositionEndUntilTimestamp = 0;
            ClearPendingTypeDelta(driver);
            return;
        }

        if (compositionString.Length > 0)
            state.CompositionCaretOffset = GetNextCompositionCaretOffset(state.ImeComposition, compositionString, state.CompositionCaretOffset);

        if (PipeClient.SendComposition(compositionString, committedText, state.CompositionCaretOffset))
        {
            ClearPendingTypeDelta(driver);
            state.ImeComposition = compositionString;
            if (compositionString.Length == 0)
            {
                state.CompositionCaretOffset = -1;
                state.SuppressEmptyCompositionEndUntilTimestamp = 0;
            }
            return;
        }

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

    static int GetNextCompositionCaretOffset(string previousComposition, string nextComposition, int previousCaretOffset)
    {
        if (nextComposition.Length == 0)
            return -1;

        if (previousComposition.Length == 0 || previousCaretOffset < 0 || previousCaretOffset == previousComposition.Length)
            return nextComposition.Length;

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

        foreach (var key in ImeKeys.EditingKeys)
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
            return;

        state.CompositionCaretOffset = nextOffset;
        PipeClient.SendComposition(state.ImeComposition, string.Empty, state.CompositionCaretOffset);
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
}
