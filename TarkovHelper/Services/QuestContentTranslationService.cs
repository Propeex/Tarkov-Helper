using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using TarkovHelper.Models;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Services;

/// <summary>
/// 퀘스트 제목의 기존 언어를 유지하면서 영어로 남은 목표 문장만 한국어로 보완합니다.
/// 번역 결과는 로컬 캐시에 저장하며 사용자 진행도나 게임 로그는 외부로 전송하지 않습니다.
/// </summary>
public sealed class QuestContentTranslationService
{
    private const string TranslationEndpoint = "https://api.mymemory.translated.net/get";
    private const int MaxSegmentBytes = 420;
    private static readonly TimeSpan MinimumRequestInterval = TimeSpan.FromMilliseconds(250);
    private static readonly ILogger Log = Services.Logging.Log.For<QuestContentTranslationService>();
    private static readonly Lazy<QuestContentTranslationService> LazyInstance =
        new(() => new QuestContentTranslationService());

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _translationGate = new(1, 1);
    private readonly string _cachePath;
    private Dictionary<string, string>? _cache;
    private DateTime _lastRequestUtc = DateTime.MinValue;

    public static QuestContentTranslationService Instance => LazyInstance.Value;

    private QuestContentTranslationService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TarkovHelper/1.5.7");

        _cachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TarkovHelper",
            "quest_content_ko_cache.json");
    }

    /// <summary>
    /// 완성형·자모를 포함한 실제 한국어 문자열인지 확인합니다.
    /// 영어 fallback을 한국어 번역으로 오인하지 않기 위해 사용합니다.
    /// </summary>
    public static bool ContainsHangul(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        foreach (var character in text)
        {
            if (character is >= '\u1100' and <= '\u11FF' ||
                character is >= '\u3130' and <= '\u318F' ||
                character is >= '\uAC00' and <= '\uD7A3')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 한국어 제목이 실제로 존재할 때만 사용하고, 그렇지 않으면 기존 영문 제목을 유지합니다.
    /// </summary>
    public static string SelectQuestTitle(string englishTitle, string? koreanTitle)
    {
        return ContainsHangul(koreanTitle)
            ? koreanTitle!.Trim()
            : englishTitle;
    }

    /// <summary>
    /// 실제 한국어 내용이 있으면 사용하고, 없으면 원문을 반환합니다.
    /// </summary>
    public static string SelectQuestContent(string sourceText, string? koreanText)
    {
        return ContainsHangul(koreanText)
            ? koreanText!.Trim()
            : sourceText;
    }

    /// <summary>
    /// 지도 목표 모델에서 영어로 남은 설명만 번역합니다.
    /// </summary>
    public async Task TranslateMissingAsync(
        IReadOnlyCollection<QuestObjective> objectives,
        CancellationToken cancellationToken = default)
    {
        if (objectives.Count == 0)
            return;

        var sources = objectives
            .Where(objective =>
                !ContainsHangul(objective.DescriptionKo) &&
                !ContainsHangul(objective.Description) &&
                !string.IsNullOrWhiteSpace(objective.Description))
            .Select(objective => objective.Description.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var translations = await GetTranslationsAsync(sources, cancellationToken);
        if (translations.Count == 0)
            return;

        foreach (var objective in objectives)
        {
            var source = objective.Description.Trim();
            if (!ContainsHangul(objective.DescriptionKo) &&
                translations.TryGetValue(source, out var translated))
            {
                objective.DescriptionKo = translated;
            }
        }
    }

    /// <summary>
    /// 퀘스트 탭의 목표 문장 목록에서 영어로 남은 문장만 번역합니다.
    /// 퀘스트 제목은 수정하지 않습니다.
    /// </summary>
    public async Task TranslateMissingAsync(
        IEnumerable<TarkovTask> tasks,
        CancellationToken cancellationToken = default)
    {
        var taskList = tasks.ToList();
        var sources = taskList
            .Where(task => task.Objectives != null)
            .SelectMany(task => task.Objectives!)
            .Where(text => !string.IsNullOrWhiteSpace(text) && !ContainsHangul(text))
            .Select(text => text.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var translations = await GetTranslationsAsync(sources, cancellationToken);
        if (translations.Count == 0)
            return;

        foreach (var task in taskList)
        {
            if (task.Objectives == null)
                continue;

            for (var index = 0; index < task.Objectives.Count; index++)
            {
                var source = task.Objectives[index]?.Trim();
                if (!string.IsNullOrWhiteSpace(source) &&
                    !ContainsHangul(source) &&
                    translations.TryGetValue(source, out var translated))
                {
                    task.Objectives[index] = translated;
                }
            }
        }
    }

    private async Task<Dictionary<string, string>> GetTranslationsAsync(
        IReadOnlyCollection<string> sources,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (sources.Count == 0)
            return result;

        await _translationGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureCacheLoadedAsync(cancellationToken);
            var cacheChanged = false;

            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_cache!.TryGetValue(source, out var cached) && ContainsHangul(cached))
                {
                    result[source] = cached;
                    continue;
                }

                var translated = await TranslateTextAsync(source, cancellationToken);
                if (!ContainsHangul(translated))
                    continue;

                translated = translated!.Trim();
                _cache[source] = translated;
                result[source] = translated;
                cacheChanged = true;
            }

            if (cacheChanged)
                await SaveCacheAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // 번역 서비스 장애가 퀘스트/지도 데이터 로드를 막아서는 안 됩니다.
            Log.Warning($"퀘스트 목표 자동 번역을 완료하지 못했습니다: {exception.Message}");
        }
        finally
        {
            _translationGate.Release();
        }

        return result;
    }

    private async Task<string?> TranslateTextAsync(
        string source,
        CancellationToken cancellationToken)
    {
        var translatedSegments = new List<string>();

        foreach (var segment in SplitByUtf8Length(source, MaxSegmentBytes))
        {
            var elapsed = DateTime.UtcNow - _lastRequestUtc;
            if (elapsed < MinimumRequestInterval)
                await Task.Delay(MinimumRequestInterval - elapsed, cancellationToken);

            var uri = $"{TranslationEndpoint}?q={Uri.EscapeDataString(segment)}&langpair=en%7Cko&mt=1";
            using var response = await _httpClient.GetAsync(uri, cancellationToken);
            _lastRequestUtc = DateTime.UtcNow;

            if (!response.IsSuccessStatusCode)
            {
                Log.Warning($"퀘스트 목표 번역 요청 실패: {(int)response.StatusCode} {response.ReasonPhrase}");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;

            if (root.TryGetProperty("responseStatus", out var statusElement) &&
                statusElement.ValueKind == JsonValueKind.Number &&
                statusElement.GetInt32() != 200)
            {
                var details = root.TryGetProperty("responseDetails", out var detailsElement)
                    ? detailsElement.ToString()
                    : "알 수 없는 오류";
                Log.Warning($"퀘스트 목표 번역 서비스 오류: {details}");
                return null;
            }

            if (!root.TryGetProperty("responseData", out var responseData) ||
                !responseData.TryGetProperty("translatedText", out var translatedElement))
            {
                return null;
            }

            var translated = WebUtility.HtmlDecode(translatedElement.GetString());
            if (string.IsNullOrWhiteSpace(translated))
                return null;

            translatedSegments.Add(translated.Trim());
        }

        return translatedSegments.Count == 0
            ? null
            : string.Join(" ", translatedSegments);
    }

    private async Task EnsureCacheLoadedAsync(CancellationToken cancellationToken)
    {
        if (_cache != null)
            return;

        _cache = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(_cachePath))
            return;

        try
        {
            await using var stream = new FileStream(
                _cachePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                useAsync: true);
            var loaded = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(
                stream,
                cancellationToken: cancellationToken);

            if (loaded == null)
                return;

            foreach (var (source, translated) in loaded)
            {
                if (!string.IsNullOrWhiteSpace(source) && ContainsHangul(translated))
                    _cache[source] = translated;
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warning($"퀘스트 번역 캐시를 읽지 못했습니다: {exception.Message}");
        }
    }

    private async Task SaveCacheAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_cachePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var tempPath = _cachePath + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    _cache,
                    new JsonSerializerOptions { WriteIndented = true },
                    cancellationToken);
            }

            File.Move(tempPath, _cachePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"퀘스트 번역 캐시를 저장하지 못했습니다: {exception.Message}");
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // 캐시 임시 파일 정리 실패는 무시합니다.
            }
        }
    }

    private static IReadOnlyList<string> SplitByUtf8Length(string text, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
            return new[] { text };

        var segments = new List<string>();
        var builder = new StringBuilder();

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = builder.Length == 0 ? word : $"{builder} {word}";
            if (Encoding.UTF8.GetByteCount(candidate) <= maxBytes)
            {
                builder.Clear();
                builder.Append(candidate);
                continue;
            }

            if (builder.Length > 0)
            {
                segments.Add(builder.ToString());
                builder.Clear();
            }

            if (Encoding.UTF8.GetByteCount(word) <= maxBytes)
            {
                builder.Append(word);
                continue;
            }

            var oversized = new StringBuilder();
            foreach (var character in word)
            {
                var characterText = character.ToString();
                if (Encoding.UTF8.GetByteCount(oversized.ToString() + characterText) > maxBytes &&
                    oversized.Length > 0)
                {
                    segments.Add(oversized.ToString());
                    oversized.Clear();
                }
                oversized.Append(character);
            }

            if (oversized.Length > 0)
                builder.Append(oversized);
        }

        if (builder.Length > 0)
            segments.Add(builder.ToString());

        return segments;
    }
}
