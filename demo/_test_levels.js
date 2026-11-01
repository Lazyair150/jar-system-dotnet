/* 分阶段配置测试：模拟玩家在回合之间换台 / 换线，验证第 3、4 关确实可解 */
global.window = global;
require('./puzzle_data.js');
require('./puzzle_engine.js');

var ENG = global.window.ENG;
var B = function (id) { return ENG.findBench(id); };
var P = function (id) { return ENG.findProd(id); };

function apply(G, cfg) {
  if (cfg.master) ENG.setBench(G, 'master', B(cfg.master));
  if (cfg.sub) ENG.setBench(G, 'sub', B(cfg.sub));
  if (cfg.lines) {
    cfg.lines.forEach(function (l, i) {
      G.lines[i] = l ? { prod: P(l.prod), target: l.target || 'master' } : null;
    });
  }
}

function run(lvIdx, phases, label) {
  var G = ENG.newGame(lvIdx);
  console.log('=== ' + label + ' ===');
  var t = 0;
  while (!G.over && t < G.lv.turns) {
    for (var p = 0; p < phases.length; p++) {
      if (phases[p].at === G.turn) apply(G, phases[p].cfg);
    }
    ENG.runTurn(G);
    t++;
    var prog = G.orders.map(function (o) {
      return '#' + (o.idx + 1) + ' ' + o.progress + '/' + o.total + (o.done ? '✓' : '');
    }).join(' ');
    console.log('  T' + G.turn + '  ' + prog + '  | ' + G.master.def.name +
      ' 罐' + G.master.jars.length + ' 液' + JSON.stringify(G.master.liquid) +
      (G.sub ? ' | ' + G.sub.def.name + ' 罐' + G.sub.jars.length : ''));
  }
  var done = G.orders.filter(function (o) { return o.done; }).length;
  console.log('  => ' + (G.over && G.over.win ? 'WIN@' + G.turn : 'FAIL') + ' ' + done + '/' + G.orders.length);
}

/* 第三关：#1 长罐灌酒（青龙产长罐）→ #2 三角罐灌酒（青龙产三角）→ #3 鼓罐（白虎）
   这套配置是 `_plan_levels.js`（计划型打手）算出来的，不是手调的 —— 手调的那套只能做到 2/3。 */
run(2, [
  { at: 1, cfg: { master: 'qinglong', sub: 'xuanwu', lines: [{ prod: 'j长' }, { prod: 'l酒' }, null] } },
  { at: 3, cfg: { master: 'qinglong', sub: 'baihu', lines: [{ prod: 'j三' }, { prod: 'j鼓', target: 'sub' }, null] } },
  { at: 4, cfg: { master: 'qinglong', sub: 'xuanwu', lines: [{ prod: 'j三' }, { prod: 'l酒' }, null] } },
  { at: 5, cfg: { master: 'baihu', sub: 'xuanwu', lines: [{ prod: 'j鼓' }, null, null] } }
], '第三关');

/* 第四关：#1 玄武台 → #2 青龙台 → #3 朱雀台+从台 → #4 白虎台（扁→尖，灌蜜） */
run(3, [
  { at: 1, cfg: { master: 'xuanwu', lines: [{ prod: 'j圆' }, { prod: 'j方' }, { prod: 'l蜜' }] } },
  { at: 3, cfg: { master: 'qinglong', lines: [{ prod: 'j三' }, { prod: 'l酒' }, null] } },
  { at: 6, cfg: { master: 'zhuque', sub: 'qinglong', lines: [{ prod: 'j高' }, { prod: 'l蜜' }, null] } },
  { at: 10, cfg: { master: 'baihu', lines: [{ prod: 'j扁' }, { prod: 'l蜜' }, null] } }
], '第四关');
