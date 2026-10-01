namespace BookmarkSync.Domain;

/// <summary>
/// Hard limits for the domain layer.
///
/// These numbers are mirrored in docs/architecture.md §6, so they live in one
/// place instead of scattering as magic numbers. HTTP-layer limits (body
/// size, rate limiting) live in BookmarkSync.Server.
/// </summary>
public static class Limits
{
    /// <summary>Maximum items allowed in a single state.</summary>
    public const int MaxItems = 50_000;

    /// <summary>
    /// Maximum bookmark-tree depth (excluding the root itself).
    /// <para>
    /// Needed because folder keys depend on the parent key, so a pathological
    /// chain could be arbitrarily deep. The cap bounds both bad input and the
    /// recursion budget of the apply phase.
    /// </para>
    /// </summary>
    public const int MaxDepth = 32;

    /// <summary>Hex characters per item key (first 16 bytes of SHA-256).</summary>
    /// <remarks>
    /// 128 bits: negligible collision probability at personal-bookmark scale.
    /// </remarks>
    public const int KeyLen = 32;

    /// <summary>URL field length cap, guards against pathological input.</summary>
    /// <remarks>
    /// Real bookmark titles routinely exceed 1 KB (page titles stored verbatim;
    /// 2762 bytes observed on a real machine), so 1024 would reject valid data.
    /// 8 KB is still far below the 16 MB request cap; truly pathological input
    /// is caught by the HTTP-layer MaxBodyBytes.
    /// </remarks>
    public const int MaxUrlLen = 4096;

    /// <summary>Title field length cap.</summary>
    public const int MaxTitleLen = 8192;
}
