using Layerlapse.Core.Setup;

namespace Layerlapse.Core.Tests;

public class PrinterAddressTests
{
    [Theory]
    [InlineData("192.168.1.50")]
    [InlineData(" 10.0.0.7 ")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("169.254.10.10")]
    public void Accepts_local_addresses(string input) => Assert.Null(PrinterAddress.Validate(input));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("192.169.1.1")]
    [InlineData("127.0.0.1")]
    [InlineData("192.168.1")]
    [InlineData("fe80::1")]
    [InlineData("printer.local")]
    [InlineData("")]
    [InlineData(null)]
    public void Rejects_everything_else(string? input) => Assert.NotNull(PrinterAddress.Validate(input));
}
