using System.Security.Cryptography;
using System.Text;

namespace ResoniteBetterIMESupport.Shared;

internal static class ImeInterprocessQueue
{
    public static string GetQueueName()
    {
        if (TryGetArgumentValue("--bepinex-target", out var bepinexTarget) && !string.IsNullOrWhiteSpace(bepinexTarget))
            return $"ResoniteBetterIMESupport.{HashForQueue(bepinexTarget)}-{ImeInterprocessChannel.OwnerId}";

        if (TryGetArgumentValue("-shmprefix", out var sharedMemoryPrefix) && !string.IsNullOrWhiteSpace(sharedMemoryPrefix))
            return $"{sharedMemoryPrefix}-{ImeInterprocessChannel.OwnerId}";

        if (TryGetArgumentValue("-QueueName", out var queueName) && !string.IsNullOrWhiteSpace(queueName))
        {
            var separatorIndex = queueName.IndexOf('_');
            var queuePrefix = separatorIndex > 0 ? queueName.Substring(0, separatorIndex) : queueName;
            return $"{queuePrefix}-{ImeInterprocessChannel.OwnerId}";
        }

        return $"ResoniteBetterIMESupport.Fallback-{ImeInterprocessChannel.OwnerId}";
    }

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
}
