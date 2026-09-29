using System.Runtime.InteropServices;

namespace BookmarkSync.Store;

/// <summary>
/// 平台相关的文件操作：原子替换与"瞬时错误"识别。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要重试</b>：Linux 上 <c>rename(2)</c> 在同一文件系统内是原子的，
/// 不会被别的进程"占用"。<b>Windows 不同</b>：MoveFileEx 会因为杀毒软件、索引器、
/// 编辑器等短暂持有文件句柄而返回 ACCESS_DENIED / SHARING_VIOLATION。
/// 这些占用通常只持续几毫秒，重试即可成功。
/// </para>
/// <para>
/// 这个 bug 在 100 次并发写的测试里稳定复现（每次约丢一个更新），但线上
/// 只在低频触发时才会被用户注意到 —— 表现是"偶尔同步失败，重试就好"。
/// </para>
/// <para>
/// <b>为什么手写而不用 File.Move 的重试</b>：.NET 的 <c>File.Move</c> 在
/// Windows 上遇到占用会直接抛异常，不重试。包一层比让每个调用点自己写
/// 循环可靠得多。
/// </para>
/// </remarks>
public static class FileOps
{
    // Windows 错误码（来自 winerror.h）。
    // 之所以手写常量：这些码不在 .NET 的公开异常类型里，
    // 只能从 HResult 的低位取出来。
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorStaleFileHandle = 116; // ESTALE，Linux 专用

    /// <summary>
    /// 判断错误是否属于"稍后重试就好"的那一类。
    /// </summary>
    /// <remarks>
    /// 权限类错误在两个平台都视为可重试 —— 注意这看起来很反直觉
    /// （权限错误不是应该直接失败吗？），但在 Windows 上"权限被拒"最常见的
    /// 原因就是别的进程短暂持有文件句柄，而不是真的 ACL 有问题。
    /// <para>
    /// 不相关的错误（如磁盘满、路径不存在）不重试：白等 5 次只会让
    /// 一次确定性失败变成 31ms 的确定性失败。
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
                // 路径不存在是确定性错误，重试没有意义
                return false;
        }

        for (Exception? cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is not IOException && cur is not UnauthorizedAccessException)
            {
                continue;
            }

            // HResult 的低 16 位是 Win32 错误码（0x8007xxxx 形式）
            int win32 = cur.HResult & 0xFFFF;
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
    /// 原子替换文件，遇到瞬时占用时指数退避重试。
    /// </summary>
    /// <param name="attempts">总尝试次数。1 表示不重试。</param>
    /// <returns>成功返回 true；确定性失败立即返回 false（不浪费时间）。</returns>
    public static bool RenameWithRetry(string oldPath, string newPath, int attempts, out Exception? lastError)
    {
        lastError = null;

        for (int i = 0; i < attempts; i++)
        {
            try
            {
                // overwrite: true 走 MoveFileEx(MOVEFILE_REPLACE_EXISTING)，
                // 语义与 Go 的 os.Rename 一致（目标存在时替换）。
                File.Move(oldPath, newPath, overwrite: true);
                return true;
            }
            catch (Exception ex) when (IsTransientFileError(ex))
            {
                lastError = ex;
                if (i < attempts - 1)
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(1 << i));
                }
            }
            catch (Exception ex)
            {
                // 确定性错误：立刻返回，不重试
                lastError = ex;
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// 尽力把目录项刷盘。它在 Linux 上有效，但在 Windows 上对目录做
    /// fsync 会直接失败 —— 那只是少一层保险，原子性仍由 rename 保证。
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
            // 见方法说明：这是"少一层保险"，不是错误。
        }
    }
}
