using System;
using System.Collections.Generic;

namespace JarSystem.Core.Config;

public static class Constants
{
    // 私有后备数组 + 只读包装。
    // 原先写成 public static readonly string[] —— 元素是**可写**的：
    // 任何调用方都能 Constants.SHAPES[0] = "x" 就地改掉全局词表，
    // 而这个数组实例还与 Vocabulary.Default 共享，等于污染整个进程的词表。
    private static readonly string[] _shapes = ["圆罐", "方罐", "三角罐", "高罐", "长罐", "扁罐", "鼓罐", "尖罐"];
    private static readonly string[] _liquids = ["酒", "蜜"];

    public static readonly IReadOnlyList<string> SHAPES = Array.AsReadOnly(_shapes);
    public static readonly IReadOnlyList<string> LIQUIDS = Array.AsReadOnly(_liquids);
    public const int DEFAULT_MAX_JARS = 20;
    public const int DEFAULT_MAX_LIQUIDS = 30;
    public const int WAREHOUSE_MAX_JARS = 200;
    public const int WAREHOUSE_MAX_LIQUIDS = 200;
    public const int MAX_ORDER_REQUIREMENTS = 10;

    /// <summary>
    /// 生产者速率的合法上限（每 tick）。**这是游戏规则，住在核心**（不是 Web 适配层的输入校验）——
    /// 否则只有 Web 宿主遵守，Godot/脚本宿主可以提交 1e9 把产出循环变成 OOM（实测 20 万/tick 即造 20 万个罐子）。
    /// </summary>
    public const double MAX_PRODUCER_RATE = 100;

    public static class OrderType
    {
        public const string A = "A";
        public const string B = "B";
    }

    public static class OrderStatus
    {
        public const string PENDING = "待执行";
        public const string EXECUTING = "执行中";
        public const string SUCCESS = "成功";
        public const string FAILED = "失败";
    }

    public static class OperationType
    {
        public const string POUR_IN = "倒液";
        public const string POUR_OUT = "倒出";
        public const string RESHAPE_EMPTY = "改形(空罐)";
        public const string RESHAPE_FULL = "改形(满罐)";
        public const string TRANSFER = "转移";
    }

    /// <summary>订单执行阶段（<c>OrderEvent.CurrentPhase</c>）。字符串值即为行为契约，勿改。</summary>
    public static class OrderPhase
    {
        public const string NONE = "none";
        public const string MASTER = "master";
        public const string SLAVE = "slave";
    }

    /// <summary>可行性分析方法（<c>FeasibilityResultData.CheckMethod</c>）。字符串值即为行为契约，勿改。</summary>
    public static class CheckMethod
    {
        public const string SINGLE_PHASE = "single_phase";
        public const string SLAVE_ONLY = "slave_only";
        public const string PARTITION = "partition";
    }

    /// <summary>工作台切换事件分支（<c>WorkshopSwitchEvent.CaseType</c>）。字符串值即为行为契约，勿改。</summary>
    public static class SwitchCaseType
    {
        public const string SAME_BANS = "same_bans";
        public const string DIFFERENT_BANS = "different_bans";
    }
}
