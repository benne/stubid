using DotNet.Testcontainers.Containers;

namespace StubId.Testing;

/// <summary>The container started and then would not answer for itself.</summary>
/// <remarks>
/// Carries what the container said, because the alternative is a caller reading a timeout and going
/// to look for docker logs by hand - and by then the container is gone.
/// </remarks>
public sealed class StubIdContainerException : Exception
{
    private StubIdContainerException(string message, string stdout, string stderr, Exception inner)
        : base(message, inner)
    {
        Stdout = stdout;
        Stderr = stderr;
    }

    public string Stdout { get; }

    public string Stderr { get; }

    internal static async Task<StubIdContainerException> DescribeAsync(
        IContainer container, Uri address, Exception failure, CancellationToken ct)
    {
        var (stdout, stderr) = await ReadLogsAsync(container, ct);

        var message = string.Join(
            Environment.NewLine,
            $"StubID started but would not accept its own address ({address}): {failure.Message}",
            "",
            "The two causes worth checking first are an image published before the runtime address",
            "endpoint existed, and a Docker host this process cannot reach on the mapped port.",
            "",
            Tail("stdout", stdout),
            Tail("stderr", stderr));

        return new StubIdContainerException(message, stdout, stderr, failure);
    }

    private static async Task<(string Stdout, string Stderr)> ReadLogsAsync(
        IContainer container, CancellationToken ct)
    {
        try
        {
            return await container.GetLogsAsync(ct: ct);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // Reporting why we could not read the logs is still better than reporting nothing.
            return ("", $"The container's logs could not be read: {failure.Message}");
        }
    }

    private static string Tail(string stream, string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var kept = lines.Length > 40 ? lines[^40..] : lines;

        return kept.Length == 0
            ? $"({stream} was empty)"
            : $"--- {stream} ---{Environment.NewLine}{string.Join('\n', kept)}";
    }
}
