/* 分阶段配置测试：模拟玩家在回合之间换台 / 换线 / 转移罐子，验证第三、四、五关确实可解。

   三套配置都不是手调的，是 `_plan_levels.js`（计划型打手）算出来的获胜计划 —— 原因见
   `关卡可解性判定.md`：手调配置打不过时，我无法区分"关卡不可解"和"我没想到"。
   打手赢 ⇒ 有完整通关记录可复核；打手输 ⇒ 只能说"这个搜索预算里没找到"。 */
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
      /* 产线只往主台产（ProductionService.cs:55 唯一调用点是 FlowToMaster） */
      G.lines[i] = l ? { prod: P(l.prod) } : null;
    });
  }
  /* 转移：按主台罐位索引把罐子搬到从台。落横置？不落 —— 转移是物流动作（TransferCommand 不 Mark）。 */
  if (cfg.xfer && cfg.xfer.length && G.sub) {
    var movers = cfg.xfer
      .map(function (i) { return G.master.jars[i]; })
      .filter(Boolean);
    movers.forEach(function (j) { ENG.transferJar(G, j, 'sub'); });
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
      (G.sub ? ' | 从' + G.sub.def.name + ' 罐' + G.sub.jars.length : ''));
  }
  var done = G.orders.filter(function (o) { return o.done; }).length;
  console.log('  => ' + (G.over && G.over.win ? 'WIN@' + G.turn : 'FAIL') + ' ' + done + '/' + G.orders.length);
}

/* 第三关：长罐灌酒（青龙）→ 三角罐灌酒（青龙）→ 鼓罐（白虎） */
run(2, [
  { at: 1, cfg: { master: 'qinglong', sub: 'xuanwu', lines: [{ prod: 'j长' }, { prod: 'l酒' }, null] } },
  { at: 2, cfg: { master: 'qinglong', sub: 'xuanwu', lines: [{ prod: 'j三' }, { prod: 'l酒' }, null] } },
  { at: 3, cfg: { master: 'qinglong', sub: 'xuanwu', lines: [{ prod: 'j三' }, { prod: 'l酒' }, null] } },
  { at: 4, cfg: { master: 'baihu', sub: 'xuanwu', lines: [{ prod: 'j鼓' }, null, null] } },
  { at: 5, cfg: { master: 'baihu', sub: 'xuanwu', lines: [{ prod: 'j鼓' }, null, null] } }
], '第三关');

/* 第四关：高罐(朱雀) → 圆/方罐(玄武) → 三角罐(青龙，转移一只到麒麟从台) → 扁罐(白虎) */
run(3, [
  { at: 1, cfg: { master: 'zhuque', sub: 'xuanwu', lines: [{ prod: 'j高' }, { prod: 'l蜜' }, null] } },
  { at: 2, cfg: { master: 'xuanwu', sub: 'zhuque', lines: [{ prod: 'j圆' }, { prod: 'l蜜' }, null] } },
  { at: 3, cfg: { master: 'xuanwu', sub: 'zhuque', lines: [{ prod: 'j方' }, { prod: 'l蜜' }, null] } },
  { at: 4, cfg: { master: 'qinglong', sub: 'qilin', lines: [{ prod: 'j三' }, { prod: 'l酒' }, null], xfer: [1] } },
  { at: 5, cfg: { master: 'baihu', sub: 'xuanwu', lines: [{ prod: 'j扁' }, { prod: 'l蜜' }, null] } }
], '第四关');

/* 第五关：**转移**。长罐:酒 只能在青龙台的规则下做成，圆罐:蜜 只能在玄武台的规则下做成，
   而两台互不相通、从台开局又是空的 ⇒ 必须把罐子从主台搬到从台。 */
run(4, [
  { at: 1, cfg: { master: 'qinglong', sub: 'xuanwu', lines: [{ prod: 'j长' }, { prod: 'l酒' }, null] } },
  { at: 2, cfg: { master: 'xuanwu', sub: 'zhuque', lines: [{ prod: 'j圆' }, { prod: 'l蜜' }, null] } },
  { at: 3, cfg: { master: 'qinglong', sub: 'xuanwu', lines: [{ prod: 'j长' }, { prod: 'l酒' }, null], xfer: [0] } }
], '第五关');
