/* 无头模拟：验证引擎跑得通、关卡可解、下界不说谎 */
global.window = global;
require('./puzzle_data.js');
require('./puzzle_engine.js');

var ENG = global.window.ENG;
var B = function (id) { return ENG.findBench(id); };
var P = function (id) { return ENG.findProd(id); };

function setup(G, cfg) {
  if (cfg.master) ENG.setBench(G, 'master', B(cfg.master));
  if (cfg.sub) ENG.setBench(G, 'sub', B(cfg.sub));
  (cfg.lines || []).forEach(function (l, i) {
    G.lines[i] = l ? { prod: P(l.prod), target: l.target || 'master' } : null;
  });
}

function play(lvIdx, cfg, verbose) {
  var G = ENG.newGame(lvIdx);
  setup(G, cfg);
  var t = 0;
  while (!G.over && t < G.lv.turns) {
    ENG.runTurn(G);
    t++;
    if (verbose) {
      var prog = G.orders.map(function (o) {
        return '#' + (o.idx + 1) + ' ' + o.progress + '/' + o.total + (o.done ? '✓' : '');
      }).join('  ');
      console.log('  T' + G.turn + '  ' + prog +
        '  | 主台罐 ' + G.master.jars.length + ' 液 ' + JSON.stringify(G.master.liquid) +
        (G.sub ? '  | 从台罐 ' + G.sub.jars.length : ''));
    }
  }
  return G;
}

function verdict(G) {
  var done = G.orders.filter(function (o) { return o.done; }).length;
  return (G.over ? (G.over.win ? 'WIN@' + G.turn : 'FAIL') : 'RUNNING') + ' ' + done + '/' + G.orders.length;
}

console.log('=== 第一关 ===');
var g1 = play(0, { master: 'xuanwu', lines: [
  { prod: 'j圆' }, { prod: 'j方' }, { prod: 'l蜜' }
] }, true);
console.log('  => ' + verdict(g1));

console.log('=== 第二关 ===');
var g2 = play(1, { master: 'xuanwu', sub: 'zhuque', lines: [
  { prod: 'j圆' }, { prod: 'l蜜' }, { prod: 'j高', target: 'sub' }
] }, true);
console.log('  => ' + verdict(g2));

console.log('=== 第三关（试一个朴素配置，预期很难）===');
var g3 = play(2, { master: 'xuanwu', sub: 'qinglong', lines: [
  { prod: 'j圆' }, { prod: 'l蜜' }, { prod: 'l酒', target: 'sub' }
] }, true);
console.log('  => ' + verdict(g3));

console.log('=== 第四关（试一个朴素配置，预期很难）===');
var g4 = play(3, { master: 'xuanwu', sub: 'zhuque', lines: [
  { prod: 'j圆' }, { prod: 'l蜜' }, { prod: 'j高', target: 'sub' }
] }, true);
console.log('  => ' + verdict(g4));

/* 下界正确性抽查：grow 说能长到 N ⇒ 把它的 steps 逐条按规则复核、真的应用一遍，
   最后**独立地**数一数「到底有几条需求被满足」。这个数必须 ≥ grow 报的 height。
   （旧版这段是坏的：reqLiquidOf 是个恒返回 null 的桩、def 与罐子无关、sim 丢了 home
     ⇒ 每一步都被当成"空罐需求"重演，35~45% 的"不一致"是假警报。见第9步笔记。） */
