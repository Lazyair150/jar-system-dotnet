using System;
using JarSystem.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// 宿主侧的装配入口（P5）：把核心注册进 DI 容器。
///
/// 边界（刻意如此）：
/// <list type="bullet">
/// <item>**核心保持零依赖** —— 它不认识 DI 容器，也不认识 <c>ILogger</c>；`new JarSystem()` 依然可用；</item>
/// <item>容器、日志生态、配置读取都只出现在**宿主层**（见 <see cref="Program"/> 的 <c>AddJarSystem()</c>）；</item>
/// <item>核心的可观测性出口仍是自定义的 <see cref="ISystemLog"/>，由 <see cref="LoggerSystemLog"/> 适配到宿主的 <c>ILogger</c>。</item>
/// </list>
/// </summary>
public static class JarSystemHosting
{
    /// <summary>注册核心与组合根；日志出口用宿主的 <c>ILogger</c> 承载。</summary>
    public static IServiceCollection AddJarSystem(this IServiceCollection services)
    {
        services.AddSingleton<ISystemLog>(sp => new LoggerSystemLog(sp.GetRequiredService<ILogger<JarSystemService>>()));
        services.AddSingleton<JarSystemService>();
        return services;
    }
}

/// <summary>
/// 把宿主生态的 <c>ILogger</c> 适配成核心的 <see cref="ISystemLog"/>。
/// 这样核心既能保持零依赖，又能把诊断送进结构化日志（级别过滤、Provider、将来的采集器）。
/// 约定（见 <see cref="ISystemLog"/>）：实现方**不得抛异常** —— <c>ILogger</c> 实现本身即满足该约定。
/// </summary>
public sealed class LoggerSystemLog : ISystemLog
{
    private readonly ILogger _logger;

    public LoggerSystemLog(ILogger logger)
    {
        _logger = logger;
    }

    public void Error(string message, Exception? ex = null) => _logger.LogError(ex, "{Message}", message);

    public void Info(string message) => _logger.LogInformation("{Message}", message);
}
