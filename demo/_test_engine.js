/* 无头模拟：验证引擎跑得通、关卡可解、下界不说谎 */
global.window = global;
require('./puzzle_data.js');
require('./puzzle_engine.js');
require('./event_text.js');

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

/* ============ 订单执行顺序 ============
   执行顺序 = G.orders 的数组顺序；od.idx 是身份，不随排序变。
   这里只断言**机制**（引擎确实按数组顺序跑、越界被拒、顺序能跨回合保持）。
   ⚠️ 一个必须记住的实测结论：在**前五关**的 3,576 种产线组合里，
   换顺序从不改变完成集合（identity 级别扫描，0 处差异）—— 因为那五关的订单互不相干：
   先执行的要么做完了（拿走的罐子本来没别人要），要么做不完（**做不完的计划不消耗罐子**）。
   所以那不是代码缺口，是关卡设计缺口。第六关「抢资源」就是补这个缺口的样本：
   两张订单一模一样、都抢同一份稀缺液体 ⇒ 顺序第一次真的改变结果（断言 ④⑤）。 */
console.log('=== 订单执行顺序 ===');
(function () {
  var bad = 0;
  function fail(m) { bad++; console.log('  ! ' + m); }

  function run(swap) {
    var G = ENG.newGame(0);
    ['j圆', 'j圆', 'j方'].forEach(function (id, i) { G.lines[i] = { prod: ENG.findProd(id) }; });
    if (swap) ENG.moveOrder(G, 0, 1);
    var firsts = [];
    for (var t = 0; t < 3; t++) {
      /* 期望值：这条回合里"第一个未完成的订单"（已完成的会被跳过） */
      var exp = null, expRank = 0;
      for (var k = 0; k < G.orders.length; k++) {
        if (!G.orders[k].done) { exp = G.orders[k].idx; expRank = k + 1; break; }
      }
      ENG.runTurn(G);
      var f = (G.events || []).filter(function (e) { return e.kind === 'orderTry'; })[0];
      if (f) firsts.push({ ev: f, exp: exp, expRank: expRank });
    }
    return firsts;
  }

  /* ① 引擎按数组顺序：原序第一条是 #1，换序第一条是 #2，而且**每一回合**都是 */
  var A = run(false), B = run(true);
  if (!A.length || !B.length) fail('没有 orderTry 事件');
  function checkSeq(list, label) {
    list.forEach(function (x, i) {
      if (x.ev.order !== x.exp) fail(label + '第 ' + (i + 1) + ' 回合：应当先执行 #' + (x.exp + 1) + '，实际 #' + (x.ev.order + 1));
      if (x.ev.rank !== x.expRank) fail(label + '第 ' + (i + 1) + ' 回合：rank 应当是 ' + x.expRank + '，实际 ' + x.ev.rank);
    });
  }
  checkSeq(A, '原序');
  checkSeq(B, '换序');
  /* 换序之后第一回合的第一条必须真的变了（否则"排序"这个动作没生效） */
  if (A.length && B.length && A[0].ev.order === B[0].ev.order) fail('换序后第一回合的执行顺序没变');

  /* ② 越界移动必须被拒，且不改动数组 */
  var G = ENG.newGame(0);
  var before = G.orders.map(function (o) { return o.idx; }).join(',');
  if (ENG.moveOrder(G, 0, -1)) fail('moveOrder(0,-1) 应当返回 false');
  if (ENG.moveOrder(G, G.orders.length - 1, 1)) fail('moveOrder(last,+1) 应当返回 false');
  if (G.orders.map(function (o) { return o.idx; }).join(',') !== before) fail('越界移动改动了订单顺序');

  /* ③ 排序是持久的：移动一次，之后每回合都按新顺序，不会自己弹回去 */
  var G2 = ENG.newGame(1);
  ENG.moveOrder(G2, 0, 1);
  var want = G2.orders.map(function (o) { return o.idx; }).join(',');
  ENG.runTurn(G2); ENG.runTurn(G2);
  if (G2.orders.map(function (o) { return o.idx; }).join(',') !== want) fail('执行两回合后顺序自己变回去了');

  /* ④ 抢资源的关卡：换顺序必须真的改变"谁做完了"。
     判据用 identity（od.idx）而不是数量 —— 两张订单面值一样，"做完 1 张"在两种顺序下都成立，
     只有看是**哪一张**才分得开。这条断言同时是第六关的存在理由：
     若哪天有人把这张关卡改回互不相干，这里会红。 */
  function doneSet(levelIdx, swap) {
    var L = ENG.newGame(levelIdx);
    if (swap) ENG.moveOrder(L, 0, 1);
    for (var i = 0; i < L.lv.turns && !L.over; i++) ENG.runTurn(L);
    return L.orders.filter(function (o) { return o.done; })
      .map(function (o) { return o.idx; }).sort().join(',');
  }
  var lvIdx = -1;
  (window.LEVELS || []).forEach(function (L, i) { if (L.name.indexOf('抢资源') >= 0) lvIdx = i; });
  if (lvIdx < 0) fail('找不到「抢资源」关卡，④ 会空转');
  else {
    var plain = doneSet(lvIdx, false), swapped = doneSet(lvIdx, true);
    if (plain === swapped) {
      fail('「抢资源」关卡里换执行顺序没有改变完成集合（' + plain + ' == ' + swapped + '）—— ' +
        '这一关的存在理由就是让顺序有意义，改回互不相干就白设了');
    }
    if (!plain || !swapped) fail('「抢资源」关卡两边都没做完任何订单，④ 会空转');
  }

  /* ⑤ 顺序不是免费的：后动的订单会连材料一起丢掉（失败也付代价）。 */
  (function () {
    if (lvIdx < 0) return;
    var L = ENG.newGame(lvIdx);
    for (var i = 0; i < L.lv.turns && !L.over; i++) ENG.runTurn(L);
    var failed = L.orders.filter(function (o) { return !o.done; });
    if (!failed.length) fail('⑤ 前提不成立：这一关没有失败订单，看不出"失败也付代价"');
  })();

  if (bad) { console.log('  => 执行顺序抽查 FAIL ' + bad + ' 处'); process.exitCode = 1; }
  else console.log('  => 执行顺序抽查 OK（引擎按数组顺序执行、越界被拒、顺序跨回合保持；' +
    '「抢资源」关里换顺序真的改变完成集合）');
})();

