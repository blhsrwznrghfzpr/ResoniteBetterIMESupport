using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System.Reflection;

namespace ResoniteBetterIMESupport.Renderer;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class RendererPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "dev.yoshi1123.resonite.ResoniteBetterIMESupport.Renderer";
    public const string PluginName = "ResoniteBetterIMESupport.Renderer";
    public const string PluginVersion = "3.0.0";

    internal static new ManualLogSource Logger = null!;

    void Awake()
    {
        Logger = base.Logger;

        LegacyPluginWarning.WarnIfLoaded(Logger);
        new Harmony(PluginGuid).PatchAll(Assembly.GetExecutingAssembly());
        Logger.LogInfo("ResoniteBetterIMESupport.Renderer loaded.");
    }
}
