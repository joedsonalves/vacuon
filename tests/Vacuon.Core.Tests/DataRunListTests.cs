using Vacuon.Native.Ntfs;
using Xunit;

namespace Vacuon.Core.Tests;

public class DataRunListTests
{
    [Fact]
    public void Decode_SingleContiguousRun()
    {
        // 0x21 = 1 byte de contagem, 2 bytes de deslocamento
        // contagem = 0x18 (24 clusters), deslocamento = 0x0234 (564)
        byte[] runs = [0x21, 0x18, 0x34, 0x02, 0x00];

        List<DataRun> decoded = DataRunList.Decode(runs);

        DataRun run = Assert.Single(decoded);
        Assert.Equal(24, run.ClusterCount);
        Assert.Equal(564, run.Lcn);
    }

    [Fact]
    public void Decode_OffsetsAreRelativeToThePreviousRun()
    {
        // Este é o detalhe que faz a MFT fragmentada funcionar: o segundo run traz
        // um DELTA, não um endereço absoluto.
        byte[] runs =
        [
            0x11, 0x10, 0x20,       // 16 clusters em LCN 0x20 (32)
            0x11, 0x08, 0x10,       // 8 clusters em LCN 32 + 16 = 48
            0x00,
        ];

        List<DataRun> decoded = DataRunList.Decode(runs);

        Assert.Equal(2, decoded.Count);
        Assert.Equal(32, decoded[0].Lcn);
        Assert.Equal(48, decoded[1].Lcn);
    }

    [Fact]
    public void Decode_HandlesNegativeOffset()
    {
        // Fragmento que fica ANTES do anterior no disco. Sem extensão de sinal,
        // o LCN vira um número gigante e a leitura vai para o vazio.
        byte[] runs =
        [
            0x11, 0x10, 0x64,       // 16 clusters em LCN 100
            0x11, 0x08, 0xC0,       // delta = -64  →  LCN 36
            0x00,
        ];

        List<DataRun> decoded = DataRunList.Decode(runs);

        Assert.Equal(2, decoded.Count);
        Assert.Equal(100, decoded[0].Lcn);
        Assert.Equal(36, decoded[1].Lcn);
    }

    [Fact]
    public void Decode_MarksSparseRunWhenOffsetFieldIsAbsent()
    {
        byte[] runs =
        [
            0x11, 0x10, 0x20,       // 16 clusters reais
            0x01, 0x20,             // 32 clusters esparsos (nibble alto = 0)
            0x00,
        ];

        List<DataRun> decoded = DataRunList.Decode(runs);

        Assert.Equal(2, decoded.Count);
        Assert.False(decoded[0].IsSparse);
        Assert.True(decoded[1].IsSparse);
        Assert.Equal(32, decoded[1].ClusterCount);
    }

    [Fact]
    public void Decode_StopsAtTerminator()
    {
        byte[] runs = [0x11, 0x10, 0x20, 0x00, 0x11, 0x99, 0x99];

        List<DataRun> decoded = DataRunList.Decode(runs);

        Assert.Single(decoded);
    }

    [Fact]
    public void Decode_StopsOnTruncatedList()
    {
        // Lista que promete 4 bytes de deslocamento e acaba antes.
        byte[] runs = [0x41, 0x10, 0x20];

        List<DataRun> decoded = DataRunList.Decode(runs);

        Assert.Empty(decoded);
    }

    [Fact]
    public void Decode_MultiByteCounts()
    {
        // 0x32: 2 bytes de contagem, 3 bytes de deslocamento — comum em volumes grandes.
        byte[] runs = [0x32, 0x00, 0x10, 0x00, 0x00, 0x01, 0x00];

        List<DataRun> decoded = DataRunList.Decode(runs);

        DataRun run = Assert.Single(decoded);
        Assert.Equal(0x1000, run.ClusterCount);
        Assert.Equal(0x010000, run.Lcn);
    }
}
