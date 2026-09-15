using System.Net.WebSockets;
using System.Text.Json;

/// <summary>
/// WebSocket 命令分派（原 Program.cs 的 <c>HandleCmdCore</c> + <c>ResolveOrderIndex</c>）。
///
/// **对外契约**：命令名、消息名与每个 payload 的键集合都不得改动（前端 `app.js` 依赖）：
/// 命令 getState / createOrder / createRandomOrder / createWorkshop / createRandomWorkshop /
/// updateWorkshopCapacity / setMaster / setSlave / executeOrder / deleteOrder / checkFeasibility /
/// checkAllFeasibility / manualTick / reset / updateProducerRate / startTick / stopTick / setAutoCheck；
/// 消息 init / state / orderExecuted / workshopSwitched / feasibilityResult / allFeasibilityResults /
/// autoCheckResults / autoCheckChanged / error。
/// </summary>
public sealed class CommandDispatcher
{
    private readonly IWebSocketSink _sink;

    public CommandDispatcher(IWebSocketSink sink)
    {
        _sink = sink;
    }

    /// <summary>命令处理入口：单例状态的串行闸门（多个连接与定时器共享同一系统实例）。</summary>
    public async Task HandleAsync(JarSystemService svc, WebSocket ws, string type, JsonElement dataEl)
    {
        await svc.Gate.WaitAsync();
        try { await HandleCoreAsync(svc, ws, type, dataEl); }
        finally { svc.Gate.Release(); }
    }

