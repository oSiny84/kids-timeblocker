using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TimeBlocker.Shared.Common;
using TimeBlocker.Shared.Configuration;
using TimeBlocker.Shared.Remote;

namespace TimeBlocker.Service.Remote;

/// <summary>
/// Telegram Bot API long polling 클라이언트.
///
/// 구조상 중요한 점:
///  - PC 에 외부 포트를 열지 않는다. 항상 서비스가 Telegram 으로 outbound 요청만 한다.
///  - 네트워크가 끊기거나 Telegram 이 죽어도 여기서 계속 재시도할 뿐,
///    로컬 차단 기능(EnforcementWorker)에는 아무 영향을 주지 않는다.
///  - 허용된 숫자 User ID 이외의 메시지는 처리하지 않는다. (username 으로 인증하지 않는다)
///  - Bot Token 은 절대 로그에 남기지 않는다.
/// </summary>
public sealed class TelegramRemoteCommandProvider : IRemoteCommandProvider
{
    private const string ApiBase = "https://api.telegram.org";

    private readonly IConfigurationStore _configStore;
    private readonly IRemoteCommandHandler _handler;
    private readonly IHttpClientFactoryLite _httpFactory;
    private readonly ILogger<TelegramRemoteCommandProvider> _logger;

    /// <summary>처리 완료한 update id. 서비스 재시작 시 예전 명령이 재실행되지 않도록 저장한다.</summary>
    private readonly string _offsetFile = Path.Combine(AppPaths.RootDirectory, "state", "telegram-offset.txt");

    private long _offset;

