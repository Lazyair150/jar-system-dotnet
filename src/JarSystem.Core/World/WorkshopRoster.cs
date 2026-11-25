using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Domain;

namespace JarSystem.Core.World;

/// <summary>
/// 当前世界唯一的一套主从工作台岗位。
/// Roster 管工作台身份与岗位指向，不拥有工作台资源，也不复制 Workshop。
/// </summary>
public sealed class WorkshopRoster
{
    private readonly List<Workshop> _workshops;
    private readonly IReadOnlyList<Workshop> _workshopsView;

    public IReadOnlyList<Workshop> Workshops => _workshopsView;
    public Workshop? Master { get; private set; }
    public Workshop? Slave { get; private set; }

    public WorkshopRoster(IEnumerable<Workshop> workshops, Workshop? master = null, Workshop? slave = null)
    {
        if (workshops == null) throw new ArgumentNullException(nameof(workshops));
        _workshops = workshops.ToList();
        if (_workshops.Any(w => w == null))
            throw new ArgumentException("工作台集合不能包含 null", nameof(workshops));
        if (_workshops.Distinct(ReferenceEqualityComparer.Instance).Count() != _workshops.Count)
            throw new ArgumentException("同一个工作台对象不能重复登记", nameof(workshops));
        if (_workshops.Select(w => w.Name).Distinct(StringComparer.Ordinal).Count() != _workshops.Count)
            throw new ArgumentException("工作台名称必须唯一", nameof(workshops));
        var ownedInstanceIds = _workshops
            .Where(workshop => workshop.OwnedInstanceId != null)
            .Select(workshop => workshop.OwnedInstanceId!)
            .ToArray();
        if (ownedInstanceIds.Distinct(StringComparer.Ordinal).Count() != ownedInstanceIds.Length)
            throw new ArgumentException("Workshop OwnedInstanceId 必须唯一", nameof(workshops));

        _workshopsView = _workshops.AsReadOnly();
        ValidateMember(master, nameof(master));
        ValidateMember(slave, nameof(slave));
        if (master != null && ReferenceEquals(master, slave))
            throw new ArgumentException("主工作台和从工作台不能是同一台");

        Master = master;
        Slave = slave;
    }

    public Workshop? Find(string name) =>
        _workshops.FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.Ordinal));

    public bool IsSpare(Workshop workshop) =>
        Contains(workshop) && !ReferenceEquals(workshop, Master) && !ReferenceEquals(workshop, Slave);

    internal void CommitMasterReplacement(Workshop expectedOld, Workshop replacement)
    {
        if (!ReferenceEquals(Master, expectedOld))
            throw new InvalidOperationException("主工作台岗位在替换期间发生变化");
        if (!IsSpare(replacement))
            throw new InvalidOperationException("目标工作台已不再是备用工作台");
        Master = replacement;
    }

    internal void CommitSlaveReplacement(Workshop expectedOld, Workshop replacement)
    {
        if (!ReferenceEquals(Slave, expectedOld))
            throw new InvalidOperationException("从工作台岗位在替换期间发生变化");
        if (!IsSpare(replacement))
            throw new InvalidOperationException("目标工作台已不再是备用工作台");
        if (replacement.HasResources)
            throw new InvalidOperationException("备用工作台必须为空才能接任从工作台");
        Slave = replacement;
    }

    private bool Contains(Workshop? workshop) =>
        workshop != null && _workshops.Any(w => ReferenceEquals(w, workshop));

    private void ValidateMember(Workshop? workshop, string parameter)
    {
        if (workshop != null && !Contains(workshop))
            throw new ArgumentException("岗位工作台必须来自工作台集合", parameter);
    }
}
