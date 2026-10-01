using BookmarkSync.Domain;

namespace BookmarkSync.Store.Tests;

/// <summary>
/// Atomic writes and transient-error classification, mirroring the atomic-write test cases.
/// </summary>
public class FileOpsTests
{
    [Fact]
    public void TransientErrorClassification()
    {
        Assert.False(FileOps.IsTransientFileError(null), "null must not count as transient");

        // Permission errors are retryable on both platforms
        Assert.True(FileOps.IsTransientFileError(new UnauthorizedAccessException()));

        // Unrelated errors must not retry
        Assert.False(FileOps.IsTransientFileError(new IOException("disk full")));
        Assert.False(FileOps.IsTransientFileError(new InvalidOperationException("x")));

        // Missing paths are deterministic errors; retrying is pointless
        Assert.False(FileOps.IsTransientFileError(new FileNotFoundException()));
        Assert.False(FileOps.IsTransientFileError(new DirectoryNotFoundException()));
    }

    [Theory]
    [InlineData(5)]    // ERROR_ACCESS_DENIED
    [InlineData(32)]   // ERROR_SHARING_VIOLATION
    [InlineData(33)]   // ERROR_LOCK_VIOLATION
    public void Win32ErrorCodesAreTransient(int win32)
    {
        // Build an IOException with the given Win32 code:
        // HResult takes the 0x8007xxxx form; .NET reads the low bits as the Win32 code.
        var hresult = unchecked((int)0x80070000 | win32);
        var ex = new IOException("simulated") { HResult = hresult };

        Assert.True(FileOps.IsTransientFileError(ex), $"Win32 code {win32} should count as transient");
    }

    [Fact]
    public void NonTransientWin32CodesAreNotRetried()
    {
        // 111 = ERROR_BROKEN_PIPE, unrelated to "file in use"
        var ex = new IOException("simulated") { HResult = unchecked((int)0x80070000 | 111) };
        Assert.False(FileOps.IsTransientFileError(ex));
    }

    /// <summary>
    /// Deterministic errors must return immediately, not wait pointlessly.
    /// Retrying a missing source path 5 times before failing would multiply the delay 5x.
    /// </summary>
    [Fact]
    public void DeterministicErrorsFailImmediately()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bmsync-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string src = Path.Combine(dir, "src.json");
            string dst = Path.Combine(dir, "nested", "missing", "dst.json"); // Missing parent dir → deterministic failure

            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool ok = FileOps.RenameWithRetry(src, dst, attempts: 5, out Exception? err);
            sw.Stop();

            Assert.False(ok);
            Assert.NotNull(err);

            // 5 exponential backoffs total ~1+2+4+8+16 = 31ms. Real elapsed must be far below,
            // proving an immediate return.
            Assert.True(
                sw.ElapsedMilliseconds < 20,
                $"deterministic errors must return at once, but took {sw.ElapsedMilliseconds}ms — retry logic caught it wrongly");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void NormalReplaceSucceedsImmediately()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bmsync-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string src = Path.Combine(dir, "a.tmp");
            string dst = Path.Combine(dir, "a.json");
            File.WriteAllText(src, "hi");

            bool ok = FileOps.RenameWithRetry(src, dst, attempts: 5, out Exception? err);

            Assert.True(ok, $"plain rename should succeed: {err?.Message}");
            Assert.True(File.Exists(dst));
            Assert.False(File.Exists(src), "source file should be gone");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ExistingTargetIsReplaced()
    {
        // Go os.Rename semantics: an existing target is replaced.
        // This must match — state.json atomic writes rely on it.
        string dir = Path.Combine(Path.GetTempPath(), "bmsync-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string src = Path.Combine(dir, "new.json");
            string dst = Path.Combine(dir, "old.json");
            File.WriteAllText(src, "new content");
            File.WriteAllText(dst, "old content");

            Assert.True(FileOps.RenameWithRetry(src, dst, attempts: 5, out _));
            Assert.Equal("new content", File.ReadAllText(dst));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TrySyncDirDoesNotThrow()
    {
        // fsync on a directory normally fails on Windows; the contract here is "never throw on failure",
        // since it is only one layer of insurance while rename still guarantees atomicity.
        string dir = Path.Combine(Path.GetTempPath(), "bmsync-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            FileOps.TrySyncDir(dir);
            FileOps.TrySyncDir(Path.Combine(dir, "does-not-exist"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
