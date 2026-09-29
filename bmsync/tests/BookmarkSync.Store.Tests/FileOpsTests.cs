using BookmarkSync.Domain;

namespace BookmarkSync.Store.Tests;

/// <summary>
/// 原子写与瞬时错误识别。对应 原子写与瞬时错误识别的测试用例（同上）。
/// </summary>
public class FileOpsTests
{
    [Fact]
    public void 瞬时错误判定()
    {
        Assert.False(FileOps.IsTransientFileError(null), "null 不应被当作瞬时错误");

        // 权限类错误在两个平台都视为可重试
        Assert.True(FileOps.IsTransientFileError(new UnauthorizedAccessException()));

        // 不相关的错误不应重试
        Assert.False(FileOps.IsTransientFileError(new IOException("磁盘满了")));
        Assert.False(FileOps.IsTransientFileError(new InvalidOperationException("x")));

        // 路径不存在是确定性错误，重试没有意义
        Assert.False(FileOps.IsTransientFileError(new FileNotFoundException()));
        Assert.False(FileOps.IsTransientFileError(new DirectoryNotFoundException()));
    }

    [Theory]
    [InlineData(5)]    // ERROR_ACCESS_DENIED
    [InlineData(32)]   // ERROR_SHARING_VIOLATION
    [InlineData(33)]   // ERROR_LOCK_VIOLATION
    public void Win32错误码被视为瞬时(int win32)
    {
        // 构造一个带指定 Win32 码的 IOException：
        // HResult 的形式是 0x8007xxxx，.NET 读它时取低位得到原始 Win32 码。
        var hresult = unchecked((int)0x80070000 | win32);
        var ex = new IOException("simulated") { HResult = hresult };

        Assert.True(FileOps.IsTransientFileError(ex), $"Win32 码 {win32} 应被视为瞬时");
    }

    [Fact]
    public void 非瞬时Win32错误码不重试()
    {
        // 111 = ERROR_BROKEN_PIPE，与"文件被占用"无关
        var ex = new IOException("simulated") { HResult = unchecked((int)0x80070000 | 111) };
        Assert.False(FileOps.IsTransientFileError(ex));
    }

    /// <summary>
    /// 确定性错误必须立刻返回，不能白等。
    /// 如果它对不存在的源路径也重试 5 次再报错，问题会被人为放大 5 倍延迟。
    /// </summary>
    [Fact]
    public void 确定性错误立即失败()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bmsync-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string src = Path.Combine(dir, "src.json");
            string dst = Path.Combine(dir, "nested", "missing", "dst.json"); // 父目录不存在 → 确定性失败

            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool ok = FileOps.RenameWithRetry(src, dst, attempts: 5, out Exception? err);
            sw.Stop();

            Assert.False(ok);
            Assert.NotNull(err);

            // 5 次指数退避累计约 1+2+4+8+16 = 31ms。真实耗时应远小于此，
            // 说明确实立即返回了。
            Assert.True(
                sw.ElapsedMilliseconds < 20,
                $"确定性错误应立即返回，却耗时 {sw.ElapsedMilliseconds}ms —— 说明重试逻辑误伤了它");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 正常替换立即成功()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bmsync-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string src = Path.Combine(dir, "a.tmp");
            string dst = Path.Combine(dir, "a.json");
            File.WriteAllText(src, "hi");

            bool ok = FileOps.RenameWithRetry(src, dst, attempts: 5, out Exception? err);

            Assert.True(ok, $"正常 rename 应成功: {err?.Message}");
            Assert.True(File.Exists(dst));
            Assert.False(File.Exists(src), "源文件应已被移走");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 目标已存在时被替换()
    {
        // Go 的 os.Rename 语义：目标存在时替换。
        // 这一点必须一致 —— state.json 的原子写正是靠它。
        string dir = Path.Combine(Path.GetTempPath(), "bmsync-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string src = Path.Combine(dir, "new.json");
            string dst = Path.Combine(dir, "old.json");
            File.WriteAllText(src, "新内容");
            File.WriteAllText(dst, "旧内容");

            Assert.True(FileOps.RenameWithRetry(src, dst, attempts: 5, out _));
            Assert.Equal("新内容", File.ReadAllText(dst));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TrySyncDir不抛异常()
    {
        // Windows 上对目录做 fsync 本来就会失败；这里的契约是"失败也不许炸"，
        // 因为它只是"少一层保险"，原子性仍由 rename 保证。
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
