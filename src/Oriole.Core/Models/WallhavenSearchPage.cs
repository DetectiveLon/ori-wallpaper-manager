namespace Oriole.Core.Models;

public sealed record WallhavenWallpaper(
    string Id,
    Uri PageUrl,
    Uri ImageUrl,
    Uri ThumbnailUrl,
    string Category,
    string Purity,
    int Width,
    int Height,
    string Ratio,
    long FileSize,
    string FileType,
    IReadOnlyList<string> Colors);

public sealed record WallhavenSearchPage(
    IReadOnlyList<WallhavenWallpaper> Items,
    int CurrentPage,
    int LastPage,
    string? Seed);
