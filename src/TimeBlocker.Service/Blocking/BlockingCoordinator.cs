using Microsoft.Extensions.Logging;
using TimeBlocker.Service.Blocking.Dns;
using TimeBlocker.Shared.Configuration;
using TimeBlocker.Shared.Core;
using TimeBlocker.Shared.Models;

namespace TimeBlocker.Service.Blocking;

/// <summary>
/// 정책 판정 결과를 실제 시스템 상태(hosts / DNS 프록시 / 방화벽)로 옮기는 조정자.
///
/// 원칙:
///  - 차단 여부는 절대 여기서 판단하지 않는다. AccessPolicyEngine 결과만 사용한다.
///  - 상태가 실제로 바뀐 경우에만 hosts/방화벽을 건드린다. (10초마다 파일을 쓰면 안 된다)
///  - 어떤 단계가 실패해도 예외를 밖으로 던지지 않는다. 다음 주기에 다시 시도한다.
/// </summary>
public sealed class BlockingCoordinator : IEnforcementController, IAsyncDisposable
{
    private readonly IConfigurationStore _configStore;
    private readonly IAccessPolicyEngine _policy;
    private readonly IHostsFileManager _hosts;
    private readonly IFirewallManager _firewall;
    private readonly IDnsProxyServer _dnsProxy;
    private readonly INetworkAdapterDnsConfigurator _adapters;
    private readonly IDnsCacheFlusher _dnsCache;
    private readonly IDnsSelfTest _selfTest;
    private readonly RobloxLocator _locator;
    private readonly ProcessEnforcer _processEnforcer;

    /// <summary>실행파일을 못 찾았다는 경고를 마지막으로 남긴 시각. 로그 폭주를 막는다.</summary>
    private readonly Dictionary<BlockTarget, DateTimeOffset> _noExecutableWarned = new();
    private readonly ILogger<BlockingCoordinator> _logger;

    private readonly SemaphoreSlim _applyLock = new(1, 1);

    /// <summary>프록시가 실패한 뒤 다시 시도하기까지의 최소 간격.</summary>
    private static readonly TimeSpan ProxyRetryInterval = TimeSpan.FromMinutes(1);

    // 마지막으로 적용한 상태. 같은 상태면 시스템을 건드리지 않는다.
    private string _appliedSignature = string.Empty;
    private bool _dnsProxyConfigured;
    private bool _adaptersRedirected;
    private IReadOnlyList<string> _detectedExecutables = Array.Empty<string>();
    private readonly Dictionary<BlockTarget, bool> _lastBlockedState = new();

    /// <summary>프록시 상태를 다시 확인하는 주기. 너무 잦으면 DNS 질의를 낭비한다.</summary>
    private static readonly TimeSpan HealthCheckInterval = TimeSpan.FromMinutes(2);

    /// <summary>연속 몇 번 실패하면 어댑터를 원복하고 폴백으로 넘어갈지.</summary>
    private const int HealthCheckFailureThreshold = 2;

    // DNS self-test / health check 상태
    private bool? _selfTestPassed;
    private string? _selfTestDetail;
    private DateTimeOffset _lastHealthCheckUtc = DateTimeOffset.MinValue;
    private int _consecutiveHealthFailures;

    // DNS 폴백 상태
    private bool _hostsFallbackActive;
    private bool _fallbackWarningLogged;
    private string? _dnsLastError;
    private DateTimeOffset _lastProxyAttemptUtc = DateTimeOffset.MinValue;

    public BlockingCoordinator(
        IConfigurationStore configStore,
        IAccessPolicyEngine policy,
        IHostsFileManager hosts,
        IFirewallManager firewall,
        IDnsProxyServer dnsProxy,
        INetworkAdapterDnsConfigurator adapters,
        IDnsCacheFlusher dnsCache,
        IDnsSelfTest selfTest,
        RobloxLocator locator,
        ProcessEnforcer processEnforcer,
        ILogger<BlockingCoordinator> logger)
    {
        _configStore = configStore;
        _policy = policy;
        _hosts = hosts;
        _firewall = firewall;
        _dnsProxy = dnsProxy;
        _adapters = adapters;
        _dnsCache = dnsCache;
        _selfTest = selfTest;
        _locator = locator;
        _processEnforcer = processEnforcer;
        _logger = logger;
    }

    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;

    public string BlockingModeDescription { get; private set; } = "Initializing";

    public IReadOnlyList<string> DetectedExecutables => _detectedExecutables;

