using Microsoft.Extensions.Logging;
using TimeBlocker.Service.Blocking;
using TimeBlocker.Service.Blocking.Dns;
using TimeBlocker.Shared.Configuration;
using TimeBlocker.Shared.Models;

namespace TimeBlocker.Service.Maintenance;

public interface ISystemRestoreService
{
    /// <summary>
    /// PC 를 TimeBlocker 설치 전 상태로 되돌린다.
    /// 어느 단계가 실패해도 나머지는 계속 수행하고, 전체 결과를 돌려준다.
    /// </summary>
    Task<RestoreReport> RestoreAsync(bool removeStateFiles, CancellationToken ct);
}

/// <summary>
/// cleanup 과 uninstall 이 공유하는 원상복구 로직.
///
/// 복구 순서 (앞 단계가 실패해도 계속 진행한다):
///   1. 일시 허용 / 상태 정리
///   2. 저장된 원래 DNS 복원      <- 인터넷을 먼저 살린다
///   3. TimeBlocker hosts 영역 제거
///   4. TimeBlocker 방화벽 규칙 제거
///   5. DNS 캐시 비우기
///   6. (선택) 남은 상태파일 제거
///
/// 사용자의 기존 hosts 내용과 기존 방화벽 규칙은 절대 건드리지 않는다.
/// - hosts 는 "# TIMEBLOCKER BEGIN ~ END" 마커 구간만 지운다.
/// - 방화벽은 "TimeBlocker_" 로 시작하는 우리 규칙 이름만 지운다.
/// </summary>
public sealed class SystemRestoreService : ISystemRestoreService
{
    private readonly IHostsFileManager _hosts;
    private readonly IFirewallManager _firewall;
    private readonly INetworkAdapterDnsConfigurator _adapters;
    private readonly IDnsCacheFlusher _dnsCache;
    private readonly ILogger _logger;

    public SystemRestoreService(
        IHostsFileManager hosts,
        IFirewallManager firewall,
        INetworkAdapterDnsConfigurator adapters,
        IDnsCacheFlusher dnsCache,
        ILogger logger)
    {
        _hosts = hosts;
        _firewall = firewall;
        _adapters = adapters;
        _dnsCache = dnsCache;
        _logger = logger;
    }

    public async Task<RestoreReport> RestoreAsync(bool removeStateFiles, CancellationToken ct)
    {
        var report = new RestoreReport();

        report.Add(ClearPermits());
        report.Add(await RestoreDnsAsync(ct).ConfigureAwait(false));
        report.Add(CleanupHosts());
        report.Add(await CleanupFirewallAsync(ct).ConfigureAwait(false));
        report.Add(await FlushDnsCacheAsync(ct).ConfigureAwait(false));

        if (removeStateFiles) report.Add(RemoveStateFiles());

        _logger.LogInformation("원상복구 완료. 성공 여부: {Success}", report.AllSucceeded);
        return report;
    }

    // ------------------------------------------------------------- 1. 허용/상태

    private RestoreStep ClearPermits()
    {
        const string name = "Permit cleanup";
        try
        {
            var path = AppPaths.PermitStateFile;
            if (!File.Exists(path)) return RestoreStep.Skipped(name, "없음");

            File.Delete(path);
            return RestoreStep.Ok(name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "일시 허용 상태 파일을 지우지 못했습니다.");
            return RestoreStep.Failed(name, ex.GetType().Name,
                $"{AppPaths.PermitStateFile} 파일을 직접 삭제하세요.");
        }
    }

    // ------------------------------------------------------------- 2. DNS 복원

