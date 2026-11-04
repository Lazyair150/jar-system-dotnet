using System;
using System.IO;
using Xunit;

namespace JarSystem.Tests;

/// <summary>浏览器表现组件也必须守住职责边界，不能重新长成“渲染 + 网络 + 规则”三合一大文件。</summary>
public class FrontendArchitectureTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JarSystem.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void ProductionViewerIsPresentationOnly()
    {
        var path = Path.Combine(RepoRoot(), "src", "JarSystem.Web", "wwwroot", "js", "production-view.js");
        var source = File.ReadAllText(path);

        Assert.DoesNotContain("WebSocket", source);
        Assert.DoesNotContain("fetch(", source);
        Assert.DoesNotContain("send(", source);
        Assert.DoesNotContain("/ws", source);
        Assert.Contains("global.JarProductionView", source);
    }

    [Fact]
    public void GamePageLoadsProductionViewerBeforeInteractionController()
    {
        var path = Path.Combine(RepoRoot(), "src", "JarSystem.Web", "wwwroot", "play.html");
        var html = File.ReadAllText(path);

        var viewer = html.IndexOf("production-view.js", StringComparison.Ordinal);
        var controller = html.IndexOf("play.js", StringComparison.Ordinal);
        Assert.True(viewer >= 0, "play.html 没有加载 production-view.js");
        Assert.True(controller > viewer, "production-view.js 必须先于 play.js 加载");
    }

    [Fact]
    public void LocalUiActionsStayInsideTheBrowser()
    {
        var root = RepoRoot();
        var path = Path.Combine(root, "src", "JarSystem.Web", "wwwroot", "js", "play.js");
        var source = File.ReadAllText(path);
        var start = source.IndexOf("function handleLocalUiAction", StringComparison.Ordinal);
        var end = source.IndexOf("/* ---------------- 事件", start, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start, "play.js 必须保留独立的纯本地动作处理函数");
        var localHandler = source[start..end];
        Assert.DoesNotContain("send(", localHandler);
        Assert.DoesNotContain("fetch(", localHandler);
        Assert.Contains("LOCAL_UI_ACTIONS[act]", source);
    }

    [Fact]
    public void ProtocolGuideDocumentsTheLocalCommandBoundary()
    {
        var path = Path.Combine(RepoRoot(), "docs", "前后端交互协议.md");
        var guide = File.ReadAllText(path);

        Assert.Contains("纯表现状态", guide);
        Assert.Contains("世界命令", guide);
        Assert.Contains("`state` 是权威", guide);
        Assert.Contains("不得发送 WebSocket 消息", guide);
    }
}