    /// <summary>status 명령에서 보여줄 DNS 런타임 상태.</summary>
    public DnsRuntimeStatus DnsStatus
    {
        get
        {
            var dns = _configStore.Current.Dns;
            if (!dns.Enabled) return DnsRuntimeStatus.Disabled(dns.Mode);

            var proxyState = dns.Mode switch
            {
                // Hosts 모드에서는 프록시를 아예 쓰지 않는다.
                DnsBlockingMode.Hosts => DnsProxyState.NotUsed,
                _ when _dnsProxy.IsRunning => DnsProxyState.Running,
                _ when _hostsFallbackActive || _dnsLastError is not null => DnsProxyState.Failed,
                _ => DnsProxyState.Stopped
            };

            var configured = _adapters.ConfiguredAdapterNames;

            return new DnsRuntimeStatus
            {
                Enabled = true,
                Mode = dns.Mode,
                ProxyState = proxyState,
                HostsFallbackActive = _hostsFallbackActive,
                UpstreamServers = dns.UpstreamServers.ToList(),
                LastError = proxyState == DnsProxyState.Failed ? (_dnsLastError ?? _dnsProxy.LastError) : null,
                AutoConfigureEnabled = dns.AutoConfigureAdapters,
                AdapterDns = configured.Count > 0 ? "127.0.0.1" : "(원래 설정)",
                ConfiguredAdapters = configured,
                OriginalDnsSaved = _adapters.HasSavedOriginal,
                SelfTestPassed = _selfTestPassed,
                SelfTestDetail = _selfTestDetail
            };
        }
    }

    /// <summary>마지막 판정 결과. 상태 조회용.</summary>
    public IReadOnlyList<AccessDecision> LastDecisions { get; private set; } = Array.Empty<AccessDecision>();

