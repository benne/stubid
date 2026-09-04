namespace StubId.Server;

/// <summary>
/// A file that is created once and never rewritten, whatever else starts at the same time.
/// </summary>
/// <remarks>
/// <para>
/// The keys and the TLS certificate are both written on first use and loaded ever after, because
/// clients cache metadata for hours and a server that generates fresh keys on every start breaks
/// every integrating application at once. That only works if concurrent starts sharing a directory
/// agree on what was written.
/// </para>
/// <para>
/// Writing through a temporary file and moving it into place is not enough on its own, which is
/// what this replaces. <c>File.Move(overwrite: false)</c> is not atomic on Unix: it checks whether
/// the destination exists and then calls <c>rename</c>, which silently replaces it. Two starts can
/// both find the destination missing, and the second's <c>rename</c> then overwrites a file the
/// first has already read - so the first keeps a key nothing else has. Measured directly rather
/// than inferred: two threads racing one destination through <c>File.Move</c> both reported
/// success in 1476 of 20000 attempts, and removing the lock below reproduces the CI failure this
/// came from - fifteen starts agreeing and one not - within a few seconds.
/// </para>
/// <para>
/// So creating and reading happen under an exclusive handle on a lock file beside it. The lock is
/// advisory - on Unix it is <c>flock</c> - which is enough here because the only things writing
/// these directories are StubID instances. It would not hold against a process that ignored it, or
/// on a filesystem that does not implement the lock, and both are outside what sharing a key
/// volume between containers means.
/// </para>
/// <para>
/// The lock file is left behind on purpose. Deleting it would be its own race: opening a path
/// after another process has unlinked it produces a new inode, and two starts would then hold
/// what they each believe is the lock on two different files.
/// </para>
/// </remarks>
internal static class WriteOnceFile
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The contents of <paramref name="path" />, creating it from <paramref name="create" /> if
    /// nobody has yet. Every caller gets the bytes of whichever start won.
    /// </summary>
    public static byte[] ReadOrCreate(string path, Func<byte[]> create)
    {
        ArgumentNullException.ThrowIfNull(create);

        // Held across the read as well as the write. Only the writer strictly needs it, but a
        // reader that has to reason about which of two existence checks is load-bearing is a
        // reader who will get it wrong later, and the cost here is one open on a start.
        using var gate = Gate(path + ".lock");

        if (!File.Exists(path))
        {
            // Still through a temporary file: the gate keeps other starts out, but a reader that
            // is not going through this at all must never see a half-written certificate.
            var pending = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(pending, create());
            File.Move(pending, path, overwrite: false);
        }

        return File.ReadAllBytes(path);
    }

    private static FileStream Gate(string path)
    {
        var deadline = Environment.TickCount64 + (long)Patience.TotalMilliseconds;

        while (true)
        {
            try
            {
                return new FileStream(
                    path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (Environment.TickCount64 < deadline)
            {
                // Generating a key takes single-digit milliseconds, so the wait is short and
                // yielding beats sleeping through most of it. The sleep is the floor for the
                // case this is not true - a cold machine, or a great many starts at once.
                if (!Thread.Yield())
                {
                    Thread.Sleep(1);
                }
            }
        }
    }
}