console.log('=== 下界正确性抽查 ===');
var bad = 0, checked = 0, illegal = 0, promise = 0;
for (var li = 0; li < global.window.LEVELS.length; li++) {
  var G = ENG.newGame(li);
  var mdef = (G.master && G.master.def) ? G.master.def : global.window.BENCHES[0];
  var benches = [mdef];
  if (G.sub && G.sub.def) benches.push(G.sub.def);
  for (var trial = 0; trial < 30; trial++) {
    /* 池子：罐子来自**真实存在的工作台**（带 home），因为 home 决定用哪套能力规则 */
    var pool = [];
    var n = 1 + Math.floor(Math.random() * 6);
    for (var i = 0; i < n; i++) {
      var hd = benches[Math.floor(Math.random() * benches.length)];
      pool.push({
        shape: global.window.SHAPES[Math.floor(Math.random() * global.window.SHAPES.length)],
        liquid: Math.random() < 0.5 ? null : (Math.random() < 0.5 ? '酒' : '蜜'),
        ref: 'j' + trial + '_' + i,
        home: hd
      });
    }
    var liq = { '酒': Math.floor(Math.random() * 4), '蜜': Math.floor(Math.random() * 4) };
    var od = G.orders[Math.floor(Math.random() * G.orders.length)];
    if (!od) continue;
    var r = ENG.grow(od, pool, mdef, liq);
    checked++;

    var sim = pool.map(function (j) { return { shape: j.shape, liquid: j.liquid, home: j.home, used: false }; });
    var simLiq = { '酒': liq['酒'], '蜜': liq['蜜'] };
    var ok = true;
    for (var s = 0; s < r.steps.length; s++) {
      var st = r.steps[s], idx = -1;
      for (var k = 0; k < pool.length; k++) if (pool[k].ref === st.ref) { idx = k; break; }
      if (idx < 0) { ok = false; break; }
      var j = sim[idx], op = st.op, d = j.home || mdef;
      if (j.used) { ok = false; if (illegal < 3) console.log('  ! 重复操作 ' + od.name + ' ref=' + st.ref); break; }  /* 一罐一操作 */
      j.used = true;
      if (op.op === 'match') { /* 只标记 */ }
      else if (op.op === 'pourout') {
        if (j.liquid == null || ENG.isBanned(d, j.liquid)) { ok = false; if (illegal < 3) console.log('  ! pourout 非法 ' + od.name + ' jar=' + j.shape + '/' + j.liquid + ' ban=' + d.ban.join(',')); break; }
        simLiq[j.liquid] = (simLiq[j.liquid] || 0) + 1; j.liquid = null;
      } else if (op.op === 'pourin') {
        if (j.liquid != null || !op.liquid || ENG.isBanned(d, op.liquid)) { ok = false; if (illegal < 3) console.log('  ! pourin 前提 ' + od.name + ' jar=' + j.shape + '/' + j.liquid + ' op=' + op.liquid + ' ban=' + d.ban.join(',')); break; }
        if (!ENG.isBaseMarked(d, j.shape) || (simLiq[op.liquid] || 0) < 1) { ok = false; if (illegal < 3) console.log('  ! pourin 无钱/非基础 ' + od.name + ' jar=' + j.shape + ' base=' + d.base.join(',') + ' 池=' + simLiq[op.liquid]); break; }
        simLiq[op.liquid]--; j.liquid = op.liquid;
      } else if (op.op === 'reshape') {
        if (j.liquid != null || !ENG.canTransform(j.shape, op.to, d)) { ok = false; if (illegal < 3) console.log('  ! reshape 前提 ' + od.name + ' jar=' + j.shape + '/' + j.liquid + ' to=' + op.to + ' base=' + d.base.join(',') + ' add=' + d.add.join(',')); break; }
        if ((simLiq[op.costLiquid] || 0) < 1) { ok = false; if (illegal < 3) console.log('  ! reshape 无成本液 ' + od.name + ' jar=' + j.shape + ' cost=' + op.costLiquid + ' 池=' + JSON.stringify(simLiq)); break; }
        simLiq[op.costLiquid]--; j.shape = op.to;
      } else if (op.op === 'reshapefull') {
        if (j.liquid == null || ENG.isBanned(d, j.liquid)) { ok = false; if (illegal < 3) console.log('  ! reshapefull 前提 ' + od.name + ' jar=' + j.shape + '/' + j.liquid + ' ban=' + d.ban.join(',')); break; }
        if (!ENG.canTransform(j.shape, op.to, d)) { ok = false; if (illegal < 3) console.log('  ! reshapefull 不能变 ' + od.name + ' jar=' + j.shape + ' to=' + op.to + ' base=' + d.base.join(',') + ' add=' + d.add.join(',')); break; }
        j.shape = op.to; j.liquid = null;
      } else { ok = false; break; }
    }
    if (!ok) { illegal++; if (illegal <= 3) console.log('    ↑ 该步来自 prep=' + JSON.stringify(r.steps.filter(function(x){return x.ref===st.ref;}).map(function(x){return {op:x.op.op,to:x.op.to,prep:!!x.prep};})));
      continue; }

    /* 独立复核：终态到底满足了几条需求（每只罐子只能用一次） */
    var taken = [], sat = 0;
    for (var q = 0; q < od.reqs.length; q++) {
      var req = od.reqs[q];
      for (var m = 0; m < sim.length; m++) {
        if (taken[m]) continue;
        if (sim[m].shape !== req.shape) continue;
        if (req.liquid ? sim[m].liquid !== req.liquid : sim[m].liquid != null) continue;
        taken[m] = true; sat++; break;
      }
    }
    if (sat < r.height) {
      promise++;
      if (promise <= 3) {
        console.log('  ! ' + od.name + '：grow 报 ' + r.height + '/' + r.total + '，重演只满足 ' + sat +
          ' | 需求 ' + od.reqs.map(function (x) { return x.shape + '/' + (x.liquid || '空'); }).join(','));
      }
    }
  }
}
console.log('  抽查 ' + checked + ' 次：非法步骤 ' + illegal + ' 次，承诺未兑现 ' + promise + ' 次');
if (illegal || promise) { console.log('  => 下界抽查 FAIL'); process.exitCode = 1; }
else { console.log('  => 下界抽查 OK'); }


