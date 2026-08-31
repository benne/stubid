using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Images;

namespace StubId.Testing.Tests;

/// <summary>Which image these tests run.</summary>
/// <remarks>
/// CI builds one and names it, because the same image has already been through the container
/// verification script by then and running a second build would prove a different artefact. A
/// developer with no image named gets one built from this repository's own Dockerfile, so the tests
/// cannot pass against a published image while the working tree is broken.
/// </remarks>
internal static class StubIdImage
{
    private const string Named = "STUBID_TEST_IMAGE";

    public static async ValueTask<string> ResolveAsync(CancellationToken ct)
    {
        if (Environment.GetEnvironmentVariable(Named) is { Length: > 0 } named)
        {
            return named;
        }

        var image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(CommonDirectoryPath.GetSolutionDirectory(), string.Empty)
            .WithDockerfile("Dockerfile")
            .WithName("stubid:testcontainers-local")
            // Built once and kept: a publish inside the SDK image is a minute on a cold layer cache,
            // and it is the same tree every time. Delete the image to force a rebuild.
            .WithImageBuildPolicy(PullPolicy.Missing)
            .WithDeleteIfExists(false)
            .WithCleanUp(false)
            .Build();

        await image.CreateAsync(ct);

        return image.FullName;
    }
}
