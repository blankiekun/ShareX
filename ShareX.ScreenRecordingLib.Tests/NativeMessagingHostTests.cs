using ShareX.HelpersLib;
using System.Text;
using Xunit;

namespace ShareX.ScreenRecordingLib.Tests;

public class NativeMessagingHostTests
{
    private static NativeMessagingHost HostReading(params byte[] input) => new(new MemoryStream(input), new MemoryStream());

    private static byte[] Frame(string text)
    {
        byte[] data = Encoding.UTF8.GetBytes(text);
        return [.. BitConverter.GetBytes(data.Length), .. data];
    }

    // Catches: the length prefix sizing an allocation before anything checks it, so a bad or hostile sender makes the host claim 2 GB.
    [Fact]
    public void Read_RefusesAMessageOverTheLimitWithoutReadingIt()
    {
        byte[] prefix = BitConverter.GetBytes(NativeMessagingHost.MaxInputLength + 1);

        Assert.Throws<InvalidDataException>(() => HostReading(prefix).Read());
    }

    // Catches: a negative length, the top bit of the prefix set, reaching new byte[] and throwing as if the host had crashed.
    [Fact]
    public void Read_TreatsANegativeLengthAsNoMessage()
    {
        Assert.Null(HostReading(BitConverter.GetBytes(-1)).Read());
    }

    // Catches: a sender that dies mid-message being handed on as a shorter message that still gets uploaded.
    [Fact]
    public void Read_RefusesATruncatedMessage()
    {
        byte[] frame = Frame("{\"Text\":\"page text\"}");

        Assert.Throws<EndOfStreamException>(() => HostReading(frame[..^3]).Read());
    }

    // Catches: decoding by another encoding or counting characters rather than bytes, which breaks non-Latin page text.
    [Fact]
    public void Read_KeepsMultibyteText()
    {
        Assert.Equal("\u65e5\u672c\u8a9e", HostReading(Frame("\u65e5\u672c\u8a9e")).Read());
    }

    // Catches: echoing a long page back. Browsers drop the connection on a message over 1 MB from a host, so the extension reports a failure for an upload that went fine.
    [Fact]
    public void Write_ShortensAReplyTooLongForABrowser()
    {
        MemoryStream output = new();
        new NativeMessagingHost(new MemoryStream(), output).Write(new string('x', NativeMessagingHost.MaxOutputLength + 1));

        Assert.Equal(Frame("{}"), output.ToArray());
    }

    // Catches: a page's link or image address reaching the downloader with a scheme that reads local files or runs something.
    [Theory]
    [InlineData("file:///home/user/.ssh/id_rsa")]
    [InlineData("/etc/passwd")]
    [InlineData(" file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com/a")]
    [InlineData("blob:https://example.com/1")]
    [InlineData("example.com/a.png")]
    [InlineData("")]
    public void IsHttpURL_RefusesAnythingButTheWeb(string url)
    {
        Assert.False(URLHelpers.IsHttpURL(url));
    }

    // Catches: a refusal so strict it stops real links: schemes are case-insensitive, and addresses on the local network are valid uploads here.
    [Theory]
    [InlineData("https://example.com/a.png?x=1")]
    [InlineData("HTTP://EXAMPLE.COM/A.PNG")]
    [InlineData("http://192.0.2.1:8080/a")]
    public void IsHttpURL_AcceptsWebAddresses(string url)
    {
        Assert.True(URLHelpers.IsHttpURL(url));
    }
}