/* ================= B 类订单的交付必须在从台（对齐 OrderEvent.CheckDelivery） =================
   原系统：`var deliveryWorkshop = Order.Type == Constants.OrderType.A ? Master : Slave;`（OrderEvent.cs:175）
   ⇒ B 类的全部需求罐最终都得落在**从台**上。主台的罐子要交付就必须转移过去，
   而能不能转过去受从台罐位限制（内核 transferBudget = SlaveInfo.MaxJars − 从台原有 − 交付罐数，
   PartitionSearch.cs:180）。下面三条把这条规则钉住。 */
console.log('=== B 类交付必须在从台 ===');
(function () {
  var bad = 0;

  function fresh() {
    var G = ENG.newGame(1);                    /* 第二关，含 B 类订单 */
    ENG.setBench(G, 'master', ENG.findBench('xuanwu'));
    ENG.setBench(G, 'sub', ENG.findBench('zhuque'));
    return G;
  }

  /* ① 从台塞满 ⇒ 主台上的交付罐无处可去 ⇒ 长不到头 */
  var g1 = fresh();
  for (var i = 0; i < 4; i++) g1.sub.jars.push({ shape: '高罐', liquid: null });
  g1.master.jars.push({ shape: '高罐', liquid: null });
  g1.master.jars.push({ shape: '高罐', liquid: null });
  g1.master.liquid['蜜'] = 2;
  var r1 = ENG.evalOrder(g1, g1.orders[1]);
  if (r1.height !== 0) { bad++; console.log('  ! 从台满是仍报 height=' + r1.height); }
  else console.log('  ① 从台罐位为 0 时拒绝交付：' + r1.reason);

  /* ② 高罐在玄武台上灌不进液体（高罐不是玄武的基础标记）—— 规则本身就该挡住 */
  var g2 = fresh();
  g2.master.jars.push({ shape: '高罐', liquid: null });
  g2.master.liquid['蜜'] = 2;
  var r2 = ENG.evalOrder(g2, g2.orders[1]);
  if (r2.height !== 1) { bad++; console.log('  ! 高罐在玄武台上竟能灌液，height=' + r2.height); }
  else console.log('  ② 玄武台上的高罐只能当空罐（高罐非其基础标记，灌不进蜜）');

  /* ③ 把它转移到朱雀从台之后就能灌蜜 —— 这正是"交付在从台"要的效果 */
  var g3 = fresh();
  var jar = { shape: '高罐', liquid: null };
  g3.master.jars.push(jar);
  g3.master.liquid['蜜'] = 2;
  ENG.transferJar(g3, jar, 'sub');
  var r3 = ENG.evalOrder(g3, g3.orders[1]);
  if (r3.height < 1) { bad++; console.log('  ! 转移到从台后反而长不出来了'); }
  else console.log('  ③ 同一只高罐转移到朱雀从台后按朱雀规则判定，可继续加工');

  if (bad) { console.log('  => B 类交付抽查 FAIL ' + bad + ' 处'); process.exitCode = 1; }
  else console.log('  => B 类交付抽查 OK');
})();


