/* 战斗内核无头验证：能不能赢、能不能输、锁死判定会不会误判 */
'use strict';

global.window = global;
require('./puzzle_data.js');
require('./puzzle_engine.js');
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
