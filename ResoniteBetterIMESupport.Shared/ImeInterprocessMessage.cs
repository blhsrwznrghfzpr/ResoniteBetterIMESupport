using InterprocessLib;
using Renderite.Shared;

namespace ResoniteBetterIMESupport.Shared;

internal static class ImeInterprocessChannel
{
    public const string OwnerId = "ResoniteBetterIMESupport";
    public const string MessageId = "ImeComposition";
}

internal enum ImeMessageKind
{
    UpdateComposition = 0,
    CommitComposition = 1,
    CancelComposition = 2
}

internal sealed class ImeInterprocessMessage : IMemoryPackable
{
    public ImeMessageKind Kind = ImeMessageKind.UpdateComposition;
    public string Composition = string.Empty;
    public int CaretOffset = -1;

    public void Pack(ref MemoryPacker packer)
    {
        packer.Write((int)Kind);
        packer.Write(Composition);
        packer.Write(CaretOffset);
    }

    public void Unpack(ref MemoryUnpacker unpacker)
    {
        var kind = 0;
        unpacker.Read(ref kind);
        Kind = Enum.IsDefined(typeof(ImeMessageKind), kind)
            ? (ImeMessageKind)kind
            : ImeMessageKind.UpdateComposition;
        unpacker.Read(ref Composition);
        unpacker.Read(ref CaretOffset);
    }

    public override string ToString() =>
        $"ImeInterprocessMessage:Kind={Kind},CompositionLength={Composition.Length},CaretOffset={CaretOffset}";
}