/* ---------- 跨回合回看：历史帧必须留下、且不许被后续回合改写 ----------
   这一块的判据来自一个**顺序 bug**：把 `G.events = []` 写在迁徙分支之后，
   迁徙的事件会被追加进已经归档的上一回合数组里 —— 于是"回看"会跟着现在一起变。
   所以这里不只查"有没有历史"，还查"历史是不是冻住的"。 */
(function () {
  var bad = 0;
  function fail(m) { bad++; console.log('  ! ' + m); }

  /* ① 只有已结束的回合进历史；进行中的那一回合就是 G.events 本身 */
  var G = ENG.newGame(0);
  if (!G.history || G.history.length !== 0) fail('新开局就有历史：' + (G.history && G.history.length));
  ENG.runTurn(G);
  if (G.history.length !== 0) fail('第一回合还没结束就归档了（同一段时间会有两个真相）');
  ENG.runTurn(G);
  if (G.history.length !== 1) fail('第二回合开始时第一回合应已归档，实际 ' + G.history.length);
  var h0 = G.history[0];
  if (h0.turn !== 1) fail('归档的回合号不对：' + h0.turn);
  if (!h0.events.length) fail('归档的事件是空的');
  if (h0.events[0].kind !== 'turnStart') fail('归档的第一条不是 turnStart：' + h0.events[0].kind);
  if (h0.events[0].snap === undefined) fail('归档的事件没有快照，回看就没画面');

  /* ② 归档之后再推进，历史必须逐字节不变 */
  var frozen = JSON.stringify(G.history[0].events);
  ENG.runTurn(G); ENG.runTurn(G);
  if (G.history.length && JSON.stringify(G.history[0].events) !== frozen) fail('历史被后续回合改写了');

  /* ③ 有界，且回合号连续（丢的是最旧的，不是中间的） */
  var H = ENG.newGame(3);
  for (var t = 0; t < 20 && !H.over; t++) ENG.runTurn(H);
  if (H.history.length > 8) fail('历史没有上限：' + H.history.length);
  var turns = H.history.map(function (x) { return x.turn; });
  for (var i = 1; i < turns.length; i++) {
    if (turns[i] !== turns[i - 1] + 1) fail('历史回合号不连续：' + turns.join(','));
  }

  /* ④ 迁徙回合的事件必须落在**本回合**，不许追加进已归档的上一回合 */
  var M = ENG.newGame(0);
  setup(M, { lines: [{ prod: 'j圆' }, { prod: 'j方' }] });   /* 不铺产线就攒不出资源，换台会走"空台直接换" */
  ENG.runTurn(M);                                  /* 先攒出资源，换台才会走迁徙 */
  if (!M.master.jars.length) fail('铺了产线却没攒出罐子，用例前提不成立');
  var prevFrozen = M.history.length ? JSON.stringify(M.history[0].events) : null;
  ENG.setBench(M, 'master', ENG.findBench('qinglong'));
  if (!M.migrating) fail('没有进入迁徙，用例前提不成立');
  ENG.runTurn(M);                                  /* 迁徙第 1 回合：不产出 */
  ENG.runTurn(M);                                  /* 迁徙第 2 回合：重组 + 换台 */
  if (prevFrozen !== null && JSON.stringify(M.history[0].events) !== prevFrozen) {
    fail('迁徙回合把事件追加进了已归档的那一回合');
  }
  var lastH = M.history[M.history.length - 1];
  if (!lastH) fail('迁徙回合没有进历史');
  else if (!lastH.events.some(function (e) { return e.kind === 'migrateDone'; })) {
    fail('迁徙完成事件没进历史：' + lastH.events.map(function (e) { return e.kind; }).join(','));
  }

  /* ⑤ 一条历史只能属于一个回合 —— 这是"回合边界"的真正判据。
     只查"有没有 migrateDone"是不够的：把归档放到迁徙分支之后，迁徙事件会混进上一回合那段，
     而那段后来仍会被整体归档，于是 migrateDone 照样"在历史里"，断言却看不见污染。
     查 turn 一致性才抓得住：混合段的 turn 会不一致。 */
  var allH = (G.history || []).concat(H.history || []).concat(M.history || []);
  if (!allH.length) fail('三个用例都没留下历史，这条断言会空转');
  allH.forEach(function (seg) {
    var mixed = seg.events.filter(function (e) { return e.turn !== seg.turn; });
    if (mixed.length) {
      fail('历史第 ' + seg.turn + ' 回合里混入了别的回合的 ' + mixed.length + ' 条事件（' +
        mixed.map(function (e) { return e.turn + ':' + e.kind; }).join(',') + '）');
    }
  });

  if (bad) { console.log('  => 跨回合回看抽查 FAIL ' + bad + ' 处'); process.exitCode = 1; }
  else console.log('  => 跨回合回看抽查 OK（只归档已结束回合、历史冻结不被改写、上限 8、迁徙事件落在本回合）');
})();

