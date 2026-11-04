using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using JarSystem.Core.Security;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// 权限点登记表与访问策略的守卫测试。
///
/// **为什么这些测试是安全证据而不是覆盖率**：C-05 要求"新增资源类型上线前必须显式登记权限点"，
/// C-06 要求"管理接口与业务接口分离鉴权策略"。这两条都无法靠读代码保证——只要有人加一条命令、
/// 或者把某条命令的类别从 Admin 改成 BusinessWrite，规则就悄悄失效了。所以这里把
/// "登记表本身是完整的、类别是显式的、两类策略源是分开的"变成可执行断言。
/// </summary>
public class SecurityPolicyTests
{
    // ---- C-05：登记表 ----

    [Fact]
    public void EveryCommandIsRegisteredExactlyOnce()
    {
        var names = PermissionPoints.Commands.Select(c => c.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(PermissionPoints.Commands, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Name));
            Assert.False(string.IsNullOrWhiteSpace(c.Resource));
            Assert.False(string.IsNullOrWhiteSpace(c.Action));
            Assert.False(string.IsNullOrWhiteSpace(c.Why));
        });
    }

    /// <summary>
    /// 命令集合必须与 <c>CommandDispatcher</c> 的 case 分支一致。
    /// 用**显式列表**而不是反射：反射能让测试自动通过，这样"新增命令忘了登记"就不会被抓住。
    /// </summary>
    [Fact]
    public void TheRegistryMatchesTheDispatcherCases()
    {
        string[] dispatcherCases =
        {
            "getState", "createOrder", "createRandomOrder", "createWorkshop", "createRandomWorkshop",
            "updateWorkshopCapacity", "moveOrder", "setWorkshopCapability", "setMaster", "setSlave",
            "executeOrder", "deleteOrder", "startOrder", "cancelProcess", "setTickInterval", "getSteps",
            "checkFeasibility", "checkAllFeasibility", "manualTick", "reset", "restartLevel", "loadLevel",
            "updateLiquidRate", "updateProducerRate", "startTick", "stopTick", "setAutoCheck"
        };
        var registered = PermissionPoints.Commands.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(dispatcherCases.OrderBy(n => n, StringComparer.Ordinal), registered);
    }

    [Fact]
    public void EveryEndpointIsRegisteredAndTheWriterIsAdmin()
    {
        string[] endpoints =
        {
            "GET /api/state", "GET /api/levels", "GET /api/invariants", "GET /api/provenance",
            "GET /api/scenario", "POST /api/scenario/validate", "POST /api/scenario"
        };
        var registered = PermissionPoints.Endpoints.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(endpoints.OrderBy(n => n, StringComparer.Ordinal), registered);

        Assert.True(PermissionPoints.TryFindEndpoint("POST /api/scenario", out var writer));
        Assert.Equal(AccessCategory.Admin, writer.Category);
        foreach (var name in endpoints.Where(n => n != "POST /api/scenario"))
        {
            Assert.True(PermissionPoints.TryFindEndpoint(name, out var point));
            Assert.Equal(AccessCategory.BusinessRead, point.Category);
        }
    }

    [Fact]
    public void AdminCategoryIsReservedForWorldResetsAndAuthorDataWrites()
    {
        var adminCommands = PermissionPoints.Commands
            .Where(c => c.Category == AccessCategory.Admin)
            .Select(c => c.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(new[] { "loadLevel", "reset", "restartLevel" }, adminCommands);
    }

    [Fact]
    public void UnregisteredNamesAreRejectedInsteadOfDefaulting()
    {
        Assert.False(PermissionPoints.TryFindCommand("dropTable", out _));
        Assert.False(PermissionPoints.TryFindCommand("", out _));
        Assert.False(PermissionPoints.TryFindEndpoint("DELETE /api/state", out _));
    }

    // ---- C-06：两类策略源 ----

    [Fact]
    public void BusinessPointsOnlyNeedToComeFromThisMachine()
    {
        Assert.True(PermissionPoints.TryFindCommand("manualTick", out var business));
        var decision = AccessPolicy.Decide(business, new AccessFacts(true, false), adminTokenConfigured: true);
        Assert.True(decision.Allowed);

        var remote = AccessPolicy.Decide(business, new AccessFacts(false, true), adminTokenConfigured: true);
        Assert.False(remote.Allowed);
        Assert.Equal(AccessPolicy.CodeRemoteDenied, remote.Code);
    }

    [Fact]
    public void AdminPointsNeedTheTokenWhenOneIsConfigured()
    {
        Assert.True(PermissionPoints.TryFindCommand("reset", out var admin));

        var localWithoutToken = AccessPolicy.Decide(admin, new AccessFacts(true, false), adminTokenConfigured: true);
        Assert.False(localWithoutToken.Allowed);
        Assert.Equal(AccessPolicy.CodeAdminTokenRequired, localWithoutToken.Code);

        var localWithToken = AccessPolicy.Decide(admin, new AccessFacts(true, true), adminTokenConfigured: true);
        Assert.True(localWithToken.Allowed);

        // 没配令牌时管理操作退化为"本机即可"：开发期零摩擦，且行为可预测（不是"配了令牌但忘带"）。
        var noTokenConfigured = AccessPolicy.Decide(admin, new AccessFacts(true, false), adminTokenConfigured: false);
        Assert.True(noTokenConfigured.Allowed);

        // 远程一律拒，哪怕出示了令牌：令牌解决的是"本机上谁在操作"，不是"允许谁从外面连进来"。
        var remote = AccessPolicy.Decide(admin, new AccessFacts(false, true), adminTokenConfigured: true);
        Assert.False(remote.Allowed);
        Assert.Equal(AccessPolicy.CodeRemoteDenied, remote.Code);
    }

    [Fact]
    public void EveryDeniedDecisionCarriesAMachineReadableCode()
    {
        Assert.False(string.IsNullOrWhiteSpace(AccessPolicy.CodeRemoteDenied));
        Assert.False(string.IsNullOrWhiteSpace(AccessPolicy.CodeAdminTokenRequired));
        foreach (var point in PermissionPoints.Commands.Concat(PermissionPoints.Endpoints))
        {
            var decision = AccessPolicy.Decide(point, new AccessFacts(false, false), adminTokenConfigured: true);
            Assert.False(decision.Allowed);
            Assert.Equal(AccessPolicy.CodeRemoteDenied, decision.Code);
        }
    }

    // ---- G-06：低权限运行姿态 ----

    [Fact]
    public void OnlyLoopbackAddressesPass()
    {
        Assert.True(HostPolicy.IsLoopback(IPAddress.Loopback));
        Assert.True(HostPolicy.IsLoopback(IPAddress.IPv6Loopback));
        Assert.True(HostPolicy.IsLoopback(IPAddress.Parse("127.0.0.5")));
        Assert.True(HostPolicy.IsLoopback(IPAddress.Parse("::ffff:127.0.0.1")));
        Assert.False(HostPolicy.IsLoopback(IPAddress.Parse("192.168.1.10")));
        Assert.False(HostPolicy.IsLoopback(IPAddress.Any));
        Assert.False(HostPolicy.IsLoopback(null));
    }

    [Fact]
    public void RemoteConnectionsAreClosedUnlessExplicitlyAllowed()
    {
        var remote = IPAddress.Parse("10.0.0.8");
        Assert.True(HostPolicy.IsAllowedConnection(IPAddress.Loopback, allowRemote: false));
        Assert.False(HostPolicy.IsAllowedConnection(remote, allowRemote: false));
        Assert.True(HostPolicy.IsAllowedConnection(remote, allowRemote: true));
        // 放开远程不等于放开"来源不明"：null 仍然拒。
        Assert.False(HostPolicy.IsAllowedConnection(null, allowRemote: true));
    }

    [Fact]
    public void ElevatedRunsWarnButDoNotRefuse()
    {
        Assert.Null(HostPolicy.ElevationWarning(false));
        var warning = HostPolicy.ElevationWarning(true);
        Assert.False(string.IsNullOrWhiteSpace(warning));
        Assert.Contains("提权", warning);
        Assert.Contains("普通用户", warning);
    }
}
