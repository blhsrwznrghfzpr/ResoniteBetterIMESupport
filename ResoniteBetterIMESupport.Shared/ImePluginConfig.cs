using BepInEx.Configuration;

namespace ResoniteBetterIMESupport.Shared;

internal static class ImePluginConfig
{
    const string DebugSection = "Debug";
    const string EnableDebugLoggingKey = "EnableDebugLogging";
    const string EnableDebugLoggingDescription = "Enable verbose IME debug logging.";

    public static ConfigEntry<bool> BindEnableDebugLogging(ConfigFile config) =>
        config.Bind(DebugSection, EnableDebugLoggingKey, false, EnableDebugLoggingDescription);
}
