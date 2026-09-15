using System.Collections.Generic;

namespace JarSystem.Core.Config;

public static class Constants
{
    public static readonly string[] SHAPES = ["圆罐", "方罐", "三角罐", "高罐", "长罐", "扁罐", "鼓罐", "尖罐"];
    public static readonly string[] LIQUIDS = ["酒", "蜜"];
    public const int DEFAULT_MAX_JARS = 20;
    public const int DEFAULT_MAX_LIQUIDS = 30;
    public const int WAREHOUSE_MAX_JARS = 200;
    public const int WAREHOUSE_MAX_LIQUIDS = 200;
    public const int MAX_ORDER_REQUIREMENTS = 10;

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
