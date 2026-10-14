using System.Threading;
using System.Threading.Tasks;
using Vitrine;
using Vitrine.Photos;
using Xunit;

namespace Lamina.Tests.Vitrine;

/// <summary> Scripted network for the photo service. </summary>
internal sealed class FakePhotoHttp : IPhotoHttp
{
    public Dictionary<string, string> Pages { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, (byte[] Bytes, string ContentType)> Images { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> PageRequests { get; } = new();
    public List<string> ImageRequests { get; } = new();

    public Task<PageResult> GetPageAsync(Uri url, CancellationToken ct)
    {
        lock (PageRequests)
            PageRequests.Add(url.ToString());
        return Task.FromResult(Pages.TryGetValue(url.ToString(), out var html)
            ? new PageResult(html, url, null)
            : new PageResult(null, url, "HTTP 404"));
    }

    public Task<ImageResult> GetImageAsync(Uri url, Uri referer, CancellationToken ct)
    {
        lock (ImageRequests)
            ImageRequests.Add(url.ToString());
        return Task.FromResult(Images.TryGetValue(url.ToString(), out var img)
            ? new ImageResult(img.Bytes, img.ContentType, null)
            : new ImageResult(null, null, "HTTP 404"));
    }

    public void Dispose()
    { }
}

public class PhotoPageParserTests
{
    private static readonly Uri Page = new("https://example.com/mods/123/page");

    [Theory]
    [InlineData("<meta property=\"og:image\" content=\"https://cdn.example.com/a.png\">", "https://cdn.example.com/a.png")]
    [InlineData("<meta content='https://cdn.example.com/b.jpg' property='og:image' />", "https://cdn.example.com/b.jpg")]
    [InlineData("<META PROPERTY=og:image CONTENT=https://cdn.example.com/c.jpg>", "https://cdn.example.com/c.jpg")]
    [InlineData("<meta name=\"twitter:image\" content=\"/img/t.png\">", "https://example.com/img/t.png")]
    [InlineData("<meta name=\"twitter:image\" content=\"relative/t.png\">", "https://example.com/mods/123/relative/t.png")]
    [InlineData("<link rel=\"image_src\" href=\"//cdn.example.com/l.png\">", "https://cdn.example.com/l.png")]
    [InlineData("<meta property=\"og:image\" content=\"https://cdn.example.com/x.png?a=1&amp;b=2\">", "https://cdn.example.com/x.png?a=1&b=2")]
    public void ExtractsImageFromMetaTags(string html, string expected)
    {
        var uri = PhotoPageParser.ExtractImageUrl($"<html><head>{html}</head></html>", Page);
        Assert.NotNull(uri);
        Assert.Equal(expected, uri!.ToString());
    }

    [Fact]
    public void PrefersSecureUrlOverOgImageOverTwitter()
    {
        const string html = "<meta name=\"twitter:image\" content=\"https://t/1.png\">"
                          + "<meta property=\"og:image\" content=\"https://og/2.png\">"
                          + "<meta property=\"og:image:secure_url\" content=\"https://secure/3.png\">";
        Assert.Equal("https://secure/3.png", PhotoPageParser.ExtractImageUrl(html, Page)!.ToString());

        const string noSecure = "<meta name=\"twitter:image\" content=\"https://t/1.png\"><meta property=\"og:image\" content=\"https://og/2.png\">";
        Assert.Equal("https://og/2.png", PhotoPageParser.ExtractImageUrl(noSecure, Page)!.ToString());
    }

    [Fact]
    public void IgnoresNonHttpAndMissing()
    {
        Assert.Null(PhotoPageParser.ExtractImageUrl("<html><body>no meta</body></html>", Page));
        Assert.Null(PhotoPageParser.ExtractImageUrl("<meta property=\"og:image\" content=\"data:image/png;base64,AAAA\">", Page));
        Assert.Null(PhotoPageParser.ExtractImageUrl("<meta property=\"og:image\" content=\"\">", Page));
        Assert.Null(PhotoPageParser.ExtractImageUrl(null, Page));
    }

    [Fact]
    public void ParseAttributesHandlesQuotesAndEntities()
    {
        var attrs = PhotoPageParser.ParseAttributes("property='og:title' content=\"Tom &amp; Jerry\" data-x=plain");
        Assert.Equal("og:title", attrs["property"]);
        Assert.Equal("Tom & Jerry", attrs["content"]);
        Assert.Equal("plain", attrs["data-x"]);
    }
}

public class PhotoIndexTests
{
    [Fact]
    public void CacheStemIsDeterministicAndCaseInsensitive()
    {
        var a = PhotoIndex.CacheStem("Moonlit Sundress");
        var b = PhotoIndex.CacheStem("moonlit sundress");
        Assert.Equal(a, b);
        Assert.Equal(32, a.Length);
        Assert.NotEqual(a, PhotoIndex.CacheStem("Other"));
    }

    [Theory]
    [InlineData("image/png", "https://x/y", ".png")]
    [InlineData("image/jpeg", "https://x/y", ".jpg")]
    [InlineData("image/webp", "https://x/y", ".webp")]
    [InlineData(null, "https://x/y.PNG", ".png")]
    [InlineData(null, "https://x/y.jpeg?w=1", ".jpg")]
    [InlineData("application/octet-stream", "https://x/y", ".jpg")]
    public void ExtensionFromContentTypeThenUrl(string? contentType, string url, string expected)
        => Assert.Equal(expected, PhotoIndex.ExtensionFor(contentType, new Uri(url)));

    [Fact]
    public void RetryWindowAndWebsiteChange()
    {
        var now = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        var entry = new PhotoIndexEntry { Source = PhotoSource.None, SourceUrl = "https://a", NextRetryUtc = now + TimeSpan.FromDays(7) };
        Assert.False(PhotoIndex.MayRetry(entry, "https://a", now));
        Assert.False(PhotoIndex.MayRetry(entry, "https://a", now + TimeSpan.FromDays(6)));
        Assert.True(PhotoIndex.MayRetry(entry, "https://a", now + TimeSpan.FromDays(7)));
        Assert.True(PhotoIndex.MayRetry(entry, "https://b", now));
        Assert.False(PhotoIndex.MayRetry(new PhotoIndexEntry { Source = PhotoSource.Fetched, Path = "x" }, "https://a", now));
    }

    [Fact]
    public void RoundTripsThroughFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lamina-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path  = Path.Combine(dir, "index.json");
            var index = new PhotoIndex(path, _ => { });
            index.Load();
            index.Set("Mod A", new PhotoIndexEntry { Source = PhotoSource.Fetched, Path = "p", SourceUrl = "s", ImageUrl = "i", Attempts = 2, LastError = "" });
            index.SaveIfDirty();
            Assert.False(index.IsDirty);

            var again = new PhotoIndex(path, _ => { });
            again.Load();
            var entry = again.Get("mod a");
            Assert.NotNull(entry);
            Assert.Equal(PhotoSource.Fetched, entry!.Source);
            Assert.Equal("i", entry.ImageUrl);
            Assert.Equal(2, entry.Attempts);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public class LocalPhotoFinderTests
{
    [Fact]
    public void FindsInPriorityOrder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lamina-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "images"));
        try
        {
            Assert.Null(LocalPhotoFinder.Find(dir, null));

            File.WriteAllBytes(Path.Combine(dir, "images", "zeta.jpg"), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(dir, "images", "alpha.png"), new byte[] { 1 });
            Assert.Equal(Path.Combine(dir, "images", "alpha.png"), LocalPhotoFinder.Find(dir, null));

            File.WriteAllBytes(Path.Combine(dir, "texture.png"), new byte[] { 1 });
            Assert.Equal(Path.Combine(dir, "images", "alpha.png"), LocalPhotoFinder.Find(dir, null)); // random root png is not a preview

            File.WriteAllBytes(Path.Combine(dir, "Cover.JPG"), new byte[] { 1 });
            Assert.Equal("cover.jpg", Path.GetFileName(LocalPhotoFinder.Find(dir, null)!).ToLowerInvariant());

            File.WriteAllBytes(Path.Combine(dir, "preview.png"), new byte[] { 1 });
            Assert.Equal(Path.Combine(dir, "preview.png"), LocalPhotoFinder.Find(dir, null));

            var meta = Path.Combine(dir, "meta-image.png");
            File.WriteAllBytes(meta, new byte[] { 1 });
            Assert.Equal(meta, LocalPhotoFinder.Find(dir, meta));

            Assert.Equal(Path.Combine(dir, "preview.png"), LocalPhotoFinder.Find(dir, Path.Combine(dir, "missing.png")));
            File.WriteAllBytes(Path.Combine(dir, "meta.webp"), new byte[] { 1 });
            Assert.Equal(Path.Combine(dir, "preview.png"), LocalPhotoFinder.Find(dir, Path.Combine(dir, "meta.webp")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public class ModPhotoServiceTests
{
    private static (ModPhotoService Service, FakePhotoHttp Http, string Root, string Photos) Create(bool fetch = true)
    {
        var root   = Path.Combine(Path.GetTempPath(), "lamina-tests", Guid.NewGuid().ToString("N"));
        var mods   = Path.Combine(root, "mods");
        var photos = Path.Combine(root, "photos");
        Directory.CreateDirectory(mods);
        var http = new FakePhotoHttp();
        var service = new ModPhotoService(photos, http, () => mods, () => fetch, _ => { }, _ => { });
        return (service, http, mods, photos);
    }

    private static async Task WaitForResultAsync(ModPhotoService service, ModInformation mod)
    {
        for (var i = 0; i < 200; i++)
        {
            service.Tick();
            if (service.GetPhoto(mod, request: false).Status != PhotoStatus.Pending)
                return;
            await Task.Delay(25);
        }

        throw new TimeoutException("photo never resolved");
    }

    [Fact]
    public async Task FetchesOgImageIntoCacheAndUpdatesMod()
    {
        var (service, http, root, photos) = Create();
        try
        {
            var mod = new ModInformation { ModDirectory = "Moonlit Sundress", Website = "https://example.com/mod/1" };
            http.Pages["https://example.com/mod/1"]      = "<html><meta property=\"og:image\" content=\"https://cdn.example.com/m.png\"></html>";
            http.Images["https://cdn.example.com/m.png"] = (new byte[] { 0x89, 0x50, 0x4E, 0x47 }, "image/png");

            Assert.Equal(PhotoStatus.Pending, service.GetPhoto(mod).Status);
            await WaitForResultAsync(service, mod);

            var state = service.GetPhoto(mod);
            Assert.Equal(PhotoStatus.Ready, state.Status);
            Assert.Equal(Path.Combine(photos, PhotoIndex.CacheStem("Moonlit Sundress") + ".png"), state.Path);
            Assert.True(File.Exists(state.Path));
            Assert.Equal(state.Path, mod.ImagePath);
            Assert.Single(http.PageRequests);
            Assert.Equal(1, service.Counters.Cached);

            // A second lookup is served from the index, not the network.
            Assert.Equal(PhotoStatus.Ready, service.GetPhoto(mod).Status);
            Assert.Single(http.PageRequests);
        }
        finally
        {
            service.Dispose();
            Directory.Delete(Path.GetDirectoryName(root)!, true);
        }
    }

    [Fact]
    public async Task FailureIsRecordedAndNotRetriedWithinWindow()
    {
        var (service, http, root, _) = Create();
        try
        {
            var mod = new ModInformation { ModDirectory = "NoPhoto", Website = "https://example.com/none" };
            http.Pages["https://example.com/none"] = "<html><title>nothing</title></html>";

            Assert.Equal(PhotoStatus.Pending, service.GetPhoto(mod).Status);
            await WaitForResultAsync(service, mod);

            Assert.Equal(PhotoStatus.Missing, service.GetPhoto(mod).Status);
            Assert.Equal(PhotoStatus.Missing, service.GetPhoto(mod).Status);
            Assert.Single(http.PageRequests);
            Assert.Equal(1, service.Counters.Failed);

            // Refresh forces another look.
            service.RequestRefresh(mod);
            await WaitForResultAsync(service, mod);
            Assert.Equal(2, http.PageRequests.Count);
        }
        finally
        {
            service.Dispose();
            Directory.Delete(Path.GetDirectoryName(root)!, true);
        }
    }

    [Fact]
    public void LocalPreviewWinsWithoutNetwork()
    {
        var (service, http, root, _) = Create();
        try
        {
            var folder = Path.Combine(root, "LocalMod");
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "preview.jpg"), new byte[] { 1 });
            var mod = new ModInformation { ModDirectory = "LocalMod", Website = "https://example.com/never" };

            var state = service.GetPhoto(mod);
            Assert.Equal(PhotoStatus.Ready, state.Status);
            Assert.Equal(Path.Combine(folder, "preview.jpg"), state.Path);
            Assert.Empty(http.PageRequests);
            Assert.Equal(1, service.Counters.Local);
        }
        finally
        {
            service.Dispose();
            Directory.Delete(Path.GetDirectoryName(root)!, true);
        }
    }

    [Fact]
    public void NoWebsiteOrFetchDisabledIsMissingWithoutRepeatedWork()
    {
        var (service, http, root, _) = Create(fetch: false);
        try
        {
            var mod = new ModInformation { ModDirectory = "Offline", Website = "https://example.com/x" };
            Assert.Equal(PhotoStatus.Missing, service.GetPhoto(mod).Status);
            Assert.Equal(PhotoStatus.Missing, service.GetPhoto(mod).Status);
            Assert.Empty(http.PageRequests);
            Assert.Equal(0, service.Counters.Pending);
        }
        finally
        {
            service.Dispose();
            Directory.Delete(Path.GetDirectoryName(root)!, true);
        }
    }

    [Fact]
    public async Task ClearCacheDeletesDownloads()
    {
        var (service, http, root, photos) = Create();
        try
        {
            var mod = new ModInformation { ModDirectory = "Cached", Website = "https://example.com/c" };
            http.Pages["https://example.com/c"]          = "<meta property=\"og:image\" content=\"https://cdn.example.com/c.jpg\">";
            http.Images["https://cdn.example.com/c.jpg"] = (new byte[] { 1, 2, 3 }, "image/jpeg");
            service.GetPhoto(mod);
            await WaitForResultAsync(service, mod);
            var path = service.GetPhoto(mod).Path!;
            Assert.True(File.Exists(path));

            service.ClearCache();
            Assert.False(File.Exists(path));
            Assert.Equal(string.Empty, mod.ImagePath);
            Assert.Equal(0, service.Counters.Cached);
            Assert.True(File.Exists(Path.Combine(photos, "index.json")));
        }
        finally
        {
            service.Dispose();
            Directory.Delete(Path.GetDirectoryName(root)!, true);
        }
    }
}
