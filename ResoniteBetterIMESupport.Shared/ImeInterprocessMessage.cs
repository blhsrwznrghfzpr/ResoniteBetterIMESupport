using InterprocessLib;
using Renderite.Shared;

namespace ResoniteBetterIMESupport.Shared;

internal static class ImeInterprocessChannel
{
    public const string OwnerId = "ResoniteBetterIMESupport";
    public const string QueueName = "ResoniteBetterIMESupport.Ime";
    public const string MessageId = "ImeComposition";
}

internal sealed class ImeInterprocessMessage : IMemoryPackable
{
    public string Composition = string.Empty;
    public int CompositionCursor = -1;
    public bool HasCommittedResult;

    public void Pack(ref MemoryPacker packer)
    {
        packer.Write(Composition);
        packer.Write(CompositionCursor);
        packer.Write(HasCommittedResult);
    }

    public void Unpack(ref MemoryUnpacker unpacker)
    {
        unpacker.Read(ref Composition);
        unpacker.Read(ref CompositionCursor);
        unpacker.Read(ref HasCommittedResult);
    }

    public override string ToString() =>
        $"ImeInterprocessMessage:CompositionLength={Composition.Length},CompositionCursor={CompositionCursor},HasCommittedResult={HasCommittedResult}";
}