    public async Task ApplyNowAsync(CancellationToken cancellationToken = default)
    {
        await _applyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ApplyCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 적용 실패가 서비스 종료로 이어지면 안 된다.
            _logger.LogError(ex, "차단 정책 적용 중 오류가 발생했습니다. 다음 주기에 다시 시도합니다.");
        }
        finally
        {
            _applyLock.Release();
        }
    }

    private async Task ApplyCoreAsync(CancellationToken ct)
    {
        var config = _configStore.Current;
        var decisions = _policy.EvaluateAll();
        LastDecisions = decisions;

        // 1. DNS 로 막아야 할 도메인 모으기
        var domainsToBlock = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var decision in decisions)
        {
            if (!decision.IsBlocked) continue;

            var settings = config.GetTarget(decision.Target);
            if (!config.Dns.Enabled || !settings.UseDnsBlocking) continue;

            foreach (var domain in settings.Domains)
            {
                var normalized = domain.Trim().TrimEnd('.').ToLowerInvariant();
                if (normalized.Length > 0) domainsToBlock.Add(normalized);
            }
        }

        // 2. 방화벽으로 막아야 할 실행파일 모으기
        var executablesToBlock = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var decision in decisions)
        {
            var settings = config.GetTarget(decision.Target);
            if (!settings.UseFirewallBlocking) continue;

            var ruleName = GetRuleName(decision.Target);
            if (!decision.IsBlocked)
            {
                executablesToBlock[ruleName] = new List<string>();
                continue;
            }

            var found = _locator.Locate(settings.ProcessNames).ToList();
            found.AddRange(settings.ExtraExecutablePaths.Where(File.Exists));
            var paths = found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            executablesToBlock[ruleName] = paths;

            // 실행파일을 못 찾으면 방화벽 규칙이 아예 만들어지지 않는다.
            // 조용히 넘어가면 "막았는데 게임이 되네" 로 이어지므로 반드시 알린다.
            if (paths.Count == 0) WarnNoExecutable(decision.Target, settings);
        }

        _detectedExecutables = executablesToBlock.Values.SelectMany(v => v).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // 3. 상태가 바뀌었는지 확인
        //    단, hosts 폴백 중이라면 프록시 복구를 시도해야 하므로 그냥 넘어가지 않는다.
        // 프록시가 도는 중이라면 주기적으로 살아 있는지 확인한다.
        // (상태 변화가 없어도 반드시 수행해야 하므로 signature 비교보다 앞에 둔다)
        if (config.Dns.Enabled && config.Dns.Mode != DnsBlockingMode.Hosts)
        {
            await RunHealthCheckAsync(config, domainsToBlock, ct).ConfigureAwait(false);
        }

        // 3-1. 차단 시간인데 게임이 돌고 있으면 유예 시간을 주고 종료한다.
        //      DNS/방화벽은 이미 붙어 있는 연결을 못 끊는 경우가 있어 별도로 확인한다.
        //      상태가 그대로여도 매 주기 확인해야 하므로 signature 비교보다 앞에 둔다.
        try
        {
            _processEnforcer.Evaluate(decisions, config);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "프로세스 단속 중 오류가 발생했습니다. 다음 주기에 다시 시도합니다.");
        }

        var signature = BuildSignature(config, domainsToBlock, executablesToBlock);
        if (signature == _appliedSignature && !_hostsFallbackActive)
        {
            return; // 변경 없음 - 시스템을 건드리지 않는다
        }

        LogTransitions(decisions);

        // 4. DNS 적용
        var dnsChanged = await ApplyDnsAsync(config, domainsToBlock, ct).ConfigureAwait(false);

        // 5. 방화벽 적용
        var firewallChanged = false;
        foreach (var (ruleName, paths) in executablesToBlock)
        {
            firewallChanged |= paths.Count > 0
                ? await _firewall.BlockAsync(ruleName, paths, ct).ConfigureAwait(false)
                : await _firewall.ClearAsync(ruleName, ct).ConfigureAwait(false);
        }

        _appliedSignature = signature;
        BlockingModeDescription = BuildModeDescription(config);

        // 6. 상태가 바뀌었으면 DNS 캐시를 비워 즉시 반영되게 한다.
        if ((dnsChanged || firewallChanged) && config.Dns.FlushCacheOnChange)
        {
            await _dnsCache.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// hosts / DNS 프록시 중 설정된 방식으로 도메인 차단을 적용한다.
    ///
    /// 동작 우선순위:
    ///   DNS Proxy 정상            -> Proxy 기반 차단 사용 (하위 도메인까지 차단)
    ///   Proxy 초기화 실패/치명적 오류 -> Hosts fallback 적용 (ProxyWithHostsFallback 인 경우)
    ///
    /// 프록시가 도는 동안에는 hosts 에 동적 도메인을 대량으로 써넣지 않는다.
    /// 차단 판정은 전부 프록시가 메모리에서 처리한다.
    /// </summary>
    private async Task<bool> ApplyDnsAsync(TimeBlockerConfig config, SortedSet<string> domains, CancellationToken ct)
    {
        if (!config.Dns.Enabled)
        {
            SetFallbackState(false);
            return _hosts.Clear();
        }

        var useProxy = config.Dns.Mode is DnsBlockingMode.Proxy or DnsBlockingMode.ProxyWithHostsFallback;

        if (useProxy)
        {
            var started = await TryStartOrKeepProxyAsync(config, ct).ConfigureAwait(false);

            if (started)
            {
                try
                {
                    _dnsProxy.UpdateBlockedDomains(domains);

                    // 어댑터 DNS 를 바꾸기 전에 프록시가 정말 응답하는지 확인한다.
                    // 여기서 실패하면 어댑터는 건드리지 않는다. (인터넷을 끊지 않기 위해)
                    if (config.Dns.AutoConfigureAdapters && !_adaptersRedirected)
                    {
                        if (!await TryConfigureAdaptersAsync(config, domains, ct).ConfigureAwait(false))
                        {
                            // 프록시는 떠 있지만 어댑터를 바꾸지 못했다.
                            // 프록시를 거치지 않으므로 차단 효과가 없다. hosts 폴백으로 넘어간다.
                            _dnsLastError ??= "어댑터 DNS 구성에 실패했습니다.";
                            SetFallbackState(true);
                            await StopProxyAsync(ct).ConfigureAwait(false);
                            return domains.Count > 0 ? _hosts.Apply(domains.ToList()) : _hosts.Clear();
                        }
                    }
                    else if (!config.Dns.AutoConfigureAdapters)
                    {
                        // 어댑터를 바꾸지 않는 설정. 프록시 자체만 확인해 둔다.
                        await RunSelfTestAsync(config, domains, adapterChanged: false, ct).ConfigureAwait(false);
                    }

                    _dnsProxyConfigured = true;
                    _dnsLastError = null;

                    if (_hostsFallbackActive)
                    {
                        _logger.LogInformation("DNS 프록시가 복구되었습니다. hosts 폴백을 해제합니다.");
                    }
                    SetFallbackState(false);

                    // 프록시가 도는 동안에는 hosts 관리 구간을 비워 둔다.
                    // (동적 하위 도메인을 hosts 에 대량으로 써넣지 않는다)
                    _hosts.Clear();
                    return true;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 프록시 설정 도중 오류가 나도 서비스는 살아 있어야 한다. 폴백으로 넘어간다.
                    _dnsLastError = $"Proxy configuration failed ({ex.GetType().Name})";
                    _logger.LogError(ex, "DNS 프록시 설정 중 오류가 발생했습니다.");
                }
            }

            if (config.Dns.Mode == DnsBlockingMode.Proxy)
            {
                // 폴백이 꺼진 모드. hosts 를 건드리지 않는다.
                _dnsLastError ??= _dnsProxy.LastError ?? "Proxy unavailable";
                SetFallbackState(false);
                _logger.LogError(
                    "DNS 프록시를 시작하지 못했습니다. Mode=Proxy 라 hosts 폴백을 쓰지 않으므로 도메인 차단이 적용되지 않습니다.");
                return false;
            }

            _dnsLastError ??= _dnsProxy.LastError ?? "Proxy unavailable";
            SetFallbackState(true);
        }
        else
        {
            // Hosts 모드에서는 폴백이라는 개념이 없다.
            SetFallbackState(false);
        }

        // hosts 방식
        if (_dnsProxyConfigured)
        {
            await StopProxyAsync(ct).ConfigureAwait(false);
        }

        return domains.Count > 0 ? _hosts.Apply(domains.ToList()) : _hosts.Clear();
    }

    /// <summary>
    /// 프록시가 이미 돌고 있으면 그대로 쓰고, 아니면 시작을 시도한다.
    /// 실패 직후 매 주기마다 재시도하지 않도록 최소 간격을 둔다.
    /// </summary>
    private async Task<bool> TryStartOrKeepProxyAsync(TimeBlockerConfig config, CancellationToken ct)
    {
        if (_dnsProxy.IsRunning) return true;

        var now = DateTimeOffset.UtcNow;
        if (_hostsFallbackActive && now - _lastProxyAttemptUtc < ProxyRetryInterval)
        {
            // 폴백 중이며 아직 재시도 간격이 안 됐다.
            return false;
        }

        _lastProxyAttemptUtc = now;

        try
        {
            return await _dnsProxy.StartAsync(config.Dns, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // StartAsync 는 자체적으로 예외를 삼키지만, 만약을 대비해 한 번 더 막는다.
            _dnsLastError = $"Proxy start failed ({ex.GetType().Name})";
            _logger.LogError(ex, "DNS 프록시 시작 호출에서 예외가 발생했습니다.");
            return false;
        }
    }


    // ============================================================ 어댑터 DNS 안전 절차

    /// <summary>
    /// 어댑터 DNS 를 127.0.0.1 로 바꾸는 전체 절차.
    ///
    ///   DNS Proxy 시작 (이미 완료)
    ///     -> localhost DNS self-test
    ///     -> 정상 응답 확인
    ///     -> 어댑터 DNS 를 127.0.0.1 로 변경
    ///     -> 실제 이름 해석 재확인
    ///     -> 실패하면 즉시 원래 설정으로 롤백
    ///
    /// self-test 가 실패하면 어댑터를 아예 건드리지 않는다. (인터넷을 끊지 않기 위해)
    /// </summary>
    private async Task<bool> TryConfigureAdaptersAsync(
        TimeBlockerConfig config, SortedSet<string> domains, CancellationToken ct)
    {
        var allowedDomain = config.Dns.SelfTestDomain;
        var blockedDomain = domains.FirstOrDefault();

        // 1) 어댑터를 바꾸기 전에 프록시가 실제로 응답하는지 확인한다.
        var direct = await _selfTest
            .TestProxyDirectAsync(config.Dns.ProxyPort, allowedDomain, blockedDomain, ct)
            .ConfigureAwait(false);

        RecordSelfTest(direct);

        if (!direct.Success)
        {
            _dnsLastError = $"DNS self-test 실패: {direct.Detail}";
            _logger.LogError(
                "DNS self-test 실패로 어댑터 DNS 를 변경하지 않습니다: {Detail}", direct.Detail);
            return false;
        }

        _logger.LogInformation("DNS self-test 통과. 어댑터 DNS 를 로컬 프록시로 변경합니다.");

        // 2) IPv6 리스너가 없으면 IPv6 DNS 는 건드리지 않는다.
        //    (::1 로 바꿔놓고 받는 쪽이 없으면 IPv6 이름 해석이 완전히 막힌다)
        var configureIpv6 = config.Dns.ConfigureIpv6 && _dnsProxy.Ipv6ListenerActive;
        if (config.Dns.ConfigureIpv6 && !_dnsProxy.Ipv6ListenerActive)
        {
            _logger.LogWarning(
                "IPv6 리스너가 없어 어댑터 IPv6 DNS 는 변경하지 않습니다. " +
                "IPv6 DNS 가 설정된 환경에서는 프록시가 우회될 수 있습니다.");
        }

        var applied = await _adapters.PointToLocalProxyAsync(configureIpv6, ct).ConfigureAwait(false);
        if (!applied.Success)
        {
            _dnsLastError = $"어댑터 DNS 변경 실패: {applied.Error}";
            _logger.LogError("어댑터 DNS 변경 실패: {Error}", applied.Error);

            // 부분 적용 상태일 수 있으므로 반드시 되돌린다.
            await _adapters.RestoreOriginalAsync(ct).ConfigureAwait(false);
            return false;
        }

        _adaptersRedirected = true;

        // 3) 캐시를 비우고 실제 경로로 다시 확인한다.
        await _dnsCache.FlushAsync(ct).ConfigureAwait(false);

        var verified = await VerifyAfterAdapterChangeAsync(
            allowedDomain, blockedDomain, direct.UpstreamUnreachable, ct).ConfigureAwait(false);

        if (!verified)
        {
            _dnsLastError = "어댑터 DNS 변경 후 검증 실패 (원래 설정으로 롤백함)";
            _logger.LogError("어댑터 DNS 변경 후 검증에 실패했습니다. 원래 DNS 설정으로 되돌립니다.");
            await RestoreAdaptersAsync(ct).ConfigureAwait(false);
            return false;
        }

        _logger.LogInformation("어댑터 DNS 변경 및 검증 완료.");
        return true;
    }

    /// <summary>어댑터 DNS 변경 후 실제 이름 해석이 되는지 확인한다.</summary>
    private async Task<bool> VerifyAfterAdapterChangeAsync(
        string allowedDomain, string? blockedDomain, bool upstreamWasUnreachable, CancellationToken ct)
    {
        var result = await _selfTest
            .TestThroughSystemResolverAsync(allowedDomain, blockedDomain, ct)
            .ConfigureAwait(false);

        // 차단 도메인이 여전히 해석되면 어댑터 변경이 먹지 않은 것이다.
        if (!result.BlockedDomainBlocked)
        {
            _logger.LogWarning(
                "어댑터 DNS 변경 후에도 차단 도메인({Domain})이 여전히 해석됩니다. " +
                "OS 가 로컬 프록시를 거치지 않고 다른 경로로 DNS 를 질의하고 있을 수 있습니다.",
                blockedDomain);
            RecordSelfTest(result);
            return false;
        }

        if (result.AllowedDomainResolved)
        {
            RecordSelfTest(result);
            return true;
        }

        // 허용 도메인이 해석되지 않았다.
        // 변경 전에도 상위 DNS 에 닿지 못했다면 인터넷이 끊긴 것이지 우리 문제가 아니다.
        if (upstreamWasUnreachable)
        {
            _logger.LogWarning(
                "허용 도메인을 해석하지 못했지만 변경 전에도 상위 DNS 에 닿지 못했습니다. " +
                "인터넷 연결 문제로 보고 어댑터 설정을 유지합니다.");
            RecordSelfTest(result);
            return true;
        }

        _logger.LogError(
            "어댑터 DNS 변경 후 허용 도메인({Domain})을 해석하지 못했습니다.", allowedDomain);
        RecordSelfTest(result);
        return false;
    }

    /// <summary>
    /// 프록시가 살아 있는지 주기적으로 확인한다.
    /// 연속 실패하면 어댑터 DNS 를 먼저 원복해서 인터넷을 살린 뒤 hosts 폴백으로 넘어간다.
    /// </summary>
    private async Task RunHealthCheckAsync(TimeBlockerConfig config, SortedSet<string> domains, CancellationToken ct)
    {
        if (!_dnsProxy.IsRunning) return;

        var now = DateTimeOffset.UtcNow;
        if (now - _lastHealthCheckUtc < HealthCheckInterval) return;
        _lastHealthCheckUtc = now;

        var result = await _selfTest
            .TestProxyDirectAsync(config.Dns.ProxyPort, config.Dns.SelfTestDomain, domains.FirstOrDefault(), ct)
            .ConfigureAwait(false);

        RecordSelfTest(result);

        if (result.Success)
        {
            _consecutiveHealthFailures = 0;
            return;
        }

        _consecutiveHealthFailures++;
        _logger.LogWarning(
            "DNS 프록시 health check 실패 ({Count}/{Threshold}): {Detail}",
            _consecutiveHealthFailures, HealthCheckFailureThreshold, result.Detail);

        if (_consecutiveHealthFailures < HealthCheckFailureThreshold) return;

        // fail-safe: 인터넷이 끊긴 채로 방치하지 않는다.
        _logger.LogError(
            "DNS 프록시가 비정상 상태입니다. 어댑터 DNS 를 원래대로 되돌리고 hosts 폴백으로 전환합니다.");

        await RestoreAdaptersAsync(ct).ConfigureAwait(false);
        await StopProxyAsync(ct).ConfigureAwait(false);

        _dnsLastError = $"Health check failed: {result.Detail}";
        SetFallbackState(true);

        // 폴백 상태에서 차단이 유지되도록 hosts 를 즉시 적용한다.
        if (domains.Count > 0) _hosts.Apply(domains.ToList());

        _consecutiveHealthFailures = 0;
        _appliedSignature = string.Empty; // 다음 주기에 상태를 다시 계산하도록 한다.
    }

    /// <summary>어댑터 DNS 를 원래대로 되돌린다. 실패해도 예외를 던지지 않는다.</summary>
    private async Task RestoreAdaptersAsync(CancellationToken ct)
    {
        try
        {
            await _adapters.RestoreOriginalAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "어댑터 DNS 원복 중 오류가 발생했습니다.");
        }
        finally
        {
            _adaptersRedirected = false;
        }
    }

    private void RecordSelfTest(DnsSelfTestResult result)
    {
        _selfTestPassed = result.Success;
        _selfTestDetail = result.Success && result.Detail == "OK" ? null : result.Detail;
    }

    /// <summary>어댑터 DNS 만 확인하고 프록시 자체 상태를 기록한다. (AutoConfigureAdapters 가 꺼진 경우)</summary>
    private async Task RunSelfTestAsync(
        TimeBlockerConfig config, SortedSet<string> domains, bool adapterChanged, CancellationToken ct)
    {
        var result = await _selfTest
            .TestProxyDirectAsync(config.Dns.ProxyPort, config.Dns.SelfTestDomain, domains.FirstOrDefault(), ct)
            .ConfigureAwait(false);

        RecordSelfTest(result);
    }

    // ============================================================ 공개 진단/복구 명령

    /// <summary>`dns test` 명령. 지금 즉시 self-test 를 수행하고 결과 문자열을 돌려준다.</summary>
    public async Task<string> RunDnsSelfTestAsync(CancellationToken ct = default)
    {
        var config = _configStore.Current;

        if (!config.Dns.Enabled) return "DNS 차단이 꺼져 있습니다.";
        if (config.Dns.Mode == DnsBlockingMode.Hosts) return "Hosts 모드에서는 DNS 프록시 self-test 를 수행하지 않습니다.";
        if (!_dnsProxy.IsRunning) return $"DNS Proxy 가 실행 중이 아닙니다. (마지막 오류: {_dnsLastError ?? _dnsProxy.LastError ?? "없음"})";

        var domains = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var decision in _policy.EvaluateAll().Where(d => d.IsBlocked))
        {
            foreach (var domain in config.GetTarget(decision.Target).Domains)
            {
                domains.Add(DnsMessage.Normalize(domain));
            }
        }

        var direct = await _selfTest
            .TestProxyDirectAsync(config.Dns.ProxyPort, config.Dns.SelfTestDomain, domains.FirstOrDefault(), ct)
            .ConfigureAwait(false);

        RecordSelfTest(direct);

        var system = await _selfTest
            .TestThroughSystemResolverAsync(config.Dns.SelfTestDomain, domains.FirstOrDefault(), ct)
            .ConfigureAwait(false);

        var builder = new System.Text.StringBuilder();
        builder.AppendLine(direct.Success ? "OK" : "ERROR");
        builder.AppendLine();
        builder.AppendLine($"Proxy direct     : {(direct.Success ? "OK" : "FAILED")} ({direct.Detail})");
        builder.AppendLine($"  allowed ({config.Dns.SelfTestDomain}) : {(direct.AllowedDomainResolved ? "resolved" : "no answer")}");
        builder.AppendLine($"  blocked ({domains.FirstOrDefault() ?? "-"}) : {(direct.BlockedDomainBlocked ? "blocked" : "not blocked")}");
        builder.AppendLine($"System resolver  : {(system.BlockedDomainBlocked ? "blocked OK" : "NOT blocked")}");
        builder.AppendLine($"  allowed resolve: {(system.AllowedDomainResolved ? "OK" : "FAILED")}");
        builder.Append($"Adapter DNS      : {_adapters.DescribeCurrentAdapterDns()}");

        return builder.ToString();
    }

    /// <summary>`dns restore` 명령. 저장된 원래 DNS 설정으로 즉시 되돌린다.</summary>
    public async Task<string> RestoreAdapterDnsAsync(CancellationToken ct = default)
    {
        if (!_adapters.HasSavedOriginal)
        {
            return "OK\n저장된 원래 DNS 설정이 없습니다. 어댑터는 변경되지 않은 상태입니다.";
        }

        var names = _adapters.ConfiguredAdapterNames.ToList();
        var restored = await _adapters.RestoreOriginalAsync(ct).ConfigureAwait(false);
        _adaptersRedirected = false;

        await _dnsCache.FlushAsync(ct).ConfigureAwait(false);

        // 어댑터를 되돌리면 프록시를 거치지 않으므로 차단 효과가 사라진다.
        // 차단을 유지하기 위해 hosts 폴백으로 전환한다.
        _dnsLastError = "관리자가 dns restore 를 실행했습니다.";
        SetFallbackState(true);
        _appliedSignature = string.Empty;
        await ApplyNowAsync(ct).ConfigureAwait(false);

        return $"OK\n어댑터 {restored}개의 DNS 를 원래 설정으로 되돌렸습니다.\n" +
               $"대상: {(names.Count > 0 ? string.Join(", ", names) : "-")}\n\n" +
               "차단은 hosts 방식으로 계속 적용됩니다. (하위 도메인 차단은 제한됨)\n" +
               "프록시 방식으로 돌아가려면 reload 를 실행하세요.";
    }

    /// <summary>
    /// 서비스 시작 시 호출한다.
    /// 이전 실행이 비정상 종료되어 어댑터 DNS 가 127.0.0.1 로 남아 있으면 먼저 안전 상태로 복구한다.
    /// </summary>
    public async Task RecoverAtStartupAsync(CancellationToken ct = default)
    {
        try
        {
            var restored = await _adapters.RecoverFromUncleanShutdownAsync(ct).ConfigureAwait(false);
            if (restored > 0)
            {
                await _dnsCache.FlushAsync(ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "비정상 종료 복구를 마쳤습니다. 이제 DNS 프록시를 다시 초기화합니다.");
            }
        }
        catch (Exception ex)
        {
            // 복구 실패가 서비스 시작을 막으면 안 된다.
            _logger.LogError(ex, "시작 시 어댑터 DNS 복구 중 오류가 발생했습니다.");
        }

        _adaptersRedirected = false;
    }

    /// <summary>
    /// hosts 폴백 상태를 바꾼다.
    /// 폴백으로 처음 전환될 때 hosts 방식의 한계를 분명히 로그로 남긴다. (매 주기 반복 기록하지 않는다)
    /// </summary>
    private void SetFallbackState(bool active)
    {
        if (active && !_fallbackWarningLogged)
        {
            _logger.LogWarning(
                "DNS Proxy unavailable. Hosts fallback activated. " +
                "Wildcard/subdomain blocking capability is limited. " +
                "(googlevideo.com 같은 도메인의 동적 하위 도메인은 차단되지 않습니다) 원인: {Reason}",
                _dnsLastError ?? "unknown");
            _fallbackWarningLogged = true;
        }

        if (!active) _fallbackWarningLogged = false;

        _hostsFallbackActive = active;
    }

    private async Task StopProxyAsync(CancellationToken ct)
    {
        if (_adaptersRedirected)
        {
            await _adapters.RestoreOriginalAsync(ct).ConfigureAwait(false);
            _adaptersRedirected = false;
        }

        await _dnsProxy.StopAsync().ConfigureAwait(false);
        _dnsProxyConfigured = false;
    }

    /// <summary>BLOCK -> ALLOW, ALLOW -> BLOCK 전환만 INFO 로그로 남긴다.</summary>
    private void LogTransitions(IReadOnlyList<AccessDecision> decisions)
    {
        foreach (var decision in decisions)
        {
            if (_lastBlockedState.TryGetValue(decision.Target, out var previous) && previous == decision.IsBlocked)
            {
                continue;
            }

            _lastBlockedState[decision.Target] = decision.IsBlocked;
            _logger.LogInformation(
                "{Target} {State} ({Reason})",
                decision.Target.ToDisplayName(),
                decision.IsBlocked ? "BLOCK" : "ALLOW",
                decision.Reason);
        }
    }

    private static string GetRuleName(BlockTarget target) => target switch
    {
        BlockTarget.Roblox => FirewallManager.RobloxRuleName,
        BlockTarget.YouTube => FirewallManager.YouTubeRuleName,
        _ => $"TimeBlocker_{target}_Block"
    };

    /// <summary>
    /// 방화벽 차단 대상인데 실행파일을 못 찾았다. 매 주기 찍으면 로그가 넘치므로
    /// 처음 한 번과 이후 30분에 한 번만 남긴다.
    /// </summary>
    private void WarnNoExecutable(BlockTarget target, TargetSettings settings)
    {
        var nowUtc = DateTimeOffset.UtcNow;

        lock (_noExecutableWarned)
        {
            if (_noExecutableWarned.TryGetValue(target, out var lastUtc)
                && nowUtc - lastUtc < TimeSpan.FromMinutes(30))
            {
                return;
            }

            _noExecutableWarned[target] = nowUtc;
        }

        _logger.LogWarning(
            "{Target} 차단 시간이지만 실행파일({Names})을 찾지 못해 방화벽 규칙을 만들지 못했습니다. " +
            "도메인 차단만 적용됩니다. 이미 실행 중인 프로그램은 막히지 않을 수 있습니다. " +
            "Microsoft Store 판 등 다른 위치에 설치했다면 ExtraExecutablePaths 에 전체 경로를 적어주세요.",
            target.ToDisplayName(),
            string.Join(", ", settings.ProcessNames));
    }

    private static string BuildSignature(
        TimeBlockerConfig config,
        SortedSet<string> domains,
        Dictionary<string, List<string>> executables)
    {
        var parts = new List<string>
        {
            $"mode={config.Dns.Mode}",
            $"dns={config.Dns.Enabled}",
            "domains=" + string.Join(',', domains)
        };

        foreach (var (rule, paths) in executables.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            parts.Add($"{rule}=" + string.Join(',', paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)));
        }

        return string.Join('|', parts);
    }

    private string BuildModeDescription(TimeBlockerConfig config)
    {
        var parts = new List<string>();

        if (config.Dns.Enabled)
        {
            if (_dnsProxyConfigured && _dnsProxy.IsRunning) parts.Add("DNS Proxy");
            else if (_hostsFallbackActive) parts.Add("Hosts (fallback)");
            else parts.Add("Hosts");
        }

        if (config.YouTube.UseFirewallBlocking || config.Roblox.UseFirewallBlocking)
        {
            parts.Add("Firewall");
        }

        return parts.Count == 0 ? "Disabled" : string.Join(" + ", parts);
    }

    /// <summary>서비스 종료 시 시스템을 원래 상태로 되돌린다.</summary>
    public async Task ShutdownAsync(CancellationToken ct)
    {
        _logger.LogInformation("차단 상태를 정리하는 중입니다...");

        // 1) 어댑터 DNS 를 반드시 먼저 원래대로 되돌린다.
        //    서비스가 내려간 상태에서 DNS 가 127.0.0.1 로 남으면 PC 인터넷이 전부 끊긴다.
        try
        {
            await RestoreAdaptersAsync(ct).ConfigureAwait(false);
            await StopProxyAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DNS 프록시 / 어댑터 정리 중 오류");
        }

        // 2) 어댑터를 되돌리면 프록시 차단이 사라지므로, 지금 차단 중이던 도메인을
        //    hosts 에 남겨 서비스가 없는 동안에도 차단이 유지되게 한다.
        //    (서비스를 끄는 것만으로 차단이 풀리면 프로그램의 목적에 어긋난다)
        try
        {
            var config = _configStore.Current;
            var domains = new SortedSet<string>(StringComparer.Ordinal);

            foreach (var decision in LastDecisions.Where(d => d.IsBlocked))
            {
                var settings = config.GetTarget(decision.Target);
                if (!config.Dns.Enabled || !settings.UseDnsBlocking) continue;

                foreach (var domain in settings.Domains)
                {
                    var normalized = DnsMessage.Normalize(domain);
                    if (normalized.Length > 0) domains.Add(normalized);
                }
            }

            if (domains.Count > 0)
            {
                _hosts.Apply(domains.ToList());
                _logger.LogInformation(
                    "종료 중: 어댑터 DNS 를 원복했고, 차단 유지를 위해 hosts 에 {Count}개 도메인을 남깁니다. " +
                    "(하위 도메인 차단은 제한됩니다)", domains.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "종료 중 hosts 차단 적용에 실패했습니다.");
        }

        _logger.LogInformation("hosts / 방화벽 차단 규칙은 유지됩니다.");
    }

    public async ValueTask DisposeAsync()
    {
        _applyLock.Dispose();
        await Task.CompletedTask;
    }
}
