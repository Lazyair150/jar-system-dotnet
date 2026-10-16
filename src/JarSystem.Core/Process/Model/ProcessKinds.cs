namespace JarSystem.Core.Process;

/// <summary>
/// 过程种类（**取值即契约**；新增种类必须在本表登记，并在设计规格 §2.1 同步）。
/// </summary>
public static class ProcessKinds
{
    /// <summary>订单执行：一次委托的实现过程。</summary>
    public const string OrderExecution = "order_execution";

    /// <summary>工作台迁徙：主台切换时的资源重组过程（P4 起使用）。</summary>
    public const string WorkshopMigration = "workshop_migration";
}