/* ================= 迁徙重组（对齐 WorkshopSwitchEvent.RunReshape） =================
   内核注释明说 SameBansCase 的用途是「把游离液体尽量灌进旧台基础形状的空罐」——
   目的是让新台接收"已装满的罐子"作为**额外液体容量**（罐子容量替代液池容量）。
   原型只有一块物理台，所以 RunTransfer 是空操作，缺的正是这段重组。 */
console.log('=== 迁徙重组 ===');
(function () {
  var bad = 0;
  var G = ENG.newGame(0);
  ENG.setBench(G, 'master', ENG.findBench('xuanwu'));
  G.master.liquid['蜜'] = 5;                       /* 玄武池容量 5 */
  for (var i = 0; i < 3; i++) G.master.jars.push({ shape: '圆罐', liquid: null });

  ENG.setBench(G, 'master', ENG.findBench('zhuque'));   /* 朱雀 pool=4、不忌蜜 ⇒ 不重组就会丢 1 份 */
  ENG.runTurn(G);
  ENG.runTurn(G);

  var full = G.master.jars.filter(function (j) { return j.liquid === '蜜'; }).length;
  if (G.master.def.name !== '朱雀台') { bad++; console.log('  ! 两回合后还没落定新台：' + G.master.def.name); }
  if (full !== 3) { bad++; console.log('  ! 应把 3 份蜜灌进 3 只空圆罐，实际 ' + full); }
  if ((G.master.liquid['蜜'] || 0) !== 2) { bad++; console.log('  ! 液池应剩 2 份，实际 ' + G.master.liquid['蜜']); }

  if (bad) { console.log('  => 迁徙重组抽查 FAIL ' + bad + ' 处'); process.exitCode = 1; }
  else console.log('  => 迁徙重组抽查 OK（5 份蜜 → 3 份进罐带走 + 2 份留池）');
})();

/* ============ 产线 → 仓库 → 主台（用户报的那处概念错误） ============
   用户的判据：产线是**世界里的资源生产线**，产出**不依赖当前主台**；
   罐子和液体是**流动的资源**，工作台只是承载它们并进行操作的载体。
   错误实现（改前）：产线形状必须是主台基础标记，否则"造不出（丢弃）"——
   等于把产线当成"命令主台生产"。 */
console.log('=== 产线 → 仓库 → 主台 ===');
(function () {
  var bad = 0;
  var G = ENG.newGame(0);                       /* 第一关主台 = 玄武（base 圆/方，禁酒） */
  G.lines[0] = { prod: ENG.findProd('j三') };   /* 三角 ×1，三角**不是**玄武的基础标记 */
  G.lines[1] = { prod: ENG.findProd('l酒') };   /* 酒 ×2，玄武**禁**酒 */
  G.lines[2] = null;

  ENG.produce(G);
  var whShapes = G.wh.jars.map(function (j) { return j.shape; });
  if (whShapes.indexOf('三角罐') < 0) { bad++; console.log('  ! 三角罐根本没被生产出来（产线被主台能力过滤了）'); }
  if ((G.wh.liquid['酒'] || 0) !== 2) { bad++; console.log('  ! 酒应照常产出 2 份进仓库，实际 ' + G.wh.liquid['酒']); }

  ENG.flowToMaster(G);
  var inMaster = G.master.jars.map(function (j) { return j.shape; });
  if (inMaster.indexOf('三角罐') < 0) { bad++; console.log('  ! 三角罐应能进主台（基础标记约束操作，不约束存储），实际 ' + JSON.stringify(inMaster)); }
  if ((G.master.liquid['酒'] || 0) !== 0) { bad++; console.log('  ! 玄武禁酒，酒不该进主台'); }
  if ((G.wh.liquid['酒'] || 0) !== 2) { bad++; console.log('  ! 被禁的酒应留在仓库等换台，实际 ' + G.wh.liquid['酒']); }

  /* 流入上限：罐子每回合最多 3 只 */
  var G2 = ENG.newGame(0);
  G2.lines[0] = { prod: ENG.findProd('j圆') };
  G2.lines[1] = { prod: ENG.findProd('j圆') };
  G2.lines[2] = { prod: ENG.findProd('j圆') };
  ENG.produce(G2);
  var total = G2.wh.jars.length;
  ENG.flowToMaster(G2);
  if (G2.master.jars.length !== Math.min(3, total)) {
    bad++; console.log('  ! 每回合应最多流入 3 只罐子，实际 ' + G2.master.jars.length + '（仓库原有 ' + total + '）');
  }
  if (G2.wh.jars.length !== Math.max(0, total - 3)) {
    bad++; console.log('  ! 超出的罐子应留在仓库不丢，实际仓 ' + G2.wh.jars.length);
  }

  if (bad) { console.log('  => 产线/仓库抽查 FAIL ' + bad + ' 处'); process.exitCode = 1; }
  else console.log('  => 产线/仓库抽查 OK（三角罐能进玄武台、酒留在仓库、每回合最多流 3 罐）');
})();