    public TelegramRemoteCommandProvider(
        IConfigurationStore configStore,
        IRemoteCommandHandler handler,
        IHttpClientFactoryLite httpFactory,
        ILogger<TelegramRemoteCommandProvider> logger)
    {
        _configStore = configStore;
        _handler = handler;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public string Name => "Telegram";

    public bool IsRunning { get; private set; }

    public string? StatusText { get; private set; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _offset = LoadOffset();

        while (!cancellationToken.IsCancellationRequested)
        {
            var settings = _configStore.Current.Telegram;

            if (!settings.Enabled)
            {
                IsRunning = false;
                StatusText = "Disabled in configuration";
                await DelayAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                continue;
            }

            var token = SecretProtector.Unprotect(settings.ProtectedBotToken);
            if (string.IsNullOrWhiteSpace(token))
            {
                IsRunning = false;
                StatusText = "Bot token is not configured";
                _logger.LogWarning("Telegram 이 켜져 있지만 Bot Token 이 설정되지 않았습니다.");
                await DelayAsync(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (settings.AllowedUserIds.Count == 0)
            {
                IsRunning = false;
                StatusText = "No allowed user id configured";
                _logger.LogWarning("허용된 Telegram User ID 가 없습니다. 어떤 명령도 처리하지 않습니다.");
                await DelayAsync(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await PollOnceAsync(token, settings, cancellationToken).ConfigureAwait(false);
                IsRunning = true;
                StatusText = "Polling";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 네트워크 끊김, API 장애 등. 로컬 차단은 그대로 동작하므로 여기서는 재시도만 한다.
                IsRunning = false;
                StatusText = $"Retrying after error: {ex.GetType().Name}";
                _logger.LogWarning("Telegram 폴링 실패({Error}). {Delay}초 후 재시도합니다.",
                    ex.GetType().Name, settings.RetryDelaySeconds);

                await DelayAsync(TimeSpan.FromSeconds(settings.RetryDelaySeconds), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        IsRunning = false;
        StatusText = "Stopped";
    }

    private async Task PollOnceAsync(string token, TelegramSettings settings, CancellationToken ct)
    {
        var client = _httpFactory.Create();

        // HttpClient 는 공유하므로 Timeout 속성을 건드리지 않고 요청별 CTS 로 제한한다.
        // long polling 이므로 폴링 타임아웃보다 넉넉하게 잡는다.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(settings.PollTimeoutSeconds + 20));
        var requestToken = timeoutCts.Token;

        var url = $"{ApiBase}/bot{token}/getUpdates" +
                  $"?timeout={settings.PollTimeoutSeconds}" +
                  $"&allowed_updates=[\"message\"]" +
                  (_offset > 0 ? $"&offset={_offset}" : string.Empty);

        using var response = await client.GetAsync(url, requestToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // 토큰이 URL 에 들어 있으므로 URL 을 로그에 남기지 않는다.
            _logger.LogWarning("Telegram getUpdates 응답 코드 {Code}", (int)response.StatusCode);
            await DelayAsync(TimeSpan.FromSeconds(settings.RetryDelaySeconds), ct).ConfigureAwait(false);
            return;
        }

        var json = await response.Content.ReadAsStringAsync(requestToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            _logger.LogWarning("Telegram API 가 오류를 반환했습니다.");
            await DelayAsync(TimeSpan.FromSeconds(settings.RetryDelaySeconds), ct).ConfigureAwait(false);
            return;
        }

        if (!document.RootElement.TryGetProperty("result", out var results)) return;

        foreach (var update in results.EnumerateArray())
        {
            if (ct.IsCancellationRequested) return;

            if (update.TryGetProperty("update_id", out var updateId))
            {
                // 다음 폴링에서 같은 update 를 다시 받지 않도록 +1
                _offset = Math.Max(_offset, updateId.GetInt64() + 1);
                SaveOffset(_offset);
            }

            await HandleUpdateAsync(token, settings, update, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleUpdateAsync(string token, TelegramSettings settings, JsonElement update, CancellationToken ct)
    {
        if (!update.TryGetProperty("message", out var message)) return;
        if (!message.TryGetProperty("text", out var textElement)) return;

        var text = textElement.GetString();
        if (string.IsNullOrWhiteSpace(text)) return;

        long fromId = 0;
        if (message.TryGetProperty("from", out var from) && from.TryGetProperty("id", out var fromIdElement))
        {
            fromId = fromIdElement.GetInt64();
        }

        long chatId = 0;
        if (message.TryGetProperty("chat", out var chat) && chat.TryGetProperty("id", out var chatIdElement))
        {
            chatId = chatIdElement.GetInt64();
        }

        // 인증: 숫자 User ID 기준. username 은 신뢰하지 않는다.
        if (!settings.AllowedUserIds.Contains(fromId))
        {
            _logger.LogWarning("Unauthorized Telegram access attempt UserId={UserId}", fromId);
            return;
        }

        var response = await _handler
            .ExecuteTextAsync(text, $"Telegram:{fromId}", ct)
            .ConfigureAwait(false);

        if (chatId != 0)
        {
            await SendMessageAsync(token, chatId, response, ct).ConfigureAwait(false);
        }
    }

    private async Task SendMessageAsync(string token, long chatId, string text, CancellationToken ct)
    {
        try
        {
            var client = _httpFactory.Create();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));

            // Telegram 메시지 최대 길이는 4096자다.
            if (text.Length > 3900) text = text[..3900] + "\n... (truncated)";

            var payload = JsonSerializer.Serialize(new
            {
                chat_id = chatId,
                text
            });

            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await client
                .PostAsync($"{ApiBase}/bot{token}/sendMessage", content, timeoutCts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Telegram sendMessage 실패: 코드 {Code}", (int)response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Telegram 응답 전송 실패: {Error}", ex.GetType().Name);
        }
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 종료 중이면 조용히 끝낸다.
        }
    }

    private long LoadOffset()
    {
        try
        {
            if (File.Exists(_offsetFile) && long.TryParse(File.ReadAllText(_offsetFile).Trim(), out var value))
            {
                return value;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Telegram offset 파일을 읽지 못했습니다.");
        }
        return 0;
    }

    private void SaveOffset(long offset)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_offsetFile)!);
            File.WriteAllText(_offsetFile, offset.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Telegram offset 파일을 저장하지 못했습니다.");
        }
    }
}

/// <summary>
/// HttpClient 생성을 감싸 테스트에서 교체할 수 있게 한다.
/// (전체 IHttpClientFactory 를 끌어오지 않고 최소한만 둔다)
/// </summary>
public interface IHttpClientFactoryLite
{
    HttpClient Create();
}

public sealed class SharedHttpClientFactory : IHttpClientFactoryLite, IDisposable
{
    // 소켓 고갈을 피하기 위해 하나를 재사용한다.
    // Timeout 은 무한으로 두고, 호출하는 쪽에서 CancellationToken 으로 제한한다.
    private readonly HttpClient _client = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(15)
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public HttpClient Create() => _client;

    public void Dispose() => _client.Dispose();
}