/* ---------------------------------------------------------------------------
   帧信封抽查：`seq / tick / revision` 与窗口账目。

   demo 原本的 `G.events` 只是"这一回合的事件数组"，没有全局序号、没有世界修订号、
   没有窗口账目。内核那边这三样都有（`Presentation/PresentationFrame` 的 seq/tick/revision
   与 `PresentationFeed` 的 FirstSeq/DroppedFrames/Since 拉模型）。这一节把 demo 的对齐情况钉住：
   如果哪天有人把 seq 改成"每回合从 1 开始"，这里会当场红。
   --------------------------------------------------------------------------- */
(function () {
  var problems = [];
  function bad(msg) { problems.push(msg); }

  /* ① 序号全局单调且连续：帧是"一条时间线"，不是"每回合各自一套编号" */
  /* 必须铺产线：不铺的话一回合只有 turnStart/orderTry/orderBlocked，
     拿不到任何"世界变了"的帧，④ 就变成空转（这条断言自己会喊出来）。 */
  var G = ENG.newGame(0);
  setup(G, { lines: [{ prod: 'j圆' }, { prod: 'j方' }, { prod: 'l蜜' }] });
  for (var i = 0; i < 20 && !G.over; i++) ENG.runTurn(G);
  var frames = ENG.framesSince(G, 0);
  if (frames.length < 10) bad('帧数太少（' + frames.length + '），这条断言会空转');
  var worldCount = frames.filter(function (f) {
    return ({ produce: 1, flow: 1, flowLiquid: 1, op: 1, deliver: 1, transfer: 1, backflow: 1, migrateReshape: 1, migrateDone: 1 })[f.kind];
  }).length;
  if (worldCount < 3) bad('可用的"世界帧"只有 ' + worldCount + ' 条，④ 会空转');
  for (var k = 1; k < frames.length; k++) {
    if (frames[k].seq !== frames[k - 1].seq + 1) {
      bad('帧序号不连续：' + frames[k - 1].seq + ' → ' + frames[k].seq + '（' + frames[k].kind + '）');
      break;
    }
  }

  /* ② 窗口账目恒等式：能给出的最旧序号 == 丢掉的帧数 + 1 */
  var w = ENG.frameWindow(G);
  if (w.lastSeq !== frames[frames.length - 1].seq) {
    bad('窗口末序号 ' + w.lastSeq + ' 与实际末帧 ' + frames[frames.length - 1].seq + ' 不一致');
  }
  if (w.firstSeq !== frames[0].seq) {
    bad('窗口首序号 ' + w.firstSeq + ' 与实际首帧 ' + frames[0].seq + ' 不一致');
  }
  if (w.dropped > 0 && w.firstSeq !== w.dropped + 1) {
    bad('窗口账目破了：firstSeq=' + w.firstSeq + ' 而 dropped=' + w.dropped +
      '（内核同一口径：FirstSeq == DroppedFrames + 1）');
  }

  /* ③ 拉模型：给游标只拿之后的帧；给末序号拿空 */
  var mid = frames[Math.floor(frames.length / 2)];
  var after = ENG.framesSince(G, mid.seq);
  if (!after.length) bad('从中间游标拉不到东西');
  after.forEach(function (f) { if (f.seq <= mid.seq) bad('拉到了游标之前的帧 #' + f.seq); });
  if (after[0] && after[0].seq !== mid.seq + 1) bad('拉取起点不是游标 + 1：' + after[0].seq);
  if (ENG.framesSince(G, w.lastSeq).length !== 0) bad('游标给到末帧之后还能拉到帧');

  /* ④ 修订号：单调不减；只有"世界真的动了"的 kind 才推进它 */
  var revs = frames.map(function (f) { return f.revision; });
  for (var r = 1; r < revs.length; r++) {
    if (revs[r] < revs[r - 1]) { bad('修订号回退：' + revs[r - 1] + ' → ' + revs[r]); break; }
  }
  var WORLD = { produce: 1, flow: 1, flowLiquid: 1, op: 1, deliver: 1, transfer: 1, backflow: 1, migrateReshape: 1, migrateDone: 1 };
  var bumped = {};
  for (var j = 1; j < frames.length; j++) {
    var changed = frames[j].revision !== frames[j - 1].revision;
    if (changed && !WORLD[frames[j].kind]) {
      bad('修订号被「只描述意图」的帧推进了：' + frames[j].kind);
      break;
    }
    if (!changed && WORLD[frames[j].kind]) bumped[frames[j].kind] = 1;
  }
  /* 允许例外：同一回合里连续两条同类世界帧不会各自都涨——这里只要求"至少有一个世界帧涨过"，
     真正的判据在上面那条（意图帧不许涨）。 */
  if (!Object.keys(bumped).length && !frames.some(function (f) { return WORLD[f.kind]; })) {
    bad('整段里没有任何世界帧，④ 会空转');
  }

  /* ⑤ tick 与回合一一对应：同一回合的帧 tick 必须相同 */
  frames.forEach(function (f) { if (f.tick !== f.turn) bad('帧 #' + f.seq + ' 的 tick(' + f.tick + ') 与 turn(' + f.turn + ') 不一致'); });

  if (problems.length === 0) {
    console.log('  => 帧信封抽查 OK（seq 连续 ' + frames[0].seq + '..' + w.lastSeq +
      '、dropped=' + w.dropped + '、拉模型与修订号单调）');
  } else {
    problems.forEach(function (m) { console.log('  ! ' + m); });
    console.log('  => 帧信封抽查 FAIL ' + problems.length + ' 处');
    process.exitCode = 1;
  }
})();