/* ============ 过程事件流（表现层的契约） ============
   UI 只按 G.events 的顺序播放，**不重新推导** —— 所以顺序本身必须被断言，
   否则"规则写第二遍"会从 UI 这一侧偷偷长回来。 */
console.log('=== 过程事件流 ===');
(function () {
  var bad = 0;
  function check(G, label) {
    var ev = G.events || [];
    if (!ev.length) { bad++; console.log('  ! ' + label + '：没有事件'); return; }
    if (ev[0].kind !== 'turnStart') { bad++; console.log('  ! ' + label + '：第一条不是 turnStart，而是 ' + ev[0].kind); }
    var seenOrder = false, delivered = {}, opAfterDeliver = false;
    ev.forEach(function (e) {
      if (!e.kind || e.turn === undefined) { bad++; console.log('  ! ' + label + '：事件缺 kind/turn'); }
      /* 流入必须早于任何订单尝试 */
      if ((e.kind === 'flow' || e.kind === 'flowLiquid') && seenOrder) { bad++; console.log('  ! ' + label + '：流入出现在订单之后'); }
      if (e.kind === 'orderTry') seenOrder = true;
      if (e.kind === 'deliver') {
        if (!seenOrder) { bad++; console.log('  ! ' + label + '：交付出现在订单尝试之前'); }
        if (e.side !== 'master' && e.side !== 'sub') { bad++; console.log('  ! ' + label + '：交付台非法 ' + e.side); }
        delivered[e.order] = true;
      }
      /* 同一条订单：交付之后不该再有它的操作（不同订单之间当然可以交替） */
      if (e.kind === 'op' && delivered[e.order]) opAfterDeliver = true;
    });
    if (opAfterDeliver) { bad++; console.log('  ! ' + label + '：同一订单在交付之后还有操作（顺序不对）'); }
  }

  /* A 类：第一关，圆罐 + 方罐 + 蜜 ⇒ 两条订单都能完成 */
  var G1 = ENG.newGame(0);
  G1.lines[0] = { prod: ENG.findProd('j圆') };
  G1.lines[1] = { prod: ENG.findProd('j方') };
  G1.lines[2] = { prod: ENG.findProd('l蜜') };
  ENG.runTurn(G1); check(G1, 'L1 A类');
  var aDeliver = (G1.events || []).filter(function (e) { return e.kind === 'deliver'; });
  if (aDeliver.length && aDeliver.some(function (e) { return e.side !== 'master'; })) {
    bad++; console.log('  ! L1 A 类订单的交付台必须是主台');
  }

  /* B 类：第五关，装备从台后交付台必须是从台 */
  var G5 = ENG.newGame(4);
  ENG.setBench(G5, 'sub', ENG.findBench('xuanwu'));
  G5.lines[0] = { prod: ENG.findProd('j长') };
  G5.lines[1] = { prod: ENG.findProd('l酒') };
  var guard = 0;
  while (!G5.over && guard++ < 12) { ENG.runTurn(G5); check(G5, 'L5 B类 T' + G5.turn); }
  var bDeliver = [];
  if (G5.log) { /* 只统计最后一回合的事件 */ }
  var lastDeliver = (G5.events || []).filter(function (e) { return e.kind === 'deliver'; });
  if (lastDeliver.length && lastDeliver.some(function (e) { return e.side !== 'sub'; })) {
    bad++; console.log('  ! 第五关 B 类订单的交付台必须是从台');
  }

  if (bad) { console.log('  => 事件流抽查 FAIL ' + bad + ' 处'); process.exitCode = 1; }
  else console.log('  => 事件流抽查 OK（首条 turnStart、产出→流入→订单→交付的顺序、交付台正确）');
})();

