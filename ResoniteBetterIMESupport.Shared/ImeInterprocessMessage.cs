using InterprocessLib;
using Renderite.Shared;

namespace ResoniteBetterIMESupport.Shared;

internal static class ImeInterprocessChannel
{
    public const string OwnerId = "ResoniteBetterIMESupport";
    public const string MessageId = "ImeComposition";
}

internal sealed class ImeInterprocessMessage : IMemoryPackable
{
    public string Composition = string.Empty;

    public void Pack(ref MemoryPacker packer)
    {
        packer.Write(Composition);
    }

    public void Unpack(ref MemoryUnpacker unpacker)
    {
        unpacker.Read(ref Composition);
    }

    public override string ToString() =>
        $"ImeInterprocessMessage:CompositionLength={Composition.Length}";
}
