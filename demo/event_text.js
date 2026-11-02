/* 事件讲解器（解谜与战斗共用）
   ---------------------------------------------------------------------------
   为什么要有这个文件：表现层的两套界面（`puzzle_ui.js` / `battle_ui.js`）如果各写一份
   "这条事件怎么念"，同一件事就会有两种措辞 —— 而这正是内核里
   `Presentation/PresentationFeed` 想避免的那类分裂：**一份世界，一套叙述**。

   约定：`describe(ev)` 必须对每一个会出现的 kind 都有说法。没登记的 kind 会原样露出
   `kind` 字符串，`_test_battle.js` 有一条断言专门盯这个（任何事件被念成裸 kind 就红）。
   这里只做措辞，不做推导 —— 顺序、数值、归属全部来自引擎发出的事件。 */
(function () {
  var SHORT = window.SHORT || {};

  var LABEL = {
    turnStart: '回合', produce: '产出', flow: '入库', flowLiquid: '入库',
    orderTry: '订单', op: '操作', deliver: '交付', orderBlocked: '受阻',
    transfer: '转移', migrateReshape: '重组', migrateDone: '换台', backflow: '回流',
    battleStart: '开战', constraintIn: '约束', constraintFreed: '松绑',
    enemyDraw: '出题', enemySweep: '收回', damage: '伤害',
    deadlocked: '锁死', battleWin: '胜', battleLose: '败', note: '战报'
  };

  function shapeName(s) { return (typeof SHORT !== 'undefined' && SHORT[s]) ? SHORT[s] : s; }

  function describe(ev) {
    if (!ev) return '—';
    var o = ev.order === undefined ? '' : '订单 #' + (ev.order + 1) + '：';
    if (ev.kind === 'turnStart') return '第 ' + ev.turn + ' 回合开始';
    if (ev.kind === 'produce') {
      if (ev.item === 'jar') return ev.amount > 0 ? ('产线产出 <b>' + ev.amount + '</b> 只空' + ev.shape + ' → 仓库')
        : ('仓库罐位满，' + ev.shape + ' ×' + ev.discarded + ' 丢弃');
      return ev.amount > 0 ? ('产线产出 <b>' + ev.amount + '</b> 份' + ev.liquid + ' → 仓库')
        : ('仓库' + ev.liquid + '已满，产出丢弃');
    }
    if (ev.kind === 'flow') return '仓库 → 主台：流入一只 ' + ev.shape + (ev.liquid ? '（' + ev.liquid + '）' : '');
    if (ev.kind === 'flowLiquid') return '仓库 → 主台：液体流入 <b>' + ev.amount + '</b> 份';
    if (ev.kind === 'orderTry') return o + '开始尝试执行（' + ev.cls + ' 类）';
    if (ev.kind === 'op') {
      if (ev.op === 'match') return o + shapeName(ev.fromShape) + ' 照用';
      if (ev.op === 'pourin') return o + shapeName(ev.fromShape) + ' 灌注 <b>' + ev.liquid + '</b>';
      if (ev.op === 'pourout') return o + shapeName(ev.fromShape) + ' 倒出 <b>' + ev.liquid + '</b> 回液池';
      if (ev.op === 'reshape') return o + shapeName(ev.fromShape) + ' 改形成 ' + shapeName(ev.toShape) + '（耗 1 份' + ev.liquid + '）';
      if (ev.op === 'reshapefull') return o + shapeName(ev.fromShape) + ' 改形成 ' + shapeName(ev.toShape) + '（罐内液体被吃掉）';
      return o + '执行 ' + ev.op;
    }
    if (ev.kind === 'deliver') return o + '交付 ' + shapeName(ev.shape) + (ev.liquid ? ':' + ev.liquid : '') + ' → ' + (ev.side === 'sub' ? '从台' : '主台');
    if (ev.kind === 'orderBlocked') return o + ev.reason;
    if (ev.kind === 'transfer') return '转移 ' + shapeName(ev.shape) + '：' + (ev.from === 'master' ? '主台 → 从台' : '从台 → 主台');
    if (ev.kind === 'migrateReshape') return '迁徙重组：把游离液体灌进旧台基础形状的空罐带走';
    if (ev.kind === 'migrateDone') return '迁徙完成：' + (ev.side === 'master' ? '主台' : '从台') + ' 换成 ' + ev.to;
    if (ev.kind === 'backflow') return '订单结束：从台残留回流主台';
    if (ev.kind === 'battleStart') return '开战：' + ev.enemy + '（' + ev.hp + ' 点）';
    if (ev.kind === 'constraintIn') return '<b>' + ev.text + '</b> 生效了';
    if (ev.kind === 'constraintFreed') return '松开了 <b>' + ev.text + '</b>';
    if (ev.kind === 'enemyDraw') {
      if (ev.note === 'squeeze') return '场上满了，题「' + ev.label + '」被挤掉';
      return '它出了新题：<b>' + ev.label + '</b>' +
        (ev.value === undefined ? '' : '（面值 ' + ev.value + '，' + ev.cls + ' 类）');
    }
    if (ev.kind === 'enemySweep') {
      return ev.label ? ('「' + ev.label + '」已经做不出来了，它把题收了回去')
        : ('它把 <b>' + ev.count + '</b> 张再也做不出来的题收了回去');
    }
    if (ev.kind === 'damage') return '吃掉 ' + ev.count + ' 张题，造成 <b>' + ev.dmg + '</b> 点伤害（剩 ' + ev.hp + '）';
    if (ev.kind === 'deadlocked') return '场上没有任何一张题还能往上长' + (ev.fatal ? ' —— 输了' : '，再这样一回合就输了');
    if (ev.kind === 'battleWin') return '<b>打穿了 ' + ev.enemy + '</b>';
    if (ev.kind === 'battleLose') return '<b>被锁死了</b> —— 场上没有任何一张题还能往上长';
    if (ev.kind === 'note') return ev.text;
    return ev.kind;
  }


  window.EVENT_TEXT = {
    describe: describe,
    shapeName: shapeName,
    label: function (kind) { return LABEL[kind] || kind; },
    labels: LABEL
  };
})();