/* ============ 中间态快照（表现层按帧渲染的依据） ============
   播放时棋盘画的是 G.events[i].snap，不是终态。所以三条性质必须成立：
     ① 每个事件都有那一刻的快照；
     ② 流入是"搬一只发一条"（相邻两帧的主台只差一只，uid 还是新的）；
     ③ 交付之后那一帧里，这只罐子已经不在台上了（否则动画会画出一只"交付了还在"的罐子）。
   还有一条隐性性质：uid 跨台不变（转移是搬同一个对象），否则动画会以为是两只罐子。 */
console.log('=== 中间态快照 ===');
(function () {
  var bad = 0;
  function fail(m) { bad++; console.log('  ! ' + m); }

  var G = ENG.newGame(0);
  G.lines[0] = { prod: ENG.findProd('j圆') };
  G.lines[1] = { prod: ENG.findProd('j方') };
  G.lines[2] = { prod: ENG.findProd('l蜜') };
  ENG.runTurn(G);

  (G.events || []).forEach(function (e, i) {
    if (!e.snap) return fail('事件 #' + i + ' (' + e.kind + ') 没有快照');
    if (!e.snap.master || !e.snap.wh) return fail('事件 #' + i + ' 快照缺 master/wh');
    if (!Array.isArray(e.snap.master.jars)) return fail('事件 #' + i + ' 快照的罐子不是数组');
  });

  /* 流入一帧一只 */
  var flows = (G.events || []).filter(function (e) { return e.kind === 'flow'; });
  if (flows.length < 2) fail('本回合应当至少流入 2 只罐子，实际 ' + flows.length);
  for (var i = 1; i < flows.length; i++) {
    var prev = flows[i - 1].snap.master.jars.length;
    var cur = flows[i].snap.master.jars.length;
    if (cur !== prev + 1) fail('流入不是逐只发生的：第 ' + i + ' 帧主台 ' + prev + ' → ' + cur);
  }
  if (flows.length) {
    var lastUid = flows[flows.length - 1].uid;
    var has = flows[flows.length - 1].snap.master.jars.some(function (j) { return j.uid === lastUid; });
    if (!has) fail('流入事件的 uid 不在该帧主台里');
  }

  /* 交付之后罐子已经不在台上 */
  var delivers = (G.events || []).filter(function (e) { return e.kind === 'deliver'; });
  if (!delivers.length) fail('第一关应当有交付事件');
  delivers.forEach(function (d) {
    var still = d.snap.master.jars.some(function (j) { return j.uid === d.uid; }) ||
      (d.snap.sub && d.snap.sub.jars.some(function (j) { return j.uid === d.uid; }));
    if (still) fail('交付事件 ' + d.shape + '(uid=' + d.uid + ') 之后该罐子还在台上');
  });

  /* 转移：uid 跨台不变 */
  var G5 = ENG.newGame(4);
  ENG.setBench(G5, 'sub', ENG.findBench('xuanwu'));
  var j = G5.master.jars[0];
  if (j) {
    var uid = j.uid;
    ENG.transferJar(G5, j, 'sub');
    var mv = (G5.events || []).filter(function (e) { return e.kind === 'transfer'; }).pop();
    if (!mv) fail('转移没有产生事件');
    else {
      if (mv.uid !== uid) fail('转移事件的 uid 变了（' + uid + ' → ' + mv.uid + '）');
      var inSub = mv.snap.sub.jars.some(function (x) { return x.uid === uid; });
      var inMaster = mv.snap.master.jars.some(function (x) { return x.uid === uid; });
      if (!inSub || inMaster) fail('转移后那一帧：罐子应当只在从台（inSub=' + inSub + ' inMaster=' + inMaster + '）');
    }
  }

  if (bad) { console.log('  => 快照抽查 FAIL ' + bad + ' 处'); process.exitCode = 1; }
  else console.log('  => 快照抽查 OK（每帧都有那一刻的世界、流入逐只、交付即离台、转移 uid 不变）');
})();
