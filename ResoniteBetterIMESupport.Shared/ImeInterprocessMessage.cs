using InterprocessLib;
using Renderite.Shared;

namespace ResoniteBetterIMESupport.Shared;

internal static class ImeInterprocessChannel
{
    public const string OwnerId = "ResoniteBetterIMESupport";
    public const string MessageId = "ImeComposition";
}

internal enum ImeEditAction
{
    None = 0,
    Backspace = 1,
    Delete = 2
}

internal sealed class ImeInterprocessMessage : IMemoryPackable
{
    public string Composition = string.Empty;
    public string CommittedText = string.Empty;
    public int CaretOffset = -1;
    public ImeEditAction EditAction = ImeEditAction.None;

    public void Pack(ref MemoryPacker packer)
    {
        packer.Write(Composition);
        packer.Write(CommittedText);
        packer.Write(CaretOffset);
        packer.Write((int)EditAction);
    }

    public void Unpack(ref MemoryUnpacker unpacker)
    {
        unpacker.Read(ref Composition);
        unpacker.Read(ref CommittedText);
        unpacker.Read(ref CaretOffset);

        var editAction = 0;
        unpacker.Read(ref editAction);
        EditAction = Enum.IsDefined(typeof(ImeEditAction), editAction)
            ? (ImeEditAction)editAction
            : ImeEditAction.None;
    }

    public override string ToString() =>
        $"ImeInterprocessMessage:CompositionLength={Composition.Length},CommittedLength={CommittedText.Length},CaretOffset={CaretOffset},EditAction={EditAction}";
}
