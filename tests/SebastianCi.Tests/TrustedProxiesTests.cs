using System.Net;
using SebastianCi.Web;

namespace SebastianCi.Tests;

public class TrustedProxiesTests
{
    [Fact]
    public void Parse_AlwaysContainsLoopbackOnlyByDefault()
        => Assert.Equal([IPAddress.Loopback, IPAddress.IPv6Loopback], TrustedProxies.Parse(null));

    [Fact]
    public void Parse_AddsValidAddressesAndIgnoresGarbage()
    {
        IReadOnlyList<IPAddress> result = TrustedProxies.Parse(" 10.0.0.5, not-an-ip ,127.0.0.1,2001:db8::1,");

        Assert.Equal(4, result.Count);
        Assert.Contains(IPAddress.Parse("10.0.0.5"), result);
        Assert.Contains(IPAddress.Parse("2001:db8::1"), result);
    }
}
