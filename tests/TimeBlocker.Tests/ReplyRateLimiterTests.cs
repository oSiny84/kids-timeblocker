using TimeBlocker.Service.Ipc;
using TimeBlocker.Shared.Common;
using TimeBlocker.Shared.Notifications;
using Xunit;

namespace TimeBlocker.Tests;

/// <summary>
/// 알림 파이프는 아이 계정도 연결할 수 있으므로, 답장으로 관리자에게
/// 도배할 수 없어야 한다.
/// </summary>
public class ReplyRateLimiterTests
{
    private readonly FixedClock _clock = new(new DateTime(2026, 9, 22, 21, 0, 0, DateTimeKind.Local));
    private readonly ReplyRateLimiter _sut;

    public ReplyRateLimiterTests()
    {
        _sut = new ReplyRateLimiter(_clock);
    }

    [Fact]
    public void FirstReply_IsAllowed()
    {
        Assert.True(_sut.TryConsume(out var reason));
        Assert.Empty(reason);
    }

    [Fact]
    public void SecondReplyWithinCooldown_IsBlocked()
    {
        _sut.TryConsume(out _);

        Assert.False(_sut.TryConsume(out var reason));
        Assert.Contains("쿨다운", reason);
    }

    [Fact]
    public void ReplyAfterCooldown_IsAllowedAgain()
    {
        _sut.TryConsume(out _);

        _clock.Advance(TimeSpan.FromSeconds(NotifierProtocol.ReplyCooldownSeconds + 1));

        Assert.True(_sut.TryConsume(out _));
    }

    [Fact]
    public void BlockedAttempt_DoesNotConsumeQuota()
    {
        _sut.TryConsume(out _);

        // 쿨다운에 막힌 시도는 시간당 한도를 깎으면 안 된다.
        for (var i = 0; i < 50; i++) _sut.TryConsume(out _);

        _clock.Advance(TimeSpan.FromSeconds(NotifierProtocol.ReplyCooldownSeconds + 1));
        Assert.True(_sut.TryConsume(out _));
    }

    [Fact]
    public void HourlyLimit_IsEnforced()
    {
        for (var i = 0; i < NotifierProtocol.ReplyPerHourLimit; i++)
        {
            Assert.True(_sut.TryConsume(out _), $"{i + 1}번째가 막혔습니다.");
            _clock.Advance(TimeSpan.FromSeconds(NotifierProtocol.ReplyCooldownSeconds + 1));
        }

        Assert.False(_sut.TryConsume(out var reason));
        Assert.Contains("시간당", reason);
    }

    [Fact]
    public void HourlyLimit_RecoversAfterAnHour()
    {
        for (var i = 0; i < NotifierProtocol.ReplyPerHourLimit; i++)
        {
            _sut.TryConsume(out _);
            _clock.Advance(TimeSpan.FromSeconds(NotifierProtocol.ReplyCooldownSeconds + 1));
        }

        Assert.False(_sut.TryConsume(out _));

        _clock.Advance(TimeSpan.FromHours(1));
        Assert.True(_sut.TryConsume(out _));
    }

    [Theory]
    [InlineData("안녕", "안녕")]
    [InlineData("두\n줄", "두 줄")]
    [InlineData("캐리지\r리턴", "캐리지 리턴")]
    [InlineData("  공백  ", "공백")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    public void Sanitize_RemovesLineBreaks(string? input, string expected)
    {
        // 줄바꿈이 남으면 로그 한 줄을 위조하거나 메시지 형식을 깨뜨릴 수 있다.
        Assert.Equal(expected, ReplyRateLimiter.Sanitize(input));
    }

    [Fact]
    public void Truncate_LeavesShortTextAlone()
    {
        Assert.Equal("짧은 글", ReplyRateLimiter.Truncate("짧은 글"));
    }

    [Fact]
    public void Truncate_CutsLongText()
    {
        var result = ReplyRateLimiter.Truncate(new string('가', NotifierProtocol.MaxReplyLength + 100));

        Assert.Equal(NotifierProtocol.MaxReplyLength + 3, result.Length);
        Assert.EndsWith("...", result);
    }
}
