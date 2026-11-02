/* 战斗内核无头验证：能不能赢、能不能输、锁死判定会不会误判 */
'use strict';

global.window = global;
require('./puzzle_data.js');
require('./puzzle_engine.js');
require('./event_text.js');
require('./battle_data.js');
require('./battle_engine.js');

/* 固定随机种子，结果可复现 */
var seed = 12345;
Math.random = function () { seed = (seed * 1103515245 + 12345) & 0x7fffffff; return seed / 0x7fffffff; };

var ENG = window.ENG;
var BT = window.BATTLE;
var BENCHES = window.BENCHES;
var PRODS = window.PRODS;

function prodOf(id) { return ENG.findProd(id); }

/* 简易自动驾驶：只做「看起来最赚」的配置，证明战斗本身能赢 */
function autopilot(B) {
  var G = B.G;
  var live = G.orders.filter(function (o) { return !o.done; });
  if (!live.length) return;

  var needShapes = {};
  var needLiquid = {};
  live.forEach(function (o) {
    o.reqs.forEach(function (r) {
      needShapes[r.shape] = true;
      if (r.liquid) needLiquid[r.liquid] = true;
    });
  });

  /* 从台先定：有 B 类订单就配一台「基础形状最互补」的 */
  var hasB = live.some(function (o) { return o.cls === 'B'; });
  if (hasB && (!B.subBase || B.masterBase.id === B.subBase.id)) {
    var alt = BENCHES.filter(function (b) { return b.id !== B.masterBase.id; })[0];
    BT.setBench(B, 'sub', alt);
  }

  /* 主台：分数 = 每张题的长进比例 × 面值 + 基础标记命中需求形状的个数 */
  var bestDef = null, bestScore = -1e9;
  BENCHES.forEach(function (b) {
    var d = BT.effectiveDef(b, B.constraints);
    var pool = G.master.jars.map(function (j) {
      return { shape: j.shape, liquid: j.liquid, ref: j, home: d };
    });
    var liq = {}; window.LIQUIDS.forEach(function (L) { liq[L] = G.master.liquid[L] || 0; });
    var score = 0;
    live.forEach(function (o) {
      var p = pool.slice();
      if (o.cls === 'B' && G.sub) {
        G.sub.jars.forEach(function (j) { p.push({ shape: j.shape, liquid: j.liquid, ref: j, home: d }); });
        window.LIQUIDS.forEach(function (L) { liq[L] += G.sub.liquid[L] || 0; });
      }
      var h = ENG.grow(o, p, d, liq).height;
      score += (h / o.total) * o.value * 10 + (h > 0 ? o.value : 0);
    });
    Object.keys(needShapes).forEach(function (s) {
      if (d.base.indexOf(s) >= 0) score += 3;
    });
    if (score > bestScore) { bestScore = score; bestDef = b; }
  });
  if (bestDef) BT.setBench(B, 'master', bestDef);

  /* 产线：最多三条，优先铺「主台能产 + 场上需要」的罐子线，再补液体线 */
  var mBase = B.masterBase.base;
  var jarCands = PRODS.filter(function (p) {
    return p.kind === 'jar' && needShapes[p.shape] && mBase.indexOf(p.shape) >= 0;
  }).sort(function (a, b) { return b.amount - a.amount; });
  var liqCands = PRODS.filter(function (p) {
    return p.kind === 'liquid' && needLiquid[p.liquid] &&
      (B.masterBase.ban || []).indexOf(p.liquid) < 0;
  });

  var slots = [];
  for (var i = 0; i < jarCands.length && slots.length < 2; i++) slots.push(jarCands[i]);
  for (var j = 0; j < liqCands.length && slots.length < 3; j++) slots.push(liqCands[j]);
  while (slots.length < 3 && jarCands.length) slots.push(jarCands[0]);

  for (var k = 0; k < 3; k++) {
    G.lines[k] = slots[k] ? { prod: slots[k], target: 'master' } : null;
  }
}

