using System.Text;

namespace TimeBlocker.Shared.Models;

/// <summary>원상복구 단계 하나의 결과.</summary>
public sealed class RestoreStep
{
    public required string Name { get; init; }

    public required bool Success { get; init; }

    /// <summary>"OK", "SKIPPED (없음)", "FAILED: ..." 같은 짧은 결과 문구.</summary>
    public required string Result { get; init; }

    /// <summary>실패 시 사용자가 직접 할 수 있는 조치.</summary>
    public string? Remedy { get; init; }

    public static RestoreStep Ok(string name, string result = "OK") =>
        new() { Name = name, Success = true, Result = result };

    public static RestoreStep Skipped(string name, string reason) =>
        new() { Name = name, Success = true, Result = $"SKIPPED ({reason})" };

    public static RestoreStep Failed(string name, string reason, string? remedy = null) =>
        new() { Name = name, Success = false, Result = $"FAILED: {reason}", Remedy = remedy };
}

/// <summary>
/// cleanup / uninstall 공통 결과.
///
/// 중요: 어느 단계가 실패해도 나머지 단계는 계속 수행한다.
/// DNS 복구가 실패했다고 hosts 정리를 건너뛰면 PC 가 더 이상한 상태로 남는다.
/// </summary>
public sealed class RestoreReport
{
    public List<RestoreStep> Steps { get; } = new();

    public bool AllSucceeded => Steps.All(s => s.Success);

    public RestoreReport Add(RestoreStep step)
    {
        Steps.Add(step);
        return this;
    }

    public string Format()
    {
        var builder = new StringBuilder();

        var width = Steps.Count == 0 ? 18 : Math.Max(18, Steps.Max(s => s.Name.Length));
        foreach (var step in Steps)
        {
            builder.AppendLine($"{step.Name.PadRight(width)}: {step.Result}");
        }

        var failed = Steps.Where(s => !s.Success).ToList();
        if (failed.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("조치 방법:");
            foreach (var step in failed.Where(s => !string.IsNullOrWhiteSpace(s.Remedy)))
            {
                builder.AppendLine($"- {step.Name}: {step.Remedy}");
            }
        }

        builder.AppendLine();
        builder.Append(AllSucceeded
            ? "System restored successfully."
            : $"System partially restored. {failed.Count} step(s) failed - 위 조치를 확인하세요.");

        return builder.ToString();
    }
}
