using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TimeBlocker.Service;
using TimeBlocker.Service.Logging;
using TimeBlocker.Shared.Configuration;
using Xunit;

namespace TimeBlocker.Tests;

/// <summary>
/// 실제 서비스가 쓰는 DI 등록을 그대로 조립해 본다.
/// 순환 의존성은 예외가 아니라 "멈춤"으로 나타난다. (.NET 8 컨테이너가 깊은 재귀를 새 스레드로
/// 넘기는데, 그 스레드가 원래 스레드가 쥔 잠금을 기다리며 교착된다.)
/// 서비스가 Running 인데 아무것도 안 하는 좀비가 되므로, 시간 제한을 두고 확인한다.
/// </summary>
public class ServiceCompositionTests : IAsyncLifetime
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "tb-composition-" + Guid.NewGuid().ToString("N"));
    private readonly FileLogWriter _logWriter;
    private readonly ServiceCollection _services = new();
    private readonly ServiceProvider _provider;

    public ServiceCompositionTests()
    {
        Directory.CreateDirectory(_tempDir);
        _logWriter = new FileLogWriter(Path.Combine(_tempDir, "logs"), 10, 3);

        _services.AddLogging();
        _services.AddSingleton<IConfigurationStore>(new JsonConfigurationStore(Path.Combine(_tempDir, "config.json")));
        _services.AddSingleton(_logWriter);

        typeof(Program)
            .GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { _services });

        _provider = _services.BuildServiceProvider();
    }

    private static T ResolveWithinLimit<T>(Func<T> resolve)
    {
        var task = Task.Run(resolve);
        Assert.True(task.Wait(Limit), "DI 해석이 멈췄습니다. 순환 의존성이 있는지 확인하세요.");
        return task.Result;
    }

    [Fact]
    public void HostedServices_CanBeResolved_WithoutHanging()
    {
        var hosted = ResolveWithinLimit(() => _provider.GetServices<IHostedService>().ToList());

        Assert.NotEmpty(hosted);
    }

    [Fact]
    public void EveryRegisteredService_CanBeResolved_WithoutHanging()
    {
        var types = _services
            .Where(d => !d.ServiceType.IsGenericTypeDefinition)
            .Select(d => d.ServiceType)
            .Distinct()
            .ToList();

        foreach (var type in types)
        {
            var resolved = ResolveWithinLimit(() => _provider.GetService(type));
            Assert.NotNull(resolved);
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        _logWriter.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* 임시 폴더 */ }
    }
}
