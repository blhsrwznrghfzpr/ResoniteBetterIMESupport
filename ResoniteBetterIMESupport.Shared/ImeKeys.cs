using Renderite.Shared;

namespace ResoniteBetterIMESupport.Shared;

internal static class ImeKeys
{
    public static readonly Key[] TextEditorEditingKeys =
    {
        Key.LeftArrow,
        Key.RightArrow,
        Key.UpArrow,
        Key.DownArrow,
        Key.Backspace,
        Key.Delete,
        Key.Home,
        Key.End
    };

    public static readonly Key[] RendererEditingKeys =
    {
        Key.LeftArrow,
        Key.RightArrow,
        Key.UpArrow,
        Key.DownArrow,
        Key.Backspace,
        Key.Delete,
        Key.Home,
        Key.End,
        Key.PageUp,
        Key.PageDown
    };

    public static readonly Key[] CaretKeys =
    {
        Key.LeftArrow,
        Key.RightArrow,
        Key.Home,
        Key.End
    };
}
