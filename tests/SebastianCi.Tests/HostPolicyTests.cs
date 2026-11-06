using SebastianCi.Web;

namespace SebastianCi.Tests;

public class HostPolicyTests
{
    [Theory]
    [InlineData("localhost", true)]
    [InlineData("localhost:8080", true)]
    [InlineData("LOCALHOST.:8080", true)]
    [InlineData("127.0.0.1:8080", true)]
    [InlineData("127.0.0.2", true)]
    [InlineData("[::1]:8080", true)]
    [InlineData("evil.example.com", false)]
    [InlineData("evil.example.com:8080", false)]
    [InlineData("localhost.evil.com", false)]
    [InlineData("127.0.0.1.evil.com", false)]
    [InlineData("192.168.0.10:8080", false)]
    [InlineData("[::1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsLoopbackHost_AcceptsOnlyLoopbackNames(string? host, bool expected)
        => Assert.Equal(expected, HostPolicy.IsLoopbackHost(host));
}
