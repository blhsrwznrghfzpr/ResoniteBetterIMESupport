using HarmonyLib;
using InterprocessLib;
using ResoniteBetterIMESupport.Shared;
using System.Runtime.CompilerServices;
using UnityEngine.InputSystem;
using IMECompositionString = UnityEngine.InputSystem.LowLevel.IMECompositionString;

namespace ResoniteBetterIMESupport.Renderer;

static class KeyboardDriverIMEPatch
{
    static readonly ConditionalWeakTable<object, DriverState> States = new();

    static bool _messengerIdentityLogged;
    static Messenger? _messenger;

    public static Type KeyboardDriverType =>
        AccessTools.TypeByName("KeyboardDriver") ?? throw new InvalidOperationException("KeyboardDriver type was not found.");

    public static DriverState GetState(object driver) => States.GetOrCreateValue(driver);

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
    }

    public static void HandleKeyboardInputActive(object driver, bool keyboardInputActive)
    {
        var state = GetState(driver);
        var wasKeyboardInputActive = state.KeyboardInputActive;
        state.KeyboardInputActive = keyboardInputActive;

        if (keyboardInputActive)
        {
            if (!wasKeyboardInputActive)
            {
                DebugLog("Keyboard input became active. Reinitializing IME state.");
                ClearComposition(state);
            }

            return;
        }

        if (!wasKeyboardInputActive || state.ImeComposition.Length == 0)
        {
            ClearComposition(state);
            return;
        }

        DebugLog($"Keyboard input became inactive. Canceling composition=\"{EscapeForLog(state.ImeComposition)}\"");
        if (!TrySendComposition(string.Empty, -1))
            DebugLog("Composition clear send failed.");

        ClearComposition(state);
    }

    public static bool ShouldAllowTextInput(object driver, char value)
    {
        var state = GetState(driver);
        if (state.ImeComposition.Length == 0)
            return true;

        DebugLog($"Suppressed text input during composition: char=0x{(int)value:X4}, composition=\"{EscapeForLog(state.ImeComposition)}\"");
        return false;
    }

    static void OnIMECompositionChange(object driver, IMECompositionString composition)
    {
        var compositionText = composition.ToString();
        var state = GetState(driver);
        var compositionCursor = -1;
        var hasCommittedResult = false;
        if (WindowsImeContextReader.TryGetCursorPosition(compositionText, out var windowsCursor, out var windowsHasCommittedResult, out _, out var windowsImeDiagnostic))
        {
            compositionCursor = windowsCursor;
        }

        hasCommittedResult = windowsHasCommittedResult;

        DebugLog($"OnIMECompositionChange: composition=\"{EscapeForLog(compositionText)}\", previous=\"{EscapeForLog(state.ImeComposition)}\", windowsIme={windowsImeDiagnostic}");

        if (!TrySendComposition(compositionText, compositionCursor, hasCommittedResult))
        {
            DebugLog("Composition update send failed.");
            return;
        }

        state.ImeComposition = compositionText;
        if (compositionText.Length == 0)
            ClearComposition(state);
    }

    static bool TrySendComposition(string composition, int compositionCursor, bool hasCommittedResult = false)
    {
        try
        {
            InitializeMessaging();
            _messenger!.SendObject(ImeInterprocessChannel.MessageId, new ImeInterprocessMessage
            {
                Composition = composition,
                CompositionCursor = compositionCursor,
                HasCommittedResult = hasCommittedResult
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
    }

    public sealed class DriverState
    {
        public string ImeComposition = string.Empty;
        public bool KeyboardInputActive;
        public Action<IMECompositionString>? CompositionHandler;
    }

    static string EscapeForLog(string value) =>
        value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

    static void DebugLog(string message) => RendererPlugin.LogDebugIme(message);
}