function runBattle(enemyId, useAutopilot, maxTurns) {
  var B = BT.newBattle(enemyId);
  var t = 0;
  while (B.status === 'playing' && t < maxTurns) {
    if (useAutopilot) autopilot(B);
    BT.battleTurn(B);
    t++;
  }
  return B;
}

console.log('===== 敌人数据 =====');
window.ENEMIES.forEach(function (e) {
  console.log('  ' + e.name + '  hp=' + e.hp + '  每回合出题=' + e.draw +
    '  约束计划=' + e.plan.length + ' 条  题池=' + e.pool.length);
});

console.log('\n===== 订单面值分布 =====');
var byVal = {};
window.ORDER_POOL.forEach(function (o) { (byVal[o.value] = byVal[o.value] || []).push(o.label); });
Object.keys(byVal).sort().forEach(function (v) {
  console.log('  面值 ' + v + ' × ' + byVal[v].length + '：' + byVal[v].join(' / '));
});

console.log('\n===== 自动驾驶（应该能赢）=====');
window.ENEMIES.forEach(function (e) {
  var B = runBattle(e.id, true, 40);
  console.log('  ' + e.name + ' → ' + B.status.toUpperCase() +
    '  hp=' + Math.max(0, B.hp) + '/' + B.maxHp +
    '  回合=' + B.stats.turns + '  吃掉=' + B.stats.done + '  剩余约束=' + B.constraints.length);
});

console.log('\n===== 摆烂（应该会被锁死输掉）=====');
window.ENEMIES.forEach(function (e) {
  var B = runBattle(e.id, false, 40);
  console.log('  ' + e.name + ' → ' + B.status.toUpperCase() +
    '  hp=' + Math.max(0, B.hp) + '/' + B.maxHp +
    '  回合=' + B.stats.turns + '  约束=' + B.constraints.length +
    (B.msg ? '  「' + B.msg + '」' : ''));
});

console.log('\n===== 锁死不误判抽查 =====');
var ok = 0, bad = 0;
for (var s = 0; s < 5; s++) {
  var B = BT.newBattle('rust');
  for (var t2 = 0; t2 < 6; t2++) {
    autopilot(B);
    var dead = BT.isDeadlocked(B);
    var live = B.G.orders.filter(function (o) { return !o.done; });
    var anyLong = live.some(function (o) {
      return B.G.orders.some(function () { return true; }) && o.progress > 0;
    });
    if (dead && anyLong) { bad++; console.log('  ⚠ 判了锁死但场上还有能长的题'); }
    else ok++;
    BT.battleTurn(B);
    if (B.status !== 'playing') break;
  }
}
console.log('  5 局 × 前 6 回合检查：正常 ' + ok + ' 次，误判 ' + bad + ' 次');
console.log(bad === 0 ? '  ✅ 锁死判定没有误判' : '  ❌ 锁死判定有问题');

/* ============ 战斗复用同一事件流 ============
   战斗界面与解谜界面必须是**同一个世界、同一条帧流、同一份措辞**。
   以前战斗页的"战报"是手拼的 HTML 另存一处（`B.G.log`），于是同一件事在两页有两种说法。
   这一节钉三件事：
   ① 战斗确实把过程发成了帧（带 seq/tick/快照），不是只有结果；
   ② 每个出现的 kind 都能被 event_text.js 念成人话（没登记的会原样露出 kind，这里必须红）；
   ③ 顺序合理：一开战就开战、约束先于伤害、出题/收回都在这条线上。 */
