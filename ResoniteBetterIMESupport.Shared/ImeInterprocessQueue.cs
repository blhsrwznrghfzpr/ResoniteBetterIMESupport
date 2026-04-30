using System.Security.Cryptography;
using System.Text;

namespace ResoniteBetterIMESupport.Shared;

internal static class ImeInterprocessQueue
{
    const string FallbackPrefix = "ResoniteBetterIMESupport.Fallback";

    public static string GetQueuePrefix()
    {
        if (TryGetArgumentValue("-shmprefix", out var sharedMemoryPrefix) && !string.IsNullOrWhiteSpace(sharedMemoryPrefix))
            return sharedMemoryPrefix;

        if (TryGetArgumentValue("--bepinex-target", out var bepinexTarget) && !string.IsNullOrWhiteSpace(bepinexTarget))
            return $"ResoniteBetterIMESupport.{HashForQueue(bepinexTarget)}";

        return FallbackPrefix;
    }

    public static string GetQueueName() => $"{GetQueuePrefix()}-{ImeInterprocessChannel.OwnerId}";

    public static string BuildStartupDiagnostic() =>
        $"bepinexTarget=\"{EscapeForLog(GetArgumentValueOrEmpty("--bepinex-target"))}\", shmprefix=\"{EscapeForLog(GetArgumentValueOrEmpty("-shmprefix"))}\", queuePrefix=\"{GetQueuePrefix()}\", queueName=\"{GetQueueName()}\"";

    static bool TryGetArgumentValue(string argumentName, out string value)
    {
        var args = Environment.GetCommandLineArgs();

        for (var i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], argumentName, StringComparison.InvariantCultureIgnoreCase))
                continue;

            value = args[i + 1];
            return true;
        }

        value = string.Empty;
        return false;
    }

    static string GetArgumentValueOrEmpty(string argumentName) =>
        TryGetArgumentValue(argumentName, out var value) ? value : string.Empty;

    static string HashForQueue(string value)
    {
        var normalizedValue = value.Trim().Replace('/', '\\').ToLowerInvariant();
        using (var sha256 = SHA256.Create())
        {
            var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(normalizedValue));
            var builder = new StringBuilder(16);

            for (var i = 0; i < 8 && i < bytes.Length; i++)
                builder.Append(bytes[i].ToString("X2"));

            return builder.ToString();
        }
    }

    static string EscapeForLog(string value) =>
        value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
}
