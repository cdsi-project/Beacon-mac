using System.Net;
using CDSI.Agent.Infrastructure.Reader;

namespace CDSI.Agent.Infrastructure.Tests.Reader;

public sealed class ReaderNetworkPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("2001:4860:4860::8888", false)]
    [InlineData("fc00::1", true)]
    public void AddressClassification_SeparatesPrivateAndPublicAddresses(
        string value,
        bool expected)
    {
        Assert.Equal(
            expected,
            ReaderHttpFeedClient.IsPrivateOrSpecialAddress(IPAddress.Parse(value)));
    }
}
