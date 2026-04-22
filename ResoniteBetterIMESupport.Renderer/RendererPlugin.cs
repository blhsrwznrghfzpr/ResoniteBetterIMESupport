using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System.Reflection;

namespace ResoniteBetterIMESupport.Renderer;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class RendererPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "dev.blhsrwznrghfzpr.ResoniteBetterIMESupport.Renderer";
    public const string PluginName = "ResoniteBetterIMESupport.Renderer";
    public const string PluginVersion = "3.0.0";

    internal static new ManualLogSource Logger = null!;
    static ConfigEntry<bool> _enableDebugLogging = null!;

    void Awake()
    {
        Logger = base.Logger;
        _enableDebugLogging = Config.Bind("Debug", "EnableDebugLogging", false, "Enable verbose IME debug logging.");

        LegacyPluginWarning.WarnIfLoaded(Logger);
        new Harmony(PluginGuid).PatchAll(Assembly.GetExecutingAssembly());
        Logger.LogInfo("ResoniteBetterIMESupport.Renderer loaded.");
    }

    internal static void LogDebugIme(string message)
    {
        if (!_enableDebugLogging.Value)
            return;

        Logger.LogInfo($"[IME debug] {message}");
    }
}
