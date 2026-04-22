using BepInEx;
using BepInEx.Logging;
using BepInEx.NET.Common;
using HarmonyLib;
using System.Reflection;

namespace ResoniteBetterIMESupport.Engine;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class EnginePlugin : BasePlugin
{
    public const string PluginGuid = "dev.yoshi1123.resonite.ResoniteBetterIMESupport.Engine";
    public const string PluginName = "ResoniteBetterIMESupport.Engine";
    public const string PluginVersion = "3.0.0";

    internal static new ManualLogSource Log = null!;

    public override void Load()
    {
        Log = base.Log;
        EngineIMEPatch.Start();
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        new Harmony(PluginGuid).PatchAll(Assembly.GetExecutingAssembly());
        Log.LogInfo("ResoniteBetterIMESupport.Engine loaded.");
    }

    static void OnProcessExit(object? sender, EventArgs e) => EngineIMEPatch.Stop();
}
