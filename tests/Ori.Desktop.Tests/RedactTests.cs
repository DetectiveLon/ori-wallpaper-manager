namespace Ori.Desktop.Tests;

/// <summary>
/// Diagnostics logs must not leak user folder names, file shares or URLs. The
/// redactor keeps only the leaf file name so logs still identify the image.
/// </summary>
public sealed class RedactTests
{
    [Theory]
    [InlineData(@"C:\Users\Lon\Pictures\secret.jpg", "secret.jpg")]
    [InlineData(@"C:\secret.txt", "secret.txt")]
    [InlineData(@"D:\wallhaven\cache\moon.png", "moon.png")]
    public void Redact_keeps_only_leaf_name_for_drive_paths(string value, string expected)
    {
        Assert.Equal(expected, App.Redact(value));
    }

    [Theory]
    [InlineData(@"\\nas01\wallpapers\private\moon.png", "moon.png")]
    [InlineData(@"\\nas01\share\secret.jpg", "secret.jpg")]
    public void Redact_keeps_only_leaf_name_for_unc_paths(string value, string expected)
    {
        Assert.Equal(expected, App.Redact(value));
    }

    [Theory]
    [InlineData("https://wallhaven.cc/w/abc123?key=topsecret")]
    [InlineData("http://example.com/private/path")]
    [InlineData("file:///C:/Users/Lon/secret.jpg")]
    public void Redact_replaces_urls_with_a_placeholder(string value)
    {
        Assert.Equal("[url]", App.Redact(value));
    }

    [Fact]
    public void Redact_handles_mixed_path_and_url_text()
    {
        var value = @"Failed C:\Users\Lon\Pictures\secret.jpg; url https://wallhaven.cc/w/abc123";

        Assert.Equal("Failed secret.jpg; url [url]", App.Redact(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Redact_returns_empty_for_empty_input(string? value)
    {
        Assert.Equal(string.Empty, App.Redact(value!));
    }

    [Fact]
    public void Redact_leaves_ordinary_text_unchanged()
    {
        const string value = "Rotation failed on display 1; retrying.";

        Assert.Equal(value, App.Redact(value));
    }
}
