using BepInEx.Logging;

namespace ResoniteBetterIMESupport.Renderer;

static class LegacyPluginWarning
{
    public static void WarnIfLoaded(ManualLogSource logger)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!string.Equals(assembly.GetName().Name, "NeosBetterIMESupport", StringComparison.OrdinalIgnoreCase))
                continue;

            logger.LogWarning("Legacy NeosBetterIMESupport is also loaded. It attaches its own IME handler and can cause duplicated composition text. Remove or disable the old NeosBetterIMESupport plugin folder from BepInEx/plugins.");
            return;
        }
    }
}
