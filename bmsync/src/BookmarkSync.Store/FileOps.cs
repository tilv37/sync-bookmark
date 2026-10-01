using System.Runtime.InteropServices;

namespace BookmarkSync.Store;

/// <summary>
/// Platform-specific file operations: atomic replace and transient-error detection.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why retry</b>: on Linux, <c>rename(2)</c> within one filesystem is atomic
/// and never "busy". <b>Windows differs</b>: MoveFileEx can return
/// ACCESS_DENIED / SHARING_VIOLATION because antivirus, indexers, or editors
/// briefly hold the file. Such holds last milliseconds; retry succeeds.
/// </para>
/// <para>
/// The bug reproduced stably in a 100-way concurrent-write test (about one
/// lost update per run) but appears online only as "occasional sync failure,
/// retry fixes it".
/// </para>
/// <para>
/// <b>Why hand-rolled instead of File.Move retry</b>: .NET File.Move throws on
/// Windows contention without retrying. One wrapper beats a loop at every
/// call site.
/// </para>
/// </remarks>
public static class FileOps
{
    // Windows error codes (from winerror.h). Hard-coded because they are not
    // exposed as .NET exception types; only visible in the low bits of HResult.
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorStaleFileHandle = 116; // ESTALE, Linux only

    /// <summary>
    /// Returns true for errors worth retrying shortly.
    /// </summary>
    /// <remarks>
    /// Treating permission errors as retryable looks counter-intuitive, but on
    /// Windows the most common "access denied" cause is a transient hold by
    /// another process, not a real ACL problem.
    /// <para>
    /// Unrelated errors (disk full, missing path) are not retried: waiting
    /// through 5 attempts would turn a deterministic failure into a 31 ms
    /// deterministic failure.
    /// </para>
    /// </remarks>
    public static bool IsTransientFileError(Exception? ex)
    {
        switch (ex)
        {
            case null:
                return false;

            case UnauthorizedAccessException:
                return true;

            case DirectoryNotFoundException:
            case FileNotFoundException:
                // Deterministic: retrying a missing path is pointless.
                return false;
        }

        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is not IOException && current is not UnauthorizedAccessException)
            {
                continue;
            }

            // Low 16 bits of HResult hold the Win32 code (0x8007xxxx form).
            int win32 = current.HResult & 0xFFFF;
            if (win32 is ErrorAccessDenied or ErrorSharingViolation or ErrorLockViolation)
            {
                return true;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && win32 == ErrorStaleFileHandle)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Atomically replaces a file with exponential-backoff retry on transient holds.
    /// </summary>
    /// <param name="sourcePath">Staging file to move into place.</param>
    /// <param name="destPath">Final path (replaced atomically).</param>
    /// <param name="attempts">Total attempts. 1 means no retry.</param>
    /// <returns>True on success; deterministic failures return false immediately.</returns>
    public static bool RenameWithRetry(string sourcePath, string destPath, int attempts, out Exception? lastError)
    {
        lastError = null;

        for (int attemptIndex = 0; attemptIndex < attempts; attemptIndex++)
        {
            try
            {
                // overwrite: true maps to MoveFileEx(MOVEFILE_REPLACE_EXISTING),
                // same semantics as Go os.Rename (replace when target exists).
                File.Move(sourcePath, destPath, overwrite: true);
                return true;
            }
            catch (Exception ex) when (IsTransientFileError(ex))
            {
                lastError = ex;
                if (attemptIndex < attempts - 1)
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(1 << attemptIndex));
                }
            }
            catch (Exception ex)
            {
                // Deterministic error: return immediately, no retry.
                lastError = ex;
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Best-effort directory fsync. Effective on Linux; on Windows, fsync on a
    /// directory fails — that only drops one durability layer, atomicity still
    /// comes from rename.
    /// </summary>
    public static void TrySyncDir(string dir)
    {
        try
        {
            using FileStream fs = new(dir, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Flush(flushToDisk: true);
        }
        catch
        {
            // Best effort by design, not an error.
        }
    }
}
