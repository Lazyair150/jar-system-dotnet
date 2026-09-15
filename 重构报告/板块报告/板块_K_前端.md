# 板块 K — 前端（修复报告）

> 范围：`src/JarSystem.Web/wwwroot/`：`js/app.js`、`js/canvas.js`、`js/constants.js`、`index.html`、`css/style.css`
> 阶段：**本次重构未改动前端**（契约冻结策略）
> 验证状态：**待验证**（浏览器冒烟，见 §4）

---

## 1. 板块职责

Web 演示界面：Canvas 生产网络渲染、工作台/订单卡片、路径弹窗、切换动画、操作日志。数据源是 WebSocket 推送的 `state`（即 `GetState()` 的序列化结果）。

## 2. 为什么本板块一行未改

本次重构的硬约束是**对外 JSON 契约冻结**。前端的字段依赖清单（审计 §1.2）如下，重构全程按此保护：

| 前端读取位置 | 依赖字段 |
|---|---|
| `app.js` 全局状态栏 | `globalState.currentMaster/masterName/currentSlave/slaveName` |
| `app.js` 工作台卡片 | `workshops[].{index,name,baseMarks,additionalMarks,ban,maxJars,maxLiquids,jars[],liquids,canPourShapes,isMaster,isSlave}`；`jars[].{id,shape,liquid,isMarked,description}` |
| `app.js` 订单卡片 | `orders[].{index,id,type,requirements[],status,feasibilityResult,description}` |
| `app.js` 路径弹窗 | `feasibilityResult.{feasible,solverPath,masterPath,transferJarIds,transferLiquids,directCount,operationCount}` |
| `canvas.js` | `producerStatus.{producers,generators,warehouse}`、`warehouse.{jars,liquids,discardedJars,discardedLiquids}` |
| WS 消息 | `init`/`state`/`orderExecuted`/`workshopSwitched`/`feasibilityResult`/`allFeasibilityResults`/`autoCheckResults`/`autoCheckChanged`/`error` |

阶段 3 的 DTO 化（`StateDto` + `[JsonPropertyName]`）正是为**把这些键名固定下来**：键名由特性决定，不再随序列化策略或属性改名而漂移。

## 3. 已知的前端小问题（记录，未改动）

| 编号 | 位置 | 问题 | 处置 |
|---|---|---|---|
| K1 | `app.js:26,29` | 读取永不存在的 `directSatisfactions`/`operationsNeeded` 兜底分支（核心这两项从不序列化） | 死代码；保留无害。可删，但删了不改变行为，属可选清理 |
| K2 | `app.js` `renderWorkshops` | 每次 `state` 推送用 `innerHTML` 重建全部卡片（第八轮已用 `editingCapacityIndex`/`capacityDraft` 缓解"容量编辑被打断"） | 保留（重构成组件化 DOM 属较大改动，收益有限） |
| K3 | `app.js` 事件绑定 | 内联 `onclick` + 全局函数 | 保留 |
| K4 | `canvas.js` | 与 `constants.js` 共享形状/液体常量（与核心 `Constants` 是两份拷贝） | 保留；形状/液体名未变，无漂移风险（若将来改常量需两处同步） |

## 4. 验证（浏览器冒烟，需你执行）

| 步骤 | 期望 |
|---|---|
| 打开 `http://localhost:3457` | 页面正常加载，工作台/订单/仓库/生产者条各就各位 |
| 创建订单（含满罐与空罐需求）→ 分析 | 订单卡片显示可行性与计数；"路径"弹窗显示「主台 → 选择性转移 → 从台」两段与转移清单 |
| 执行订单 | 卡片状态更新；B 类订单出现转移动画（`transferOccurred`） |
| 设为主台 | 旧台卡片缩放淡出 + 新台发光 + 粒子动画（`workshopSwitched`） |
| 编辑工作台容量 | 输入框不再被 2 秒 tick 重绘打断；提交后容量生效、溢出计入丢弃 |
| 自动检测 | 开关生效，5 秒推送 `autoCheckResults` |

## 5. 遗留项

| 项 | 计划 |
|---|---|
| 前端整体重写（组件化、去掉 `innerHTML` 重建） | **不在本次重构范围**（契约冻结策略下前端零改动是最安全的）；若将来做 Godot 版，前端本就整体废弃 |
| K1 死分支清理 | 可选（零行为影响） |
| 形状/液体常量两份拷贝（K4） | 可选：改为构建时从核心导出，或在前端加断言；当前名称稳定，风险低 |
