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
/// autoCheckResults / autoCheckChanged / error，以及**连接时一次性下发的 world**（形状/液体词表，见 <see cref="WebSocketHub"/>）。
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
        catch (JarSystem.Core.Domain.DomainValidationException ex)
        {
            // 领域规则拒绝（速率越界 / 容量非法 / 订单类型非法 / 需求形状越界…）统一在这里转成 error 回包。
            // 适配层只负责"呈现"，规则本身住在核心 ⇒ Web / Godot / 脚本三个宿主自动继承同一条约束。
            await _sink.SendAsync(ws, JsonArgs.J(new { type = "error", data = new { message = ex.Message } }));
        }
        catch (Exception ex)
        {
            // **兜底**（2026-09-16 补）：上面只接领域异常，任何别的异常（上游解析越界、意外的空引用…）
            // 以前会一路冒到 `WebSocketHub` 的会话级 catch ⇒ 客户端"莫名断线"、服务端只留一行 Console.Error。
            // 现在统一转成 error 回包 + 经 ISystemLog 留痕，于是 Hub 那句
            // "非法/非预期形状的报文只回一条 error，绝不断开连接" 对**所有**畸形输入都成立。
            // 注：`IWebSocketSink.SendAsync` 内部自吞发送异常（连接已断也不会抛），故这里无需再包一层。
            svc.System.Log.Error("命令 " + type + " 处理失败: " + ex.Message, ex);
            await _sink.SendAsync(ws, JsonArgs.J(new { type = "error", data = new { message = "命令处理失败: " + ex.Message } }));
        }
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
                var orderType = JsonArgs.JS(dataEl, "orderType") ?? "A";
                var reqs = JsonArgs.ParseReqs(dataEl);
                // 订单类型 A/B、形状/液体域、需求条数与顺序 —— 全部由核心校验（见 Order.ValidateOrder）；
                // 这里只把拒绝原因转给客户端。
                var created = sys.CreateOrder(reqs, orderType);
                if (!created.Success)
                {
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
                // 容量合法性由核心校验（WorkshopService.UpdateWorkshopCapacity 抛 DomainValidationException）
                sys.UpdateWorkshopCapacity(idx, maxJars, maxLiquids);
                await _sink.SendAsync(ws, JsonArgs.J(new { type = "state", data = sys.GetState() }));
                break;
            }

            case "moveOrder":
            {
                // 执行顺序是内核状态（谁先跑决定谁先拿稀缺资源），不是页面排版。
                var oid = JsonArgs.JI(dataEl, "orderId");
                var dir = JsonArgs.JI(dataEl, "dir");
                sys.MoveOrder(oid, dir);
                await _sink.SendAsync(ws, JsonArgs.J(new { type = "state", data = sys.GetState() }));
                break;
            }

            case "setWorkshopCapability":
            {
                var idx = JsonArgs.JI(dataEl, "index");
                // 合法性由核心校验（WorkshopService.SetWorkshopCapability 抛 DomainValidationException）：
                // 索引、词表、以及"新禁令会不会锁死池里已有的液体"三条都只有内核知道。
                sys.SetWorkshopCapability(idx, JsonArgs.JL(dataEl, "baseMarks"),
                    JsonArgs.JL(dataEl, "additionalMarks"), JsonArgs.JL(dataEl, "ban"));
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

            case "startOrder":
            {
                // P4：**异步轨**（分步执行）—— 只"开始"，随后由世界 tick 驱动（每 2 刻一个单位）。
                // 进度 / 阻断 / 结束经过程消息广播（见 ProcessObserver），state 里也有活动过程的投影。
                var idx = ResolveOrderIndex(svc, dataEl);
                if (idx < 0) { await _sink.SendAsync(ws, JsonArgs.J(new { type = "error", data = new { message = "订单不存在" } })); break; }
                var started = sys.StartOrder(idx);
                if (!started.Started)
                {
                    await _sink.SendAsync(ws, JsonArgs.J(new { type = "error", data = new { message = started.Error ?? "无法开始执行" } }));
                    break;
                }
                await _sink.SendAsync(ws, JsonArgs.J(new { type = "state", data = sys.GetState() }));
                break;
            }

            case "cancelProcess":
            {
                // P4：取消进行中的过程（已完成的部分不回滚；订单按失败收尾）。
                var processId = JsonArgs.JI(dataEl, "processId");
                var cancelled = sys.CancelProcess(processId);
                if (!cancelled.Cancelled)
                {
                    await _sink.SendAsync(ws, JsonArgs.J(new { type = "error", data = new { message = cancelled.Error ?? "取消失败" } }));
                    break;
                }
                await _sink.SendAsync(ws, JsonArgs.J(new { type = "state", data = sys.GetState() }));
                break;
            }

            case "setTickInterval":
            {
                // P5：宿主侧时间参数（表现层）——**只改推进节奏**，不碰判定（判定只认逻辑 tick）。
                var ms = JsonArgs.JI(dataEl, "intervalMs");
                if (ms < 100 || ms > 60000)
                {
                    await _sink.SendAsync(ws, JsonArgs.J(new { type = "error", data = new { message = "推进间隔必须在 100~60000 毫秒之间" } }));
                    break;
                }
                svc.TickIntervalMs = ms;
                await _sink.BroadcastAsync(JsonArgs.J(new { type = "tickIntervalChanged", data = new { intervalMs = ms } }));
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
            {
                var rate = JsonArgs.JD(dataEl, "rate");
                // 速率上下界由核心校验（ProductionService.UpdateProducerRate）
                sys.UpdateProducerRate(JsonArgs.JS(dataEl, "producerName") ?? "", JsonArgs.JS(dataEl, "shape") ?? "", rate);
                await _sink.SendAsync(ws, JsonArgs.J(new { type = "state", data = sys.GetState() }));
                break;
            }

            case "startTick":
                // P5：推进循环改由 TickBackgroundService（PeriodicTimer）驱动，这里只翻开关
                svc.IsRunning = true;
                break;

            case "stopTick":
                svc.IsRunning = false;
                break;

            case "setAutoCheck":
            {
                sys.SetAutoCheck(JsonArgs.JB(dataEl, "enabled"));
                await _sink.SendAsync(ws, JsonArgs.J(new { type = "autoCheckChanged", data = new { enabled = sys.AutoCheckEnabled } }));
                // P5：自动检测循环改由 AutoCheckBackgroundService（PeriodicTimer）驱动，这里只设开关
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
            // 用 `TryGetInt32` 而不是 `GetInt32()`：后者对**超出 Int32 的合法 JSON 数字**
            // （例如 99999999999999999999）会抛异常，而那不是 DomainValidationException
            // ⇒ 越过下面的 catch 冒到 WS 会话级 catch ⇒ 客户端直接掉线，
            // 与 `WebSocketHub` 自己的承诺（"非法形状的报文只回一条 error，绝不断开连接"）相矛盾（2026-09-16 修）。
            // 超范围 ⇒ 一律当作"找不到"，调用方按既有的"无效订单索引"回报。
            if (!idEl.TryGetInt32(out var id)) return -1;
            // 批 E 后 Orders 是 IReadOnlyList（没有 List.FindIndex），改为等价的线性查找：找不到返回 -1
            for (int i = 0; i < sys.Orders.Count; i++)
                if (sys.Orders[i].Id == id) return i;
            return -1;
        }
        // 兼容旧参数 orderIndex
        return JsonArgs.JI(d, "orderIndex");
    }
}
