/* 陶罐战线 · 战斗数据
   —— 敌人、订单族模板、约束。战斗内核只认这三样，不含任何界面代码。 */

(function () {
  'use strict';

  var BENCHES = window.BENCHES;

  var BATTLE_C = {
    HP_DEFAULT: 20,
    FIELD_MAX: 4,       /* 场上最多几张未完成订单 */
    DRAW_PER_TURN: 2,   /* 敌人每回合出几张 */
    MAX_JAR_SHRINK: 2   /* 罐位最多被缩几格（6 → 4） */
  };

  /* ---------------- 订单族模板 ---------------- */
  /* reqs 里 liquid 为 null 表示「空罐」，否则表示「必须装着这种液体」。
     cls 'A' 只看主台；'B' 主从联合（要求你先配好从台）。 */

  var ORDER_POOL = [
    { id: 'a1', cls: 'A', label: '圆 · 双', reqs: [{ shape: '圆罐', liquid: null }, { shape: '圆罐', liquid: null }] },
    { id: 'a2', cls: 'A', label: '方 · 双', reqs: [{ shape: '方罐', liquid: null }, { shape: '方罐', liquid: null }] },
    { id: 'a3', cls: 'A', label: '高 · 双', reqs: [{ shape: '高罐', liquid: null }, { shape: '高罐', liquid: null }] },
    { id: 'a4', cls: 'A', label: '圆方 · 对', reqs: [{ shape: '圆罐', liquid: null }, { shape: '方罐', liquid: null }] },
    { id: 'a5', cls: 'A', label: '三角 · 双', reqs: [{ shape: '三角罐', liquid: null }, { shape: '三角罐', liquid: null }] },
    { id: 'a6', cls: 'A', label: '鼓 · 双', reqs: [{ shape: '鼓罐', liquid: null }, { shape: '鼓罐', liquid: null }] },
    { id: 'a7', cls: 'A', label: '圆蜜', reqs: [{ shape: '圆罐', liquid: '蜜' }, { shape: '方罐', liquid: null }] },
    { id: 'a8', cls: 'A', label: '高蜜', reqs: [{ shape: '高罐', liquid: '蜜' }, { shape: '高罐', liquid: null }] },
    { id: 'a9', cls: 'A', label: '长 · 蜜', reqs: [{ shape: '长罐', liquid: '蜜' }, { shape: '长罐', liquid: null }] },
    { id: 'a10', cls: 'B', label: '扁 · 酒', reqs: [{ shape: '扁罐', liquid: '酒' }, { shape: '扁罐', liquid: null }] },
    { id: 'a11', cls: 'B', label: '尖 · 蜜', reqs: [{ shape: '尖罐', liquid: '蜜' }, { shape: '尖罐', liquid: null }] },
    { id: 'a12', cls: 'A', label: '圆 · 三', reqs: [{ shape: '圆罐', liquid: null }, { shape: '圆罐', liquid: null }, { shape: '圆罐', liquid: null }] },
    { id: 'a13', cls: 'A', label: '三蜜圆', reqs: [{ shape: '圆罐', liquid: '蜜' }, { shape: '圆罐', liquid: '蜜' }, { shape: '方罐', liquid: null }] },
    { id: 'a14', cls: 'B', label: '三长蜜', reqs: [{ shape: '三角罐', liquid: '蜜' }, { shape: '长罐', liquid: null }, { shape: '圆罐', liquid: null }] },
    { id: 'a15', cls: 'A', label: '高长', reqs: [{ shape: '高罐', liquid: null }, { shape: '高罐', liquid: '蜜' }, { shape: '长罐', liquid: null }] },
    { id: 'a16', cls: 'B', label: '扁鼓方', reqs: [{ shape: '扁罐', liquid: null }, { shape: '鼓罐', liquid: null }, { shape: '方罐', liquid: null }] },
    { id: 'a17', cls: 'A', label: '方 · 三', reqs: [{ shape: '方罐', liquid: null }, { shape: '方罐', liquid: null }, { shape: '方罐', liquid: null }] },
    { id: 'a18', cls: 'B', label: '尖圆蜜', reqs: [{ shape: '尖罐', liquid: '蜜' }, { shape: '圆罐', liquid: '蜜' }] }
  ];

  /* 单一工作台能不能同时供给这张订单的所有形状与液体？
     能 → 这张订单是「一台通吃」，比较简单；不能 → 逼你用从台或换台。 */
  function oneBenchCanServe(reqs) {
    for (var i = 0; i < BENCHES.length; i++) {
      var b = BENCHES[i];
      var ok = true;
      for (var j = 0; j < reqs.length; j++) {
        var r = reqs[j];
        var hasShape = b.base.indexOf(r.shape) >= 0 || b.add.indexOf(r.shape) >= 0;
        if (!hasShape) { ok = false; break; }
        if (r.liquid && (b.ban || []).indexOf(r.liquid) >= 0) { ok = false; break; }
      }
      if (ok) return true;
    }
    return false;
  }

  /* 面值 = 罐数 - 1 + 装液罐数，若「没有一台能通吃」再 +1，封顶 4。
     它算的是**这张题有多难**，不是你投了几个罐子——你只能选择做哪几张。 */
  function orderValue(reqs) {
    var liquids = 0;
    for (var i = 0; i < reqs.length; i++) if (reqs[i].liquid) liquids++;
    var v = (reqs.length - 1) + liquids;
    if (!oneBenchCanServe(reqs)) v += 1;
    return Math.max(1, Math.min(4, v));
  }

  ORDER_POOL.forEach(function (o) { o.value = orderValue(o.reqs); });

  /* ---------------- 约束 ---------------- */
  /* kind: 'banShape' 封形状 | 'banLiquid' 禁液体 | 'shrinkJars' 缩罐位 */

  /* ---------------- 敌人 ---------------- */
  /* plan 是约束的出场顺序，循环取用。前一条会在「预告」栏先亮一回合，再真正生效。 */

  var ENEMIES = [
    {
      id: 'rust',
      name: '锈蚀',
      hp: 18,
      draw: 2,
      desc: '它一层一层剥掉你的世界：先断液体，再一块一块封掉形状。别指望一套配置打到黑。',
      plan: [
        { kind: 'banLiquid', target: '蜜' },
        { kind: 'banShape', target: '鼓罐' },
        { kind: 'banLiquid', target: '酒' },
        { kind: 'banShape', target: '三角罐' },
        { kind: 'banShape', target: '长罐' },
        { kind: 'banShape', target: '扁罐' },
        { kind: 'shrinkJars' },
        { kind: 'banShape', target: '尖罐' },
        { kind: 'banShape', target: '方罐' },
        { kind: 'banShape', target: '圆罐' },
        { kind: 'banShape', target: '高罐' }
      ],
      pool: ['a1', 'a4', 'a7', 'a6', 'a12', 'a15', 'a16', 'a9', 'a17']
    },
    {
      id: 'glut',
      name: '贪食',
      hp: 16,
      draw: 2,
      desc: '它从最稀有的形状吃起：尖、扁、鼓先没了。蜜是它最后才舍得断的东西。',
      plan: [
        { kind: 'banShape', target: '尖罐' },
        { kind: 'banShape', target: '扁罐' },
        { kind: 'banShape', target: '三角罐' },
        { kind: 'banShape', target: '长罐' },
        { kind: 'banLiquid', target: '酒' },
        { kind: 'shrinkJars' },
        { kind: 'banShape', target: '鼓罐' },
        { kind: 'banLiquid', target: '蜜' },
        { kind: 'banShape', target: '方罐' },
        { kind: 'banShape', target: '高罐' },
        { kind: 'banShape', target: '圆罐' }
      ],
      /* 池子必须做成「禁令打到后段才失效」的样子。
         原来这里是 a5(三角·双) 与 a13(三蜜圆)：三角在它的第 3 条禁令就被封，
         于是前 4 条禁令（尖/扁/三角/长）一过，池子里只剩 a2/a3/a8 三张还能做
         —— 场上抽空、玩家被锁死。实测（6 个种子）基线打手只赢 1/6、锁死输 5 次，
         而这条「自动驾驶应该能赢」当时只是 console.log，没人断言，所以漂了很久。
         换成 a1(圆·双) 与 a4(圆方·对)：圆到第 11 条才禁、方第 9 条 ⇒ 后段始终有活口。
         换完实测：6/6 全胜、锁死 0、赢时仍剩 4.2 条约束（没被削平）、摆烂仍 3/3 输。
         身份没变 —— 尖(a11/a18)、扁(a10)、长(a14) 这些"最稀有的形状"依旧在池子里。 */
      pool: ['a2', 'a3', 'a1', 'a8', 'a10', 'a4', 'a14', 'a11', 'a18']
    }
  ];

  window.BATTLE_C = BATTLE_C;
  window.ORDER_POOL = ORDER_POOL;
  window.ENEMIES = ENEMIES;
  window.orderValue = orderValue;
  window.oneBenchCanServe = oneBenchCanServe;
})();