    private async Task HandleCoreAsync(JarSystemService svc, WebSocket ws, string type, JsonElement dataEl)
    {
        var sys = svc.System;
        switch (type)
        {
            case "getState": await _sink.SendAsync(ws, JsonArgs.J(new { type = "state", data = sys.GetState() })); break;

            case "createOrder":
            {
                // 输入校验（安全）：订单类型只接受 A/B
                var orderType = JsonArgs.JS(dataEl, "orderType") ?? "A";
                if (orderType != "A" && orderType != "B")
                {
                    await _sink.SendAsync(ws, JsonArgs.J(new { type = "error", data = new { message = "订单类型只能是 A 或 B" } }));
                    break;
                }
                var reqs = JsonArgs.ParseReqs(dataEl);
                var created = sys.CreateOrder(reqs, orderType);
                if (!created.Success)
                {
                    // 形状/液体/需求数量/顺序等域校验失败时给出明确原因（此前静默忽略）
                    await _sink.SendAsync(ws, JsonArgs.J(new { type = "error", data = new { message = created.Error ?? "订单创建失败" } }));
                    break;
                }
                await _sink.SendAsync(ws, JsonArgs.J(new { type = "state", data = sys.GetState() }));
                if (sys.AutoCheckEnabled) await _sink.SendAsync(ws, JsonArgs.J(new { type = "autoCheckResults", data = sys.CheckAllFeasibility() }));
                break;
            }

            case "createRandomOrder":
                sys.CreateRandomOrder();
                if (sys.AutoCheckEnabled) await _sink.SendAsync(ws, JsonArgs.J(new { type = "autoCheckResults", data = sys.CheckAllFeasibility() }));
                await _sink.SendAsync(ws, JsonArgs.J(new { type = "state", data = sys.GetState() }));
                break;

            case "createWorkshop":
                sys.CreateWorkshop(JsonArgs.JS(dataEl, "name") ?? "", JsonArgs.JL(dataEl, "baseMarks"), JsonArgs.JL(dataEl, "additionalMarks"), JsonArgs.JL(dataEl, "ban"));
                await _sink.SendAsync(ws, JsonArgs.J(new { type = "state", data = sys.GetState() }));
                break;

            case "createRandomWorkshop":
                sys.CreateRandomWorkshop();
                await _sink.SendAsync(ws, JsonArgs.J(new { type = "state", data = sys.GetState() }));
                break;

            case "updateWorkshopCapacity":
            {
                var idx = JsonArgs.JI(dataEl, "index");
                var maxJars = JsonArgs.JIOrNull(dataEl, "maxJars");
                var maxLiquids = JsonArgs.JIOrNull(dataEl, "maxLiquids");
                // 输入校验（安全）：负容量会让 Resize*Capacity 的区间移除越界，在边界处拦掉
                if ((maxJars.HasValue && maxJars.Value < 0) || (maxLiquids.HasValue && maxLiquids.Value < 0))
                {
                    await _sink.SendAsync(ws, JsonArgs.J(new { type = "error", data = new { message = "容量不能为负数" } }));
                    break;
                }
                sys.UpdateWorkshopCapacity(idx, maxJars, maxLiquids);
                await _sink.SendAsync(ws, JsonArgs.J(new { type = "state", data = sys.GetState() }));
                break;
            }

            case "setMaster":
            {
                var newIdx = JsonArgs.JI(dataEl, "index");
                var oldIdx = sys.CurrentMaster;
                var res = sys.SetMaster(newIdx);
                await _sink.SendAsync(ws, JsonArgs.J(new { type = "state", data = sys.GetState() }));
                if (res.TryGetValue("success", out var sv) && sv is bool ok && ok &&
                    res.TryGetValue("switched", out var swv) && swv is bool switched && switched)
                {
                    await _sink.SendAsync(ws, JsonArgs.J(new
                    {
                        type = "workshopSwitched",
                        data = new
                        {
                            fromIndex = oldIdx ?? 0,
                            toIndex = newIdx,
                            caseType = res.TryGetValue("caseType", out var ctv) ? ctv as string ?? "" : "",
                            operations = res.TryGetValue("operations", out var opv) && opv is System.Collections.IList opl ? opl : new List<object>()
                        }
                    }));
                }
                else if (res.TryGetValue("success", out var sv2) && sv2 is bool ok2 && !ok2)
                {
                    await _sink.SendAsync(ws, JsonArgs.J(new { type = "error", data = new { message = res.TryGetValue("reason", out var rv) ? rv as string ?? "切换失败" : "切换失败" } }));
                }
                break;
            }

            case "setSlave":
            {
                var res = sys.SetSlave(JsonArgs.JI(dataEl, "index"));
                await _sink.SendAsync(ws, JsonArgs.J(new { type = "state", data = sys.GetState() }));
                if (res.TryGetValue("success", out var sv) && sv is bool ok && !ok)
                {
                    await _sink.SendAsync(ws, JsonArgs.J(new { type = "error", data = new { message = res.TryGetValue("reason", out var rv) ? rv as string ?? "设置从台失败" : "设置从台失败" } }));
                }
                break;
            }

            case "executeOrder":
            {
                var idx = ResolveOrderIndex(svc, dataEl);
                if (idx < 0) { await _sink.SendAsync(ws, JsonArgs.J(new { type = "error", data = new { message = "订单不存在" } })); break; }
                var r = sys.ExecuteOrder(idx);
                await _sink.SendAsync(ws, JsonArgs.J(new { type = "orderExecuted", data = new { success = r.Success, reason = r.Reason, operations = r.Operations, transferOccurred = r.TransferOccurred } }));
                await _sink.SendAsync(ws, JsonArgs.J(new { type = "state", data = sys.GetState() }));
                break;
            }

            case "deleteOrder":
            {
                var idx = ResolveOrderIndex(svc, dataEl);
                if (idx < 0) { await _sink.SendAsync(ws, JsonArgs.J(new { type = "error", data = new { message = "订单不存在" } })); break; }
                sys.DeleteOrder(idx);
                await _sink.SendAsync(ws, JsonArgs.J(new { type = "state", data = sys.GetState() }));
                break;
            }

            case "checkFeasibility":
            {
                var idx = ResolveOrderIndex(svc, dataEl);
                if (idx < 0) { await _sink.SendAsync(ws, JsonArgs.J(new { type = "error", data = new { message = "订单不存在" } })); break; }
                var result = sys.CheckFeasibility(idx);
                var orderId = result != null && idx < sys.Orders.Count ? sys.Orders[idx].Id : 0;
                await _sink.SendAsync(ws, JsonArgs.J(new { type = "feasibilityResult", data = new { orderId, result } }));
                break;
            }

            case "checkAllFeasibility":
                await _sink.SendAsync(ws, JsonArgs.J(new { type = "allFeasibilityResults", data = sys.CheckAllFeasibility() }));
                break;

            case "manualTick":
                sys.Tick();
                await _sink.SendAsync(ws, JsonArgs.J(new { type = "state", data = sys.GetState() }));
                break;

            case "reset":
                sys.Reset();
                await _sink.SendAsync(ws, JsonArgs.J(new { type = "state", data = sys.GetState() }));
                break;

            case "updateProducerRate":
                sys.UpdateProducerRate(JsonArgs.JS(dataEl, "producerName") ?? "", JsonArgs.JS(dataEl, "shape") ?? "", JsonArgs.JD(dataEl, "rate"));
                await _sink.SendAsync(ws, JsonArgs.J(new { type = "state", data = sys.GetState() }));
                break;

            case "startTick":
                if (!svc.IsRunning)
                {
                    svc.IsRunning = true;
                    svc.TickTimer = new Timer(async _ =>
                    {
                        try
                        {
                            string json;
                            await svc.Gate.WaitAsync();
                            try { sys.Tick(); json = JsonArgs.J(new { type = "state", data = sys.GetState() }); }
                            finally { svc.Gate.Release(); }
                            await _sink.BroadcastAsync(json);
                        }
                        catch (Exception ex) { Console.Error.WriteLine("[timer] " + ex.Message); }
                    }, null, 0, 2000);
                }
                break;

            case "stopTick":
                svc.IsRunning = false; svc.TickTimer?.Dispose(); svc.TickTimer = null;
                break;

            case "setAutoCheck":
            {
                sys.SetAutoCheck(JsonArgs.JB(dataEl, "enabled"));
                await _sink.SendAsync(ws, JsonArgs.J(new { type = "autoCheckChanged", data = new { enabled = sys.AutoCheckEnabled } }));
                if (sys.AutoCheckEnabled && svc.AutoCheckTimer == null)
                    svc.AutoCheckTimer = new Timer(async _ =>
                    {
                        try
                        {
                            string json;
                            await svc.Gate.WaitAsync();
                            try { json = JsonArgs.J(new { type = "autoCheckResults", data = sys.CheckAllFeasibility() }); }
                            finally { svc.Gate.Release(); }
                            await _sink.BroadcastAsync(json);
                        }
                        catch (Exception ex) { Console.Error.WriteLine("[timer] " + ex.Message); }
                    }, null, 5000, 5000);
                else if (!sys.AutoCheckEnabled) { svc.AutoCheckTimer?.Dispose(); svc.AutoCheckTimer = null; }
                await _sink.SendAsync(ws, JsonArgs.J(new { type = "state", data = sys.GetState() }));
                break;
            }

            // 未知命令：此前是静默丢弃（switch 无 default），客户端无法区分"命令名写错"与"网络异常"。
            // 仅新增回包，不改任何已知命令的行为（合法输入路径完全不受影响）。
            default:
                await _sink.SendAsync(ws, JsonArgs.J(new { type = "error", data = new { message = "未知命令: " + type } }));
                break;
        }
    }

    /// <summary>按 orderId 解析订单下标（兼容旧的 orderIndex 参数）。</summary>
    private static int ResolveOrderIndex(JarSystemService svc, JsonElement d)
    {
        var sys = svc.System;
        if (d.TryGetProperty("orderId", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
        {
            var id = idEl.GetInt32();
            // 批 E 后 Orders 是 IReadOnlyList（没有 List.FindIndex），改为等价的线性查找：找不到返回 -1
            for (int i = 0; i < sys.Orders.Count; i++)
                if (sys.Orders[i].Id == id) return i;
            return -1;
        }
        // 兼容旧参数 orderIndex
        return JsonArgs.JI(d, "orderIndex");
    }
}
