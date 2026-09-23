using System.Text;

namespace TimeBlocker.Shared.Models;

public enum DoctorStatus
{
    Pass,
    Warn,
    Fail
}

/// <summary>점검 항목 하나의 결과.</summary>
public sealed class DoctorCheck
{
    public required string Name { get; init; }

    public required DoctorStatus Status { get; init; }

    /// <summary>항목 옆에 붙는 짧은 설명. 예: "127.0.0.1:53"</summary>
    public string? Detail { get; init; }

    /// <summary>WARN / FAIL 일 때 사용자가 할 수 있는 조치. 한 줄로 짧게.</summary>
    public string? Remedy { get; init; }

    public static DoctorCheck Pass(string name, string? detail = null) =>
        new() { Name = name, Status = DoctorStatus.Pass, Detail = detail };

    public static DoctorCheck Warn(string name, string? detail = null, string? remedy = null) =>
        new() { Name = name, Status = DoctorStatus.Warn, Detail = detail, Remedy = remedy };

    public static DoctorCheck Fail(string name, string? detail = null, string? remedy = null) =>
        new() { Name = name, Status = DoctorStatus.Fail, Detail = detail, Remedy = remedy };
}

/// <summary>doctor 명령의 전체 결과.</summary>
public sealed class DoctorReport
{
    public List<DoctorCheck> Checks { get; } = new();

    public int PassCount => Checks.Count(c => c.Status == DoctorStatus.Pass);
    public int WarnCount => Checks.Count(c => c.Status == DoctorStatus.Warn);
    public int FailCount => Checks.Count(c => c.Status == DoctorStatus.Fail);

    /// <summary>하나라도 FAIL 이면 FAIL, FAIL 없이 WARN 만 있으면 WARN, 전부 통과면 PASS.</summary>
    public DoctorStatus Overall =>
        FailCount > 0 ? DoctorStatus.Fail :
        WarnCount > 0 ? DoctorStatus.Warn :
        DoctorStatus.Pass;

    public DoctorReport Add(DoctorCheck check)
    {
        Checks.Add(check);
        return this;
    }

    /// <summary>
    /// 사람이 읽는 형식으로 출력한다. Telegram 메시지로도 쓰이므로 너무 길지 않게 만든다.
    /// 조치 안내는 문제가 있는 항목에 대해서만 마지막에 모아서 보여준다.
    /// </summary>
    public string Format()
    {
        var builder = new StringBuilder();
        builder.AppendLine("TimeBlocker Doctor");
        builder.AppendLine();

        foreach (var check in Checks)
        {
            var tag = check.Status switch
            {
                DoctorStatus.Pass => "[PASS]",
                DoctorStatus.Warn => "[WARN]",
                _ => "[FAIL]"
            };

            builder.Append(tag).Append(' ').Append(check.Name);
            if (!string.IsNullOrWhiteSpace(check.Detail)) builder.Append(" : ").Append(check.Detail);
            builder.AppendLine();
        }

        // 조치가 필요한 항목만 따로 모아 준다.
        var actionable = Checks
            .Where(c => c.Status != DoctorStatus.Pass && !string.IsNullOrWhiteSpace(c.Remedy))
            .ToList();

        if (actionable.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("조치 방법:");
            foreach (var check in actionable)
            {
                builder.AppendLine($"- {check.Name}: {check.Remedy}");
            }
        }

        builder.AppendLine();
        builder.Append($"Result : {Overall.ToString().ToUpperInvariant()}");

        var notes = new List<string>();
        if (WarnCount > 0) notes.Add($"{WarnCount} warning{(WarnCount > 1 ? "s" : "")}");
        if (FailCount > 0) notes.Add($"{FailCount} failure{(FailCount > 1 ? "s" : "")}");
        if (notes.Count > 0) builder.Append($" ({string.Join(", ", notes)})");

        return builder.ToString();
    }
}

/// <summary>doctor 점검을 수행하는 쪽. Shared 는 Windows 구현을 알 필요가 없으므로 인터페이스만 둔다.</summary>
public interface IDiagnosticsService
{
    Task<DoctorReport> RunAsync(CancellationToken cancellationToken = default);
}