console.log('\n===== 战斗事件流 =====');
(function () {
  var problems = [];
  function bad(m) { problems.push(m); }

  var ET = window.EVENT_TEXT;
  if (!ET) { console.log('  ! event_text.js 没加载，这一节会空转'); process.exitCode = 1; return; }

  var B = BT.newBattle('rust');
  /* 开战帧要在**刚建好**时看：`framesSince` 只覆盖"还在窗口里的回合"（8 回合），
     长局里第一帧早就滚出去了 —— 拿它判"开战帧存在"会假红。 */
  var opening = ENG.framesSince(B.G, 0);
  if (!opening.length || opening[0].kind !== 'battleStart') {
    bad('开战第一帧不是 battleStart（是 ' + (opening[0] ? opening[0].kind : '无') + '）');
  }
  if (opening[0] && ET.describe(opening[0]).indexOf('开战') < 0) bad('开战帧念不出"开战"');

  for (var t = 0; t < 40 && B.status === 'playing'; t++) {
    autopilot(B);
    BT.battleTurn(B);
  }

  /* framesSince(0) 只拿得到"还在窗口里的回合"（历史 8 回合 + 当前）——
     所以这里不假设能看到第一帧，只断言**看到的这些**是完整的帧。 */
  var frames = ENG.framesSince(B.G, 0);
  if (frames.length < 10) bad('战斗帧太少（' + frames.length + '），断言会空转');

  /* ① 帧的信封与快照 */
  frames.forEach(function (f, i) {
    if (typeof f.seq !== 'number') bad('第 ' + i + ' 帧没有 seq');
    if (i && frames[i - 1].seq >= f.seq) bad('帧序号没有严格递增：' + frames[i - 1].seq + ' → ' + f.seq);
    if (f.tick === undefined) bad('帧 #' + f.seq + ' 没有 tick');
    if (!f.snap) bad('帧 #' + f.seq + '（' + f.kind + '）没有世界快照');
  });

  /* ② 每个出现的 kind 都必须有讲解；念出来的不能是裸 kind */
  var seen = {};
  frames.forEach(function (f) {
    seen[f.kind] = (seen[f.kind] || 0) + 1;
    var text = ET.describe(f);
    if (!text || text === f.kind) bad('事件 ' + f.kind + ' 没有被讲解（event_text.js 缺这一条）');
    if (ET.label(f.kind) === f.kind) bad('事件 ' + f.kind + ' 没有中文标签');
  });
  var mustHave = ['enemyDraw', 'note'];
  mustHave.forEach(function (k) { if (!seen[k]) bad('整场战斗里没有出现 ' + k + '（战斗过程没被发成帧？）'); });

  /* ③ 顺序：**同一个回合内**约束生效必须排在伤害之前（先改规则，再结算）。
     不能拿"全局第一次出现"比 —— 第 3 回合的约束排在第 1 回合的伤害后面是天经地义的，
     那种写法会假红（第一版就是这么错的）。 */
  var byTurn = {};
  frames.forEach(function (f, i) {
    var k = f.tick;
    (byTurn[k] = byTurn[k] || []).push({ kind: f.kind, i: i });
  });
  var checkedTurns = 0;
  Object.keys(byTurn).forEach(function (k) {
    var list = byTurn[k];
    var ci = -1, di = -1;
    list.forEach(function (x) {
      if (x.kind === 'constraintIn' && ci < 0) ci = x.i;
      if (x.kind === 'damage' && di < 0) di = x.i;
    });
    if (ci >= 0 && di >= 0) {
      checkedTurns++;
      if (ci > di) bad('第 ' + k + ' 回合：约束生效排在伤害之后（' + ci + ' > ' + di + '）');
    }
  });
  if (!checkedTurns) bad('没有任何一回合同时出现约束与伤害，③ 会空转');

  if (problems.length) {
    problems.forEach(function (m) { console.log('  ! ' + m); });
    console.log('  => 战斗事件流 FAIL ' + problems.length + ' 处');
    process.exitCode = 1;
  } else {
    console.log('  => 战斗事件流 OK（' + frames.length + ' 帧，' +
      Object.keys(seen).sort().join('/') + '；信封完整、措辞齐备、约束先于伤害）');
  }
})();
