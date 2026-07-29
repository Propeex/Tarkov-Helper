using System.Runtime.CompilerServices;
using SixLabors.ImageSharp;

internal static class ImageConversionSelfTest
{
    [ModuleInitializer]
    internal static void RunWhenRequested()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("TARKOV_ICON_SELF_TEST"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        // 2x2 lossless red WebP generated specifically for this deterministic test.
        var webp = Convert.FromBase64String("UklGRhwAAABXRUJQVlA4TA8AAAAvAUAAAAcQ/Y/+ByKi/wEA");
        using var image = Image.Load(webp);
        using var output = new MemoryStream();
        image.SaveAsPngAsync(output, CancellationToken.None).GetAwaiter().GetResult();
        var png = output.ToArray();

        var validPng = png.Length >= 8 &&
                       png[0] == 0x89 && png[1] == 0x50 && png[2] == 0x4E && png[3] == 0x47 &&
                       png[4] == 0x0D && png[5] == 0x0A && png[6] == 0x1A && png[7] == 0x0A;
        if (!validPng)
            throw new InvalidDataException("WebP to PNG self-test produced an invalid PNG payload.");

        Console.WriteLine($"Item icon conversion self-test passed: webp={webp.Length} bytes, png={png.Length} bytes");
        Environment.Exit(0);
    }
}
