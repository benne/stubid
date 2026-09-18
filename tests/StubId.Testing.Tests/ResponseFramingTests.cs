using System.Net.Sockets;
using System.Text;

namespace StubId.Testing.Tests;

/// <summary>
/// How an answer is framed on the wire, read from the wire.
/// </summary>
/// <remarks>
/// Every other suite here runs against an in-memory test server, which does no HTTP/1.1 framing:
/// a response whose length was never set looks there exactly like one that set it. So a change
/// that dropped the length passed every one of them while the container answered
/// <c>Transfer-Encoding: chunked</c> - a header no recording carries, in place of one every
/// recorded answer has.
/// <para>
/// Read over a socket rather than through <c>HttpClient</c>, which decodes chunked transfer and
/// would hide the difference again. The request is the smallest that gets an answer, and
/// <c>Connection: close</c> means the read ends without parsing the body.
/// </para>
/// </remarks>
[Trait("Category", "Container")]
[Collection(StubIdCollection.Name)]
public class ResponseFramingTests(StubIdInstance stub)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/op/.well-known/openid-configuration")]
    [InlineData("/op/.well-known/openid-configuration/jwks")]
    public async Task A_document_is_framed_by_its_length(string path)
    {
        var head = await HeadAsync(path);

        Assert.Contains("Content-Length:", head, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Transfer-Encoding:", head, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The response head, as bytes off the socket.</summary>
    private async Task<string> HeadAsync(string path)
    {
        var address = stub.Container.MappedAddress;

        using var socket = new TcpClient();
        await socket.ConnectAsync(address.Host, address.Port, Ct);

        await using var stream = socket.GetStream();

        var request = Encoding.ASCII.GetBytes(
            $"GET {path} HTTP/1.1\r\nHost: {address.Host}:{address.Port}\r\nConnection: close\r\n\r\n");

        await stream.WriteAsync(request, Ct);

        using var reader = new StreamReader(stream, Encoding.ASCII);
        var answer = await reader.ReadToEndAsync(Ct);

        return answer.Split("\r\n\r\n", 2)[0];
    }
}
