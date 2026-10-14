using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Vitrine.Photos;

/// <summary> Result of fetching a page: its HTML (null on failure) and the URL after redirects. </summary>
public sealed record PageResult(string? Html, Uri FinalUrl, string? Error);

/// <summary> Result of fetching an image: bytes (null on failure), the content type and the error. </summary>
public sealed record ImageResult(byte[]? Bytes, string? ContentType, string? Error);

/// <summary> The two network calls the photo service makes, behind an interface so tests use a fake. </summary>
public interface IPhotoHttp : IDisposable
{
    Task<PageResult> GetPageAsync(Uri url, CancellationToken ct);

    Task<ImageResult> GetImageAsync(Uri url, Uri referer, CancellationToken ct);
}

/// <summary> Real HttpClient implementation with the limits from the design: 15 s timeout, 2 MB of HTML, 10 MB of image, redirects followed. </summary>
public sealed class HttpPhotoHttp : IPhotoHttp
{
    /// <summary> Identifies Lamina to mod sites, as polite crawlers do. </summary>
    public const string UserAgent = "Lamina-Vitrine/0.1 (+https://github.com/AHAKuo/FFXIVLamina)";

    public const int MaxHtmlBytes  = 2 * 1024 * 1024;
    public const int MaxImageBytes = 10 * 1024 * 1024;

    private readonly HttpClient _client;

    public HttpPhotoHttp()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect        = true,
            MaxAutomaticRedirections = 8,
            AutomaticDecompression   = System.Net.DecompressionMethods.All,
        };
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,image/*;q=0.9,*/*;q=0.5");
    }

    /// <inheritdoc/>
    public async Task<PageResult> GetPageAsync(Uri url, CancellationToken ct)
    {
        try
        {
            using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var final = response.RequestMessage?.RequestUri ?? url;
            if (!response.IsSuccessStatusCode)
                return new PageResult(null, final, $"HTTP {(int)response.StatusCode}");

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return new PageResult(null, final, "page is an image"); // The caller can use the URL directly.

            var bytes = await ReadLimitedAsync(response, MaxHtmlBytes, ct).ConfigureAwait(false);
            var charset = response.Content.Headers.ContentType?.CharSet;
            var encoding = System.Text.Encoding.UTF8;
            if (!string.IsNullOrEmpty(charset))
            {
                try { encoding = System.Text.Encoding.GetEncoding(charset.Trim('"')); }
                catch (ArgumentException) { /* unknown charset: UTF-8 */ }
            }

            return new PageResult(encoding.GetString(bytes), final, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new PageResult(null, url, ex is TaskCanceledException ? "timeout" : ex.Message);
        }
    }

    /// <inheritdoc/>
    public async Task<ImageResult> GetImageAsync(Uri url, Uri referer, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Referrer = referer;
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new ImageResult(null, null, $"HTTP {(int)response.StatusCode}");

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return new ImageResult(null, mediaType, $"not an image ({mediaType})");

            if (response.Content.Headers.ContentLength is { } length && length > MaxImageBytes)
                return new ImageResult(null, mediaType, $"image too large ({length} bytes)");

            var bytes = await ReadLimitedAsync(response, MaxImageBytes, ct).ConfigureAwait(false);
            return new ImageResult(bytes, mediaType, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new ImageResult(null, null, ex is TaskCanceledException ? "timeout" : ex.Message);
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, int limit, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit)
                throw new InvalidOperationException($"response exceeds {limit} bytes");
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    public void Dispose()
        => _client.Dispose();
}