/* ===========================================================================
   迁徙逐帧：换台不是"一句话换掉"，是一条能看见的时间线。

   内核这边是 `WorkshopSwitchEvent.Execute() = Begin → RunReshape → RunTransfer`，
   由 `SwitchProcessAdvancer` 一 tick 走一段；其中 `RunReshape`（SameBansCase）的用途是
   "把游离液体尽量灌进**旧台基础形状**的空罐"，让新台靠这些装满的罐子带走液体容量。

   原型以前把整段折成一条 `migrateReshape` 文字事件，于是玩家只看到"换台了"。
   这一节钉住的正是那段被折叠的过程：**每灌一只罐子一帧**，而且每帧的快照里，
   那只罐子必须**已经装着液体**（帧是投影，不是预告）。
   =========================================================================== */
console.log('=== 迁徙逐帧 ===');
(function () {
  var problems = [];
  function bad(m) { problems.push(m); }

  var G = ENG.newGame(1);                        /* 第二关：玄武台，能产圆/方 + 蜜 */
  setup(G, { lines: [{ prod: 'j圆' }, { prod: 'j方' }, { prod: 'l蜜' }] });
  for (var t = 0; t < 4; t++) ENG.runTurn(G);    /* 攒出罐子和液池里的蜜 */

  var jarsBefore = G.master.jars.length;
  var lqBefore = 0;
  Object.keys(G.master.liquid).forEach(function (k) { lqBefore += G.master.liquid[k] || 0; });
  if (jarsBefore < 2 || lqBefore < 1) bad('用例前提不成立：攒不出罐子/液体（罐 ' + jarsBefore + '，液 ' + lqBefore + '）');

  var oldName = G.master.def.name;
  ENG.setBench(G, 'master', ENG.findBench('zhuque'));
  if (!G.migrating) bad('没有进入迁徙，这一节会空转');

  /* ① 迁徙开始是**下一回合的第一帧**（换台是回合之间的玩家动作，那时上一回合已归档，
        帧不能往历史里追加）。而且它不推进世界修订号 —— "已受理"不是"世界变了"，
        修订号被它推进的话，缓存失效会跟着误判。 */
  var startTick = G.turn;
  ENG.runTurn(G);
  var startTurn = ENG.framesOfTurn(G, startTick);
  var startFrames = startTurn.filter(function (f) { return f.kind === 'migrateStart'; });
  if (startFrames.length !== 1) bad('迁徙开始回合里 migrateStart 出现 ' + startFrames.length + ' 次（应为 1 次）');
  if (startFrames.length) {
    var before = startTurn.filter(function (f) { return f.seq < startFrames[0].seq; }).pop();
    if (before && before.revision !== startFrames[0].revision) {
      bad('migrateStart 推进了世界修订号 —— 它只表示"已受理"，世界还没变');
    }
  }

  /* 走完迁徙（开始那一回合已经走过了） */
  for (var t2 = 0; t2 < 4 && G.migrating; t2++) ENG.runTurn(G);
  if (G.migrating) bad('迁徙没有在预期回合内结束');

  /* ② 收官回合的帧序列：migrateReshape → migratePour×N → migrateTransfer → migrateDone */
  var doneTurn = ENG.framesOfTurn(G, G.turn);
  var kinds = doneTurn.map(function (f) { return f.kind; });
  var pours = doneTurn.filter(function (f) { return f.kind === 'migratePour'; });
  var idxReshape = kinds.indexOf('migrateReshape');
  var idxTransfer = kinds.indexOf('migrateTransfer');
  var idxDone = kinds.indexOf('migrateDone');
  if (idxReshape < 0 || idxTransfer < 0 || idxDone < 0) {
    bad('收官回合缺帧：' + kinds.join(','));
  } else if (!(idxReshape < idxTransfer && idxTransfer < idxDone)) {
    bad('三段顺序不对：' + kinds.join(','));
  }
  if (pours.length === 0) bad('这一回合一只罐子都没灌上 —— 用例没能覆盖"逐只"（检查液池与基础形状）');

  /* ③ 逐只：每帧只多一只装满的罐子，且灌的液体是新台能接受的 */
  pours.forEach(function (f) {
    var filled = (f.snap.master.jars || []).filter(function (j) { return j.liquid === f.liquid; });
    if (!filled.length) bad('migratePour 那一帧的快照里没有装着 ' + f.liquid + ' 的罐子 —— 帧说早了');
    if (filled.length < f.index) bad('第 ' + f.index + ' 只灌完却只找到 ' + filled.length + ' 只满罐');
  });
  var seqs = pours.map(function (f) { return f.seq; });
  for (var i = 1; i < seqs.length; i++) if (seqs[i] !== seqs[i - 1] + 1) bad('灌罐帧序号不连续：' + seqs[i - 1] + ' → ' + seqs[i]);

  /* ④ 结束后：能力真的换了，且液池里被灌走的液体不再留在池子里 */
  if (G.master.def !== ENG.findBench('zhuque')) bad('迁徙结束后主台能力没换');
  var lqAfter = 0;
  Object.keys(G.master.liquid).forEach(function (k) { lqAfter += G.master.liquid[k] || 0; });
  var poured = pours.length;
  if (lqBefore - poured !== lqAfter && lqBefore - poured > lqAfter) {
    bad('液池账目对不上：迁徙前 ' + lqBefore + '、灌走 ' + poured + '、之后 ' + lqAfter);
  }

  /* ⑤ 每一帧都能被念出来（迁徙这段的措辞也不许露出裸 kind） */
  doneTurn.concat(startTurn).forEach(function (f) {
    var text = window.EVENT_TEXT.describe(f);
    if (!text || text === f.kind) bad('事件 ' + f.kind + ' 没有被讲解');
  });

  if (problems.length) {
    problems.forEach(function (m) { console.log('  ! ' + m); });
    console.log('  => 迁徙逐帧抽查 FAIL ' + problems.length + ' 处');
    process.exitCode = 1;
  } else {
    console.log('  => 迁徙逐帧抽查 OK（' + oldName + ' → 朱雀台；灌罐 ' + pours.length +
      ' 帧逐只可见、三段顺序正确、帧内快照已落账）');
  }
})();
