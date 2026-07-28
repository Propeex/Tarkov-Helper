using TarkovHelper.Services;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var service = DatabaseUpdateService.Instance;
service.ProgressChanged += (_, progress) =>
{
    Console.WriteLine($"[{progress.Percent,6:F1}%] {progress.Message}");
};

try
{
    var result = await service.CheckAndUpdateAsync();
    Console.WriteLine(result.Message);
    return result.Success && result.WasUpdated ? 0 : 1;
}
finally
{
    service.Dispose();
}
