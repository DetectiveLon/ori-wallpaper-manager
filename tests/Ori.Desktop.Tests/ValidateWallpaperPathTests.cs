using Ori.Desktop.Services;

namespace Ori.Desktop.Tests;

/// <summary>
/// The desktop wallpaper validator only checks local paths and the Windows image
/// allowlist. No wallpaper is applied and no COM object is created here.
/// </summary>
public sealed class ValidateWallpaperPathTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "ori-wallpaper-path-tests", Guid.NewGuid().ToString("N"));

    public ValidateWallpaperPathTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    private string MakeFile(string fileName)
    {
        var path = Path.Combine(_directory, fileName);
        File.WriteAllText(path, "not-a-real-image");
        return path;
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".JPG")]
    [InlineData(".jpeg")]
    [InlineData(".png")]
    [InlineData(".bmp")]
    [InlineData(".dib")]
    [InlineData(".tif")]
    [InlineData(".tiff")]
    public void Accepts_supported_image_extensions(string extension)
    {
        var path = MakeFile("wallpaper" + extension);

        Assert.Equal(Path.GetFullPath(path), WindowsDesktopWallpaper.ValidateWallpaperPath(path));
    }

    [Theory]
    [InlineData(".gif")]
    [InlineData(".webp")]
    [InlineData(".txt")]
    [InlineData("")]
    public void Rejects_unsupported_or_missing_extensions(string extension)
    {
        var path = MakeFile("wallpaper" + extension);

        Assert.Throws<InvalidDataException>(() => { _ = WindowsDesktopWallpaper.ValidateWallpaperPath(path); });
    }

    [Fact]
    public void Rejects_a_missing_file_even_when_the_extension_is_supported()
    {
        var path = Path.Combine(_directory, "missing.jpg");

        Assert.Throws<FileNotFoundException>(() => { _ = WindowsDesktopWallpaper.ValidateWallpaperPath(path); });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_empty_input(string? value)
    {
        Assert.ThrowsAny<ArgumentException>(() => WindowsDesktopWallpaper.ValidateWallpaperPath(value!));
    }
}
