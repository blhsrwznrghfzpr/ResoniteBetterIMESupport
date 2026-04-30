using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using ResoniteBetterIMESupport.Shared;
using System.Reflection;

namespace ResoniteBetterIMESupport.Renderer;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class RendererPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "dev.blhsrwznrghfzpr.ResoniteBetterIMESupport.Renderer";
    public const string PluginName = "ResoniteBetterIMESupport.Renderer";
    public const string PluginVersion = "3.0.3";

    internal static new ManualLogSource Logger = null!;
    static ConfigEntry<bool> _enableDebugLogging = null!;

    void Awake()
    {
        Logger = base.Logger;
        _enableDebugLogging = ImePluginConfig.BindEnableDebugLogging(Config);
        Logger.LogInfo($"IME IPC startup diagnostic: {ImeInterprocessQueue.BuildStartupDiagnostic()}");

        KeyboardDriverIMEPatch.InitializeMessaging();
        KeyboardDriverIMEPatch.SyncConfigEntry(_enableDebugLogging);
        new Harmony(PluginGuid).PatchAll(Assembly.GetExecutingAssembly());
        Logger.LogInfo("ResoniteBetterIMESupport.Renderer loaded.");
    }

    void OnDestroy() => KeyboardDriverIMEPatch.DisposeMessaging();

    internal static void LogDebugIme(string message)
    {
        if (!_enableDebugLogging.Value)
            return;

        Logger.LogInfo($"[IME debug] {message}");
    }
}
