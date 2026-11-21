using System.Text;
using CardDispenser.Core.Util;
using Xunit;

namespace CardDispenser.Tests;

public class Crc16Tests
{
    [Fact]
    public void Compute_MatchesCcittFalseCheckValue()
    {
        // The implementation is CRC-16/CCITT-FALSE (poly 0x1021, init 0xFFFF, no reflection, no xorout);
        // 0x29B1 is that variant's standard check value for the ASCII string "123456789".
        Assert.Equal((ushort)0x29B1, Crc16.Compute(Encoding.ASCII.GetBytes("123456789")));
    }
}
