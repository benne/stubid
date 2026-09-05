using System.Runtime.CompilerServices;

namespace StubId.Interop.AspNetCore;

/// <summary>
/// Stops every host this assembly builds from watching its configuration files.
/// </summary>
/// <remarks>
/// Each host a <c>WebApplicationFactory</c> builds installs a file watcher so that editing
/// appsettings reconfigures it while it runs. On Linux each watcher is an inotify instance and a
/// user gets 128 of them; this assembly builds two dozen hosts across classes xUnit runs in
/// parallel, so the suite sat close to the limit and then crossed it.
/// <para>
/// Crossing it is a bad failure to debug. It surfaces as an IOException about file descriptors in
/// whichever test happened to build the next host, so the tests that fail are arbitrary, differ
/// between runs, and name nothing to do with what they assert. It cost an afternoon once already.
/// </para>
/// <para>
/// Set here rather than on each factory because the budget belongs to the process, not to a class:
/// a fix that lives in one test class is a fix the next class added does not get. Nothing in this
/// assembly edits its configuration while a host is running, so the watching was never buying
/// anything.
/// </para>
/// </remarks>
internal static class HostWatching
{
    [ModuleInitializer]
    internal static void Off() =>
        Environment.SetEnvironmentVariable("DOTNET_hostBuilder__reloadConfigOnChange", "false");
}
