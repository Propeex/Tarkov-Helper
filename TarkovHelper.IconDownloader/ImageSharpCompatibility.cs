using SkiaSharp;

namespace SixLabors.ImageSharp;

/// <summary>
/// Minimal adapter used by the icon downloader so its conversion path remains
/// isolated from the WPF application. Skia decodes the tarkov.dev WebP payload
/// and writes a PNG that the existing ImageCacheService can load by item ID.
/// </summary>
internal sealed class Image : IDisposable
{
    private readonly SKBitmap _bitmap;

    private Image(SKBitmap bitmap)
    {
        _bitmap = bitmap;
    }

    public static Image Load(byte[] bytes)
    {
        var bitmap = SKBitmap.Decode(bytes)
            ?? throw new InvalidDataException("지원되지 않거나 손상된 이미지입니다.");
        return new Image(bitmap);
    }

    public Task SaveAsPngAsync(Stream output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var image = SKImage.FromBitmap(_bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidDataException("PNG 변환에 실패했습니다.");
        encoded.SaveTo(output);
        return output.FlushAsync(cancellationToken);
    }

    public void Dispose()
    {
        _bitmap.Dispose();
    }
}
