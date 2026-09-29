using TimeBlocker.Shared.Models;

namespace TimeBlocker.Shared.Remote;

/// <summary>
/// 원격(Telegram) / 로컬 CLI 에서 들어올 수 있는 명령 종류.
/// alias(s, yt, rb, sch ...)는 파서에서 모두 이 타입으로 변환된다.
/// </summary>
public enum RemoteCommandType
{
    Unknown,

    // 조회
    Status,
    Targets,
    ShowSchedule,
    ShowDomains,
    ShowMaxPermit,
    AdminList,
    DnsStatus,
    DnsTest,
    Doctor,
    Ping,
    Version,
    Help,

    // 일시 허용 / 차단
    Permit,

    /// <summary>대상의 상태를 auto / block / unblock 중 하나로 바꾼다.</summary>
    SetMode,

    // 설정 변경
    SetSchedule,
    SetDefaultSchedule,
    AddDomain,
    RemoveDomain,
    SetMaxPermit,
    Reload,

    /// <summary>어댑터 DNS 를 저장된 원래 설정으로 즉시 되돌린다.</summary>
    DnsRestore
}

/// <summary>
/// 파싱 결과. 파싱과 실행을 분리하기 위해 "무엇을 해야 하는가"만 담는다.
/// 실제 상태 변경은 RemoteCommandHandler 가 수행한다.
/// </summary>
public sealed class RemoteCommand
{
    public RemoteCommandType Type { get; init; } = RemoteCommandType.Unknown;

    /// <summary>Permit / SetMode / Domain 명령의 대상. null 이면 전체.</summary>
    public BlockTarget? Target { get; init; }

    /// <summary>SetMode 로 바꿀 상태.</summary>
    public BlockMode? Mode { get; init; }

    /// <summary>Permit 의 분.</summary>
    public int Minutes { get; init; }

    /// <summary>SetSchedule 대상 요일. SetDefaultSchedule 은 7요일 전체.</summary>
    public IReadOnlyList<DayOfWeek> Days { get; init; } = Array.Empty<DayOfWeek>();

    /// <summary>SetSchedule 시작 시각. Off 인 경우 null.</summary>
    public TimeSpan? Start { get; init; }

    /// <summary>SetSchedule 종료 시각. Off 인 경우 null.</summary>
    public TimeSpan? End { get; init; }

    /// <summary>schedule sat off 처럼 해당 요일을 제한 없음으로 만드는 경우 true.</summary>
    public bool TurnOff { get; init; }

    /// <summary>AddDomain / RemoveDomain 의 도메인.</summary>
    public string? Domain { get; init; }

    /// <summary>SetMaxPermit 의 값(분).</summary>
    public int Value { get; init; }

    /// <summary>원본 입력 문자열. 감사 로그에 남긴다.</summary>
    public string RawText { get; init; } = string.Empty;

    /// <summary>Unknown 일 때 사용자에게 돌려줄 오류/사용법 문구.</summary>
    public string? Error { get; init; }

    /// <summary>설정 파일을 변경하는 명령인지. (감사 로그 및 저장 여부 판단)</summary>
    public bool IsConfigChanging => Type is
        RemoteCommandType.SetSchedule or
        RemoteCommandType.SetDefaultSchedule or
        RemoteCommandType.SetMode or
        RemoteCommandType.AddDomain or
        RemoteCommandType.RemoveDomain or
        RemoteCommandType.SetMaxPermit;

    public static RemoteCommand Invalid(string raw, string error) =>
        new() { Type = RemoteCommandType.Unknown, RawText = raw, Error = error };
}
