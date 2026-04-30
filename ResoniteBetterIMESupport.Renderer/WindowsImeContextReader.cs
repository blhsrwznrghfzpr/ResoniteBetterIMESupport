using System.Runtime.InteropServices;

namespace ResoniteBetterIMESupport.Renderer;

static class WindowsImeContextReader
{
    const int GCS_COMPSTR = 0x0008;
    const int GCS_CURSORPOS = 0x0080;
    const int GCS_RESULTSTR = 0x0800;
    const int IMM_ERROR_NODATA = -1;
    const int IMM_ERROR_GENERAL = -2;

    public static bool TryGetCursorPosition(string unityComposition, out int cursorPosition, out bool hasCommittedResult, out string committedResult, out string diagnostic)
    {
        cursorPosition = -1;
        hasCommittedResult = false;
        committedResult = string.Empty;
        diagnostic = "unavailable";

        if (!IsWindows())
        {
            diagnostic = "not-windows";
            return false;
        }

        try
        {
            return TryGetCursorPositionFromImm32(unityComposition, out cursorPosition, out hasCommittedResult, out committedResult, out diagnostic);
        }
        catch (Exception ex)
        {
            cursorPosition = -1;
            hasCommittedResult = false;
            committedResult = string.Empty;
            diagnostic = $"native-ime-unavailable, exception={ex.GetType().Name}, message=\"{EscapeForLog(ex.Message)}\"";
            return false;
        }
    }

    static bool TryGetCursorPositionFromImm32(string unityComposition, out int cursorPosition, out bool hasCommittedResult, out string committedResult, out string diagnostic)
    {
        cursorPosition = -1;
        hasCommittedResult = false;
        committedResult = string.Empty;
        diagnostic = "unavailable";

        var hwnd = GetActiveWindow();
        var hwndSource = "active";
        if (hwnd == IntPtr.Zero)
        {
            hwnd = GetForegroundWindow();
            hwndSource = "foreground";
        }

        if (hwnd == IntPtr.Zero)
        {
            diagnostic = "no-hwnd";
            return false;
        }

        var himc = ImmGetContext(hwnd);
        if (himc == IntPtr.Zero)
        {
            diagnostic = $"no-himc, hwndSource={hwndSource}, hwnd=0x{hwnd.ToInt64():X}";
            return false;
        }

        try
        {
            var immComposition = TryGetCompositionString(himc, GCS_COMPSTR, out var stringStatus);
            var immResult = TryGetCompositionString(himc, GCS_RESULTSTR, out var resultStatus);
            committedResult = immResult ?? string.Empty;
            hasCommittedResult = committedResult.Length > 0;
            var rawCursor = ImmGetCompositionStringW(himc, GCS_CURSORPOS, IntPtr.Zero, 0);
            if (rawCursor == IMM_ERROR_NODATA || rawCursor == IMM_ERROR_GENERAL)
            {
                diagnostic = $"cursor={FormatImmValue(rawCursor)}, compStringStatus={FormatImmValue(stringStatus)}, compString=\"{EscapeForLog(immComposition ?? "<null>")}\", resultStatus={FormatImmValue(resultStatus)}, resultString=\"{EscapeForLog(committedResult)}\", hasResult={hasCommittedResult}";
                return false;
            }

            var normalizedCursor = rawCursor & 0xFFFF;
            var compositionMatches = immComposition == null || immComposition == unityComposition;
            diagnostic = $"cursor={normalizedCursor}, compStringStatus={FormatImmValue(stringStatus)}, compString=\"{EscapeForLog(immComposition ?? "<null>")}\", resultStatus={FormatImmValue(resultStatus)}, resultString=\"{EscapeForLog(committedResult)}\", hasResult={hasCommittedResult}, unityMatch={compositionMatches}";
            if (!compositionMatches)
                return false;

            cursorPosition = normalizedCursor;
            return true;
        }
        finally
        {
            TryReleaseContext(hwnd, himc);
        }
    }

    static void TryReleaseContext(IntPtr hwnd, IntPtr himc)
    {
        try
        {
            ImmReleaseContext(hwnd, himc);
        }
        catch
        {
        }
    }

    static string? TryGetCompositionString(IntPtr himc, int index, out int status)
    {
        var byteLength = ImmGetCompositionStringW(himc, index, IntPtr.Zero, 0);
        status = byteLength;
        if (byteLength <= 0)
            return byteLength == 0 ? string.Empty : null;

        var buffer = Marshal.AllocHGlobal(byteLength);
        try
        {
            var copied = ImmGetCompositionStringW(himc, index, buffer, byteLength);
            status = copied;
            return copied <= 0 ? copied == 0 ? string.Empty : null : Marshal.PtrToStringUni(buffer, copied / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    static bool IsWindows() =>
        Environment.OSVersion.Platform == PlatformID.Win32NT
        || Environment.OSVersion.Platform == PlatformID.Win32S
        || Environment.OSVersion.Platform == PlatformID.Win32Windows
        || Environment.OSVersion.Platform == PlatformID.WinCE;

    static string FormatImmValue(int value) =>
        value switch
        {
            IMM_ERROR_NODATA => "IMM_ERROR_NODATA",
            IMM_ERROR_GENERAL => "IMM_ERROR_GENERAL",
            _ => value.ToString()
        };

    static string EscapeForLog(string value) =>
        value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

    [DllImport("user32.dll")]
    static extern IntPtr GetActiveWindow();

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("imm32.dll")]
    static extern IntPtr ImmGetContext(IntPtr hWnd);

    [DllImport("imm32.dll")]
    static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);

    [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
    static extern int ImmGetCompositionStringW(IntPtr hIMC, int dwIndex, IntPtr lpBuf, int dwBufLen);
}