    private async Task<RestoreStep> RestoreDnsAsync(CancellationToken ct)
    {
        const string name = "DNS restore";
        try
        {
            if (!_adapters.HasSavedOriginal)
            {
                return RestoreStep.Skipped(name, "변경된 어댑터 없음");
            }

            var names = string.Join(", ", _adapters.ConfiguredAdapterNames);
            var restored = await _adapters.RestoreOriginalAsync(ct).ConfigureAwait(false);

            // 복구가 끝났는데도 백업이 남아 있으면 일부 어댑터가 실패한 것이다.
            if (_adapters.HasSavedOriginal)
            {
                return RestoreStep.Failed(name, $"일부 어댑터 복구 실패 ({names})",
                    "네트워크 설정에서 해당 어댑터의 DNS 를 '자동으로 DNS 서버 주소 받기'로 바꾸세요.");
            }

            return RestoreStep.Ok(name, $"OK ({restored}개: {names})");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "어댑터 DNS 복구 실패");
            return RestoreStep.Failed(name, ex.GetType().Name,
                "네트워크 설정에서 DNS 를 '자동으로 DNS 서버 주소 받기'로 바꾸세요.");
        }
    }

    // ------------------------------------------------------------- 3. hosts

    private RestoreStep CleanupHosts()
    {
        const string name = "Hosts cleanup";
        try
        {
            // 마커 구간이 없으면 Clear() 가 false 를 돌려준다. 이것은 실패가 아니다.
            var hadSection = _hosts.GetManagedDomains().Count > 0;
            _hosts.Clear();

            // 실제로 지워졌는지 확인한다.
            if (_hosts.GetManagedDomains().Count > 0)
            {
                return RestoreStep.Failed(name, "관리 구간이 남아 있음",
                    @"관리자 권한으로 C:\Windows\System32\drivers\etc\hosts 를 열어 " +
                    "# TIMEBLOCKER BEGIN ~ # TIMEBLOCKER END 구간을 지우세요.");
            }

            return hadSection ? RestoreStep.Ok(name) : RestoreStep.Skipped(name, "관리 구간 없음");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "hosts 정리 실패");
            return RestoreStep.Failed(name, ex.GetType().Name,
                "관리자 권한으로 hosts 파일의 # TIMEBLOCKER 구간을 직접 지우세요.");
        }
    }

    // ------------------------------------------------------------- 4. 방화벽

    private async Task<RestoreStep> CleanupFirewallAsync(CancellationToken ct)
    {
        const string name = "Firewall cleanup";
        try
        {
            // 우리가 만든 규칙 이름만 지운다. 사용자의 다른 규칙은 건드리지 않는다.
            var ruleNames = new[] { FirewallManager.RobloxRuleName, FirewallManager.YouTubeRuleName };

            var removed = 0;
            foreach (var ruleName in ruleNames)
            {
                if (await _firewall.ClearAsync(ruleName, ct).ConfigureAwait(false)) removed++;
            }

            // 남아 있는지 확인한다.
            var remaining = new List<string>();
            foreach (var ruleName in ruleNames)
            {
                if (await _firewall.ExistsAsync(ruleName, ct).ConfigureAwait(false)) remaining.Add(ruleName);
            }

            if (remaining.Count > 0)
            {
                return RestoreStep.Failed(name, $"규칙이 남아 있음 ({string.Join(", ", remaining)})",
                    "관리자 권한으로 실행: netsh advfirewall firewall delete rule name=\"TimeBlocker_Roblox_Block\"");
            }

            return removed > 0 ? RestoreStep.Ok(name, $"OK ({removed}개 제거)") : RestoreStep.Skipped(name, "규칙 없음");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "방화벽 규칙 정리 실패");
            return RestoreStep.Failed(name, ex.GetType().Name,
                "관리자 권한으로 netsh advfirewall firewall delete rule name=\"TimeBlocker_*\" 를 실행하세요.");
        }
    }

    // ------------------------------------------------------------- 5. DNS 캐시

    private async Task<RestoreStep> FlushDnsCacheAsync(CancellationToken ct)
    {
        const string name = "DNS cache flush";
        try
        {
            await _dnsCache.FlushAsync(ct).ConfigureAwait(false);
            return RestoreStep.Ok(name);
        }
        catch (Exception ex)
        {
            // 캐시를 못 비워도 잠시 후 자연히 만료된다. 치명적이지 않다.
            _logger.LogWarning(ex, "DNS 캐시 비우기 실패");
            return RestoreStep.Failed(name, ex.GetType().Name, "ipconfig /flushdns 를 직접 실행하세요.");
        }
    }

    // ------------------------------------------------------------- 6. 상태파일

    private RestoreStep RemoveStateFiles()
    {
        const string name = "State cleanup";
        try
        {
            var stateDirectory = Path.Combine(AppPaths.RootDirectory, "state");
            if (!Directory.Exists(stateDirectory)) return RestoreStep.Skipped(name, "없음");

            // 어댑터 백업이 아직 남아 있으면 복구가 끝나지 않은 것이므로 지우지 않는다.
            if (_adapters.HasSavedOriginal)
            {
                return RestoreStep.Failed(name, "어댑터 DNS 백업이 남아 있어 보존함",
                    "DNS 복구를 먼저 끝낸 뒤 다시 실행하세요. (dns-restore)");
            }

            Directory.Delete(stateDirectory, recursive: true);
            return RestoreStep.Ok(name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "상태 폴더 정리 실패");
            return RestoreStep.Failed(name, ex.GetType().Name,
                $"{Path.Combine(AppPaths.RootDirectory, "state")} 폴더를 직접 삭제하세요.");
        }
    }
}
