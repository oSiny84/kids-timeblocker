using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TimeBlocker.Service.Blocking;
using TimeBlocker.Service.Blocking.Dns;
using Xunit;

namespace TimeBlocker.Tests;

/// <summary>
/// hosts 파일 조작과 DNS 메시지 처리는 실수하면 PC 네트워크를 망가뜨릴 수 있으므로
/// 실제 시스템 파일 대신 임시 파일을 대상으로 검증한다.
/// </summary>
public class HostsFileManagerTests : IDisposable
{
    private readonly string _directory;
    private readonly string _hostsPath;
    private readonly HostsFileManager _sut;

    private const string ExistingContent =
        """
        # Copyright (c) 1993-2009 Microsoft Corp.
        127.0.0.1       localhost
        10.0.0.5        my-nas
        """;

    public HostsFileManagerTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "TimeBlockerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _hostsPath = Path.Combine(_directory, "hosts");
        File.WriteAllText(_hostsPath, ExistingContent);

        _sut = new HostsFileManager(NullLogger<HostsFileManager>.Instance, _hostsPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* 무시 */ }
    }

    private string ReadHosts() => File.ReadAllText(_hostsPath);

    [Fact]
    public void Apply_PreservesExistingEntries()
    {
        _sut.Apply(new[] { "youtube.com", "www.youtube.com" });

        var content = ReadHosts();

        Assert.Contains("127.0.0.1       localhost", content);
        Assert.Contains("10.0.0.5        my-nas", content);
        Assert.Contains("# Copyright", content);
    }

    [Fact]
    public void Apply_WritesManagedSectionWithMarkers()
    {
        _sut.Apply(new[] { "youtube.com" });

        var content = ReadHosts();

        Assert.Contains("# TIMEBLOCKER BEGIN", content);
        Assert.Contains("# TIMEBLOCKER END", content);
        Assert.Contains("0.0.0.0 youtube.com", content);
        Assert.Contains("::1 youtube.com", content);
    }

    [Fact]
    public void Clear_RemovesOnlyManagedSection()
    {
        _sut.Apply(new[] { "youtube.com", "roblox.com" });
        _sut.Clear();

        var content = ReadHosts();

        Assert.DoesNotContain("TIMEBLOCKER", content);
        Assert.DoesNotContain("youtube.com", content);
        Assert.Contains("127.0.0.1       localhost", content);
        Assert.Contains("10.0.0.5        my-nas", content);
    }

    [Fact]
    public void Apply_Twice_DoesNotDuplicateSection()
    {
        _sut.Apply(new[] { "youtube.com" });
        _sut.Apply(new[] { "youtube.com", "roblox.com" });

        var content = ReadHosts();

        Assert.Equal(1, CountOccurrences(content, "# TIMEBLOCKER BEGIN"));
        Assert.Equal(1, CountOccurrences(content, "# TIMEBLOCKER END"));
        Assert.Contains("0.0.0.0 roblox.com", content);
    }

    [Fact]
    public void Apply_SameContentTwice_ReportsNoChange()
    {
        Assert.True(_sut.Apply(new[] { "youtube.com" }));

        // 같은 내용이면 파일을 다시 쓰지 않아야 한다. (10초마다 hosts 를 건드리면 안 된다)
        Assert.False(_sut.Apply(new[] { "youtube.com" }));
    }

    [Fact]
    public void Apply_EmptyList_ClearsSection()
    {
        _sut.Apply(new[] { "youtube.com" });
        _sut.Apply(Array.Empty<string>());

        Assert.DoesNotContain("TIMEBLOCKER", ReadHosts());
    }

    [Fact]
    public void GetManagedDomains_ReturnsWhatWasApplied()
    {
        _sut.Apply(new[] { "youtube.com", "roblox.com" });

        var domains = _sut.GetManagedDomains();

        Assert.Contains("youtube.com", domains);
        Assert.Contains("roblox.com", domains);
        Assert.Equal(2, domains.Count);
    }

    [Fact]
    public void RecoversFromMissingEndMarker()
    {
        // BEGIN 만 있고 END 가 없는 손상된 파일
        File.WriteAllText(_hostsPath, ExistingContent + "\n# TIMEBLOCKER BEGIN\n0.0.0.0 old.com\n");

        _sut.Apply(new[] { "youtube.com" });
        var content = ReadHosts();

        Assert.DoesNotContain("old.com", content);
        Assert.Contains("0.0.0.0 youtube.com", content);
        Assert.Equal(1, CountOccurrences(content, "# TIMEBLOCKER BEGIN"));
    }

    [Fact]
    public void Clear_OnFileWithoutSection_IsNoOp()
    {
        Assert.False(_sut.Clear());
        Assert.Contains("127.0.0.1       localhost", ReadHosts());
    }

    [Fact]
    public void RepeatedWriteFailure_IsLoggedOnceNotEveryCycle()
    {
        // hosts 경로가 폴더면 쓰기가 항상 실패한다. (권한 문제/백신 잠금과 같은 상황)
        var lockedPath = Path.Combine(_directory, "locked");
        Directory.CreateDirectory(lockedPath);

        var logger = new CountingLogger<HostsFileManager>();
        var manager = new HostsFileManager(logger, lockedPath);

        // 10초 주기로 6번 돌았다고 가정
        for (var i = 0; i < 6; i++)
        {
            Assert.False(manager.Apply(new[] { "youtube.com" }));
        }

        // 같은 오류가 반복돼도 ERROR 는 한 번만 남아야 한다.
        Assert.Equal(1, logger.CountOf(LogLevel.Error));
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}

