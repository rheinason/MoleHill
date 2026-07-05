using MoleHill.Core.Engine;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class XxHash64BuilderTests
{
    [Fact]
    public void ComputeHash_EmptyInput_MatchesXxHash64Vector()
    {
        ulong hash = XxHash64Builder.ComputeHash(Array.Empty<byte>());

        Assert.Equal(0xEF46DB3751D8E999UL, hash);
    }

    [Fact]
    public void AddBytes_ChunkedInput_MatchesOneShotHash()
    {
        byte[] bytes = Enumerable.Range(0, 257).Select(static i => (byte)i).ToArray();
        var builder = new XxHash64Builder();

        builder.AddBytes(bytes.AsSpan(0, 3));
        builder.AddBytes(bytes.AsSpan(3, 31));
        builder.AddBytes(bytes.AsSpan(34, 97));
        builder.AddBytes(bytes.AsSpan(131));

        Assert.Equal(XxHash64Builder.ComputeHash(bytes), builder.ToUInt64());
    }
}
