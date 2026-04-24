using BepisResoniteWrapper;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.NET.Common;
using HarmonyLib;
using System.Reflection;

namespace ResoniteBetterIMESupport.Engine;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class EnginePlugin : BasePlugin
{
    public const string PluginGuid = "dev.blhsrwznrghfzpr.ResoniteBetterIMESupport.Engine";
    public const string PluginName = "ResoniteBetterIMESupport.Engine";
    public const string PluginVersion = "3.0.2";

    internal static new ManualLogSource Log = null!;
    static ConfigEntry<bool> _enableDebugLogging = null!;

    public override void Load()
    {
        Log = base.Log;
        _enableDebugLogging = Config.Bind("Debug", "EnableDebugLogging", false, "Enable verbose IME debug logging.");
        ResoniteHooks.OnEngineReady += OnEngineReady;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        new Harmony(PluginGuid).PatchAll(Assembly.GetExecutingAssembly());
        Log.LogInfo("ResoniteBetterIMESupport.Engine loaded.");
    }

    static void OnEngineReady()
    {
        Log.LogInfo("Resonite engine is ready. Starting IME bridge.");
        EngineIMEPatch.Start();
    }

    internal static void LogDebugIme(string message)
    {
        if (!_enableDebugLogging.Value)
            return;

        Log.LogInfo($"[IME debug] {message}");
    }

    static void OnProcessExit(object? sender, EventArgs e)
    {
        ResoniteHooks.OnEngineReady -= OnEngineReady;
        EngineIMEPatch.Stop();
    }
}
