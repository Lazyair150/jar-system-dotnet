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