public class DnsMessageTests
{
    /// <summary>"www.youtube.com" A 질의 패킷을 만든다.</summary>
    private static byte[] BuildQuery(string name, ushort id = 0x1234)
    {
        var bytes = new List<byte>
        {
            (byte)(id >> 8), (byte)(id & 0xFF),
            0x01, 0x00,             // flags: 표준 질의, RD=1
            0x00, 0x01,             // QDCOUNT = 1
            0x00, 0x00,             // ANCOUNT
            0x00, 0x00,             // NSCOUNT
            0x00, 0x00              // ARCOUNT
        };

        foreach (var label in name.Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }
        bytes.Add(0x00);            // 이름 끝
        bytes.AddRange(new byte[] { 0x00, 0x01 });  // QTYPE = A
        bytes.AddRange(new byte[] { 0x00, 0x01 });  // QCLASS = IN

        return bytes.ToArray();
    }

    [Fact]
    public void ReadsQuestionName()
    {
        Assert.Equal("www.youtube.com", DnsMessage.TryReadQuestionName(BuildQuery("www.youtube.com")));
    }

    [Fact]
    public void ReadsQuestionName_LowercasesResult()
    {
        Assert.Equal("www.youtube.com", DnsMessage.TryReadQuestionName(BuildQuery("WWW.YouTube.COM")));
    }

