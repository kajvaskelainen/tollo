using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Kanava.Services;

/// <summary>
/// Logojen ja julisteiden asynkroninen lataus: HTTP-haku taustalla, rajoitettu rinnakkaisuus,
/// pienennetty dekoodaus ja muistivälimuisti. UI ei koskaan odota kuvia.
/// </summary>
public static class ImageCache
{
    private static readonly ConcurrentDictionary<string, Task<ImageSource?>> Cache = new();
    private static readonly SemaphoreSlim Gate = new(8);
    private const int MaxEntries = 3000;

    public static ImageSource? TryGet(string url, int decodeWidth)
    {
        var key = url + "|" + decodeWidth;
        return Cache.TryGetValue(key, out var t) && t.IsCompletedSuccessfully ? t.Result : null;
    }

    public static Task<ImageSource?> GetAsync(string? url, int decodeWidth)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
            return Task.FromResult<ImageSource?>(null);
        if (Cache.Count > MaxEntries) Cache.Clear();
        return Cache.GetOrAdd(url + "|" + decodeWidth, _ => LoadAsync(url, decodeWidth));
    }

    private static async Task<ImageSource?> LoadAsync(string url, int decodeWidth)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var bytes = await XtreamClient.Http.GetByteArrayAsync(url, cts.Token).ConfigureAwait(false);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = decodeWidth;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
        finally { Gate.Release(); }
    }

    // ----- Liitetty ominaisuus: <Image svc:ImageCache.Url="{Binding Logo}" /> -----

    public static readonly DependencyProperty UrlProperty = DependencyProperty.RegisterAttached(
        "Url", typeof(string), typeof(ImageCache), new PropertyMetadata(null, OnUrlChanged));

    public static readonly DependencyProperty DecodeWidthProperty = DependencyProperty.RegisterAttached(
        "DecodeWidth", typeof(int), typeof(ImageCache), new PropertyMetadata(96));

    public static string? GetUrl(DependencyObject d) => (string?)d.GetValue(UrlProperty);
    public static void SetUrl(DependencyObject d, string? v) => d.SetValue(UrlProperty, v);
    public static int GetDecodeWidth(DependencyObject d) => (int)d.GetValue(DecodeWidthProperty);
    public static void SetDecodeWidth(DependencyObject d, int v) => d.SetValue(DecodeWidthProperty, v);

    private static async void OnUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image img) return;
        var url = e.NewValue as string;
        var width = GetDecodeWidth(d);
        var cached = url is null ? null : TryGet(url, width);
        img.Source = cached;
        if (cached is not null || url is null) return;
        var src = await GetAsync(url, width);
        // Kierrätetty säiliö on voinut vaihtaa URL:n odotuksen aikana
        if (GetUrl(img) == url) img.Source = src;
    }
}