    [Fact]
    public void ReadsQuestionName_ReturnsNullForGarbage()
    {
        Assert.Null(DnsMessage.TryReadQuestionName(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void BuildsNxDomainResponse_KeepsIdAndSetsRcode3()
    {
        var query = BuildQuery("www.youtube.com", id: 0xABCD);

        var response = DnsMessage.TryBuildNxDomainResponse(query);

        Assert.NotNull(response);
        Assert.Equal(0xAB, response![0]);
        Assert.Equal(0xCD, response[1]);
        Assert.Equal(0x80, response[2] & 0x80);         // QR = 1 (응답)
        Assert.Equal(3, response[3] & 0x0F);            // RCODE = 3 (NXDOMAIN)
        Assert.Equal(0, (response[6] << 8) | response[7]); // ANCOUNT = 0

        // 질의 섹션이 그대로 유지되어야 한다.
        Assert.Equal("www.youtube.com", DnsMessage.TryReadQuestionName(response));
    }

    [Fact]
    public void BuildsServerFailureResponse()
    {
        var response = DnsMessage.TryBuildServerFailureResponse(BuildQuery("example.com"));

        Assert.NotNull(response);
        Assert.Equal(2, response![3] & 0x0F);  // RCODE = 2 (SERVFAIL)
    }

    [Theory]
    [InlineData("youtube.com", true)]
    [InlineData("www.youtube.com", true)]                      // 하위 도메인
    [InlineData("rr1---sn-ab5l6nz7.googlevideo.com", true)]     // 동적 CDN 호스트
    [InlineData("a.b.c.googlevideo.com", true)]
    [InlineData("notyoutube.com", false)]                       // 부분 문자열은 차단 아님
    [InlineData("myyoutube.com", false)]
    [InlineData("google.com", false)]
    [InlineData("example.com", false)]
    public void IsBlocked_MatchesDomainAndSubdomains(string question, bool expected)
    {
        var blocked = new HashSet<string> { "youtube.com", "googlevideo.com" };

        Assert.Equal(expected, DnsMessage.IsBlocked(question, blocked));
    }

    [Fact]
    public void IsBlocked_EmptyListNeverBlocks()
    {
        Assert.False(DnsMessage.IsBlocked("youtube.com", new HashSet<string>()));
    }

    [Fact]
    public void IsBlocked_IgnoresTrailingDot()
    {
        var blocked = new HashSet<string> { "youtube.com" };
        Assert.True(DnsMessage.IsBlocked("www.youtube.com.", blocked));
    }

    // ------------------------------------------------- 요청된 판정 시나리오

    /// <summary>
    /// 기본 설정의 YouTube 도메인 목록으로 프록시가 판정할 때 기대되는 결과.
    /// Proxy 모드에서는 하위 도메인까지 막혀야 하고, 관계없는 도메인은 통과해야 한다.
    /// </summary>
    public static TheoryData<string, bool> ProxyScenario => new()
    {
        { "youtube.com", true },
        { "www.youtube.com", true },
        { "music.youtube.com", true },
        { "googlevideo.com", true },
        { "rr1---sn-test.googlevideo.com", true },
        { "notgooglevideo.com", false },
        { "myyoutube.com", false }
    };

    [Theory]
    [MemberData(nameof(ProxyScenario))]
    public void ProxyBlocking_RequiredScenario(string question, bool expectedBlocked)
    {
        // 기본 설정의 YouTube 도메인 목록을 그대로 사용한다.
        var config = Shared.Configuration.TimeBlockerConfig.CreateDefault();
        var blocked = new HashSet<string>(config.YouTube.Domains.Select(DnsMessage.Normalize), StringComparer.Ordinal);

        Assert.Equal(expectedBlocked, DnsMessage.IsBlocked(question, blocked));
    }

    [Theory]
    [MemberData(nameof(ProxyScenario))]
    public void RequiredDomainFamilies_AreCoveredBySubdomainRule(string question, bool expectedBlocked)
    {
        // 사양에서 요구한 최소 도메인 계열만으로도 같은 결과가 나와야 한다.
        var families = new[]
        {
            "youtube.com",
            "youtu.be",
            "googlevideo.com",
            "ytimg.com",
            "youtubei.googleapis.com"
        };

        var anyMatch = families.Any(family => DnsMessage.Matches(question, family));
        Assert.Equal(expectedBlocked, anyMatch);
    }

    // --------------------------------------------------- Matches 규칙 자체

    [Theory]
    [InlineData("googlevideo.com", "googlevideo.com", true)]                  // 정확히 일치
    [InlineData("abc.googlevideo.com", "googlevideo.com", true)]              // 하위 도메인
    [InlineData("rr1---sn-xxxx.googlevideo.com", "googlevideo.com", true)]
    [InlineData("rr2---sn-xxxx.googlevideo.com", "googlevideo.com", true)]
    [InlineData("a.b.c.googlevideo.com", "googlevideo.com", true)]
    [InlineData("notgooglevideo.com", "googlevideo.com", false)]              // 앞에 점이 없다
    [InlineData("googlevideo.com.evil.com", "googlevideo.com", false)]        // 접미사가 아니다
    [InlineData("myyoutube.com", "youtube.com", false)]
    [InlineData("music.youtube.com", "youtube.com", true)]
    [InlineData("youtu.be", "youtu.be", true)]
    [InlineData("notyoutu.be", "youtu.be", false)]
    public void Matches_FollowsEqualsOrDotSuffixRule(string question, string blocked, bool expected)
    {
        Assert.Equal(expected, DnsMessage.Matches(question, blocked));
    }

    [Fact]
    public void Matches_IsCaseAndTrailingDotInsensitive()
    {
        Assert.True(DnsMessage.Matches("RR1---SN-X.GoogleVideo.COM.", "googlevideo.com"));
    }

    /// <summary>
    /// IsBlocked(집합 버전)와 Matches(단일 도메인 규칙)가 항상 같은 답을 내야 한다.
    /// 집합 버전은 성능을 위해 상위 도메인을 훑는 방식이라 규칙이 어긋나지 않는지 확인한다.
    /// </summary>
    [Fact]
    public void IsBlocked_AgreesWithMatchesRule()
    {
        var blockedList = new[] { "youtube.com", "googlevideo.com", "youtubei.googleapis.com", "youtu.be" };
        var blockedSet = new HashSet<string>(blockedList, StringComparer.Ordinal);

        var questions = new[]
        {
            "youtube.com", "www.youtube.com", "music.youtube.com", "m.youtube.com",
            "googlevideo.com", "rr1---sn-test.googlevideo.com", "a.b.googlevideo.com",
            "youtubei.googleapis.com", "x.youtubei.googleapis.com", "googleapis.com",
            "youtu.be", "notyoutu.be",
            "notgooglevideo.com", "myyoutube.com", "youtube.com.evil.com",
            "example.com", "roblox.com"
        };

        foreach (var question in questions)
        {
            var viaSet = DnsMessage.IsBlocked(question, blockedSet);
            var viaRule = blockedList.Any(b => DnsMessage.Matches(question, b));

            Assert.True(viaSet == viaRule, $"규칙 불일치: {question} (set={viaSet}, rule={viaRule})");
        }
    }
}

/// <summary>로그 레벨별 호출 횟수를 세는 테스트용 로거.</summary>
internal sealed class CountingLogger<T> : ILogger<T>
{
    private readonly Dictionary<LogLevel, int> _counts = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        _counts[logLevel] = _counts.GetValueOrDefault(logLevel) + 1;
    }

    public int CountOf(LogLevel level) => _counts.GetValueOrDefault(level);
}
