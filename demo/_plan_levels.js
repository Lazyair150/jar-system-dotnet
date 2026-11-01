/* 自动打手：逐回合规划「用哪台 + 铺什么线」，用 1 回合前瞻做决策。
   目的不是做一个 AI，而是**判定关卡是否可解** —— 贪心打手打不过不等于不可解，
   但也只有换一个更强的打手才谈得上"证明可解"。

   决策方式（每回合）：
     1) 生成候选配置：主台 × 从台 × 产线集合（0~2 条，带目标）
     2) 用**领域启发式**给每个候选打分（便宜）
     3) 取前 K 名，各克隆一份真跑一回合，用「已完成订单数 + 总进度 + 罐位占用」复核，取最优
     4) 应用最优配置，跑掉这一回合

   为什么先启发式再仿真：候选量级在千级，逐个仿真太慢；而"这台能不能产出这条需求要的形状/液体"
   本身就是很强的信号，用它筛到 K=40 再仿真，成本降到可接受。 */
global.window = global;
require('./puzzle_data.js');
require('./puzzle_engine.js');

var ENG = global.window.ENG;
var LIQ = global.window.SHAPES ? global.LIQUIDS || ['酒', '蜜'] : ['酒', '蜜'];

function B(id) { return ENG.findBench(id); }
function P(id) { return ENG.findProd(id); }

/* ---------- 关卡数据自检：这四个"装满某液体的形状"到底有没有台做得出来 ---------- */
function filledRequirementIsPossible(lv, shape, liquid) {
  return lv.benches.some(function (bid) {
    var d = B(bid);
    return d.base.indexOf(shape) >= 0 && d.ban.indexOf(liquid) < 0;
  });
}

/* ---------- 需求画像：这一关还缺什么 ---------- */
function remainingNeeds(G) {
  var shapes = {}, liquids = {};
  G.orders.forEach(function (o) {
    if (o.done) return;
    var left = o.total - o.progress;
    if (left <= 0) return;
    o.reqs.forEach(function (r) {
      shapes[r.shape] = (shapes[r.shape] || 0) + 1;
      if (r.liquid) liquids[r.liquid] = (liquids[r.liquid] || 0) + 1;
    });
  });
  return { shapes: shapes, liquids: liquids };
}

/* ---------- 候选产线集合 ---------- */
function lineCandidates(prods) {
  var out = [];
  out.push([null, null, null]);
  prods.forEach(function (p) {
    for (var i = 0; i < 3; i++) {
      var a = [null, null, null]; a[i] = { prod: p, target: 'master' };
      out.push(a);
    }
  });
  /* 两条线的组合。**没有 target 维度**：产线只往主台产
     （原系统 ProductionService.cs:55 唯一调用点是 Warehouse.FlowToMaster(master, ...)），
     从台的罐子只能靠 transferJar 搬过去。 */
  for (var i = 0; i < prods.length; i++) {
    for (var j = 0; j < prods.length; j++) {
      if (i === j) continue;
      out.push([{ prod: prods[i] }, { prod: prods[j] }, null]);
    }
  }
  return out;
}

/* ---------- 领域启发式打分（不仿真） ---------- */
function heuristic(G, cfg, needs) {
  var mb = B(cfg.master), sb = cfg.sub ? B(cfg.sub) : null;
  var s = 0;
  cfg.lines.forEach(function (l) {
    if (!l) return;
    var pr = P(l.prod);
    var target = mb;   /* 产线只往主台产；findBench 返回的就是 def 本身 */
    if (!target) { s -= 5; return; }
    if (pr.kind === 'jar') {
      /* 这条罐子线产出的形状，是不是我们缺的？是不是目标台的基础标记？ */
      var sh = pr.shape;
      if (needs.shapes[sh]) s += 3 * needs.shapes[sh];
      if (target.base.indexOf(sh) < 0) s -= 8;          /* 不是基础标记 ⇒ 产不进去 */
      if (target.add.indexOf(sh) >= 0 && target.base.indexOf(sh) < 0) s -= 4;
    } else {
      var liq = pr.liquid;
      if (needs.liquids[liq]) s += 3 * needs.liquids[liq];
      if (target.ban.indexOf(liq) >= 0) s -= 8;          /* 这个台忌这种液体 */
    }
  });
  /* 靠后的需求更容易卡住：给"还缺很多"的台一点点偏好，避免主台乱跳 */
  if (cfg.sub && sb) s += 0.5;
  return s;
}

/* ---------- 用真跑一回合来复核（只看前 K 名） ---------- */
function simulate(G, cfg) {
  var H = JSON.parse(JSON.stringify(G));
  apply(H, cfg);
  ENG.runTurn(H);
  return H;
}

function apply(G, cfg) {
  if (cfg.master) ENG.setBench(G, 'master', B(cfg.master));
  /* setBench('sub', null) 会在引擎里崩（它把 null 当 def 去读 ban）——
     所以「不设从台」的含义定为"保持现状"。反正从台换了是免费的，保持即可。 */
  if (cfg.sub) ENG.setBench(G, 'sub', B(cfg.sub));
  if (cfg.lines) {
    for (var i = 0; i < 3; i++) {
      var l = cfg.lines[i];
      G.lines[i] = l ? { prod: P(l.prod) } : null;
    }
  }
  /* 转移：按**索引**取克隆自己的罐子再搬（simulate 是 JSON 深拷贝，引用对不上）。 */
  if (cfg.xfer && cfg.xfer.length && G.sub) {
    var movers = [];
    for (var k = 0; k < cfg.xfer.length; k++) {
      var idx = cfg.xfer[k];
      if (idx >= 0 && idx < G.master.jars.length) movers.push(G.master.jars[idx]);
    }
    for (var m = 0; m < movers.length; m++) ENG.transferJar(G, movers[m], 'sub');
  }
}

/* 转移变体：不搬，或把主台第 i 只罐子搬到从台（只试前 4 只，够用且不让搜索爆掉）。
   为什么要把转移放进候选而不是固定策略：罐子换台 ⇒ 之后按新台的规则判定，
   哪只该搬取决于那条订单缺什么，靠启发式猜不准，让真跑一回合来选。 */
function xferVariants(G) {
  var out = [{ }];
  if (!G.sub) return out;
  var n = Math.min(4, G.master.jars.length);
  for (var i = 0; i < n; i++) out.push({ xfer: [i] });
  return out;
}

function value(G) {
  var done = 0, prog = 0;
  G.orders.forEach(function (o) { if (o.done) done++; prog += o.progress; });
  return done * 1000 + prog * 10 + G.master.jars.length + (G.sub ? G.sub.jars.length : 0) * 0.5;
}

/* ---------- 一关的完整流程 ---------- */
function play(lvIdx, verbose) {
  var G = ENG.newGame(lvIdx);
  var lv = G.lv;
  var prods = lv.prods;
  var lines = lineCandidates(prods);
  var log = [];

  while (!G.over && G.turn < lv.turns) {
    var needs = remainingNeeds(G);
    /* 只在缺东西的形状/液体上考虑产线，砍掉无关组合 */
    var curProds = prods.filter(function (pid) {
      var pr = P(pid);
      if (pr.kind === 'jar') return !!needs.shapes[pr.shape];
      return !!needs.liquids[pr.liquid];
    });
    if (!curProds.length) curProds = prods;
    var curLines = lineCandidates(curProds);

    /* 1) 启发式筛 */
    var cands = [];
    lv.benches.forEach(function (mb) {
      var subs = [null].concat(lv.benches.filter(function (x) { return x !== mb; }));
      subs.forEach(function (sb) {
        curLines.forEach(function (ls) {
          var cfg = { master: mb, sub: sb, lines: ls };
          cands.push({ cfg: cfg, h: heuristic(G, cfg, needs) });
        });
      });
    });
    cands.sort(function (a, b) { return b.h - a.h; });

    /* 2) 前 K 名真跑一回合 */
    var K = Math.min(40, cands.length), best = null, bestV = -1e9;
    for (var i = 0; i < K; i++) {
      var variants = xferVariants(G);
      for (var v = 0; v < variants.length; v++) {
        var cfg = { master: cands[i].cfg.master, sub: cands[i].cfg.sub, lines: cands[i].cfg.lines, xfer: variants[v].xfer };
        var H = simulate(G, cfg);
        var val = value(H);
        if (val > bestV) { bestV = val; best = cfg; }
      }
    }
    if (!best) best = cands.length ? cands[0].cfg : { master: lv.benches[0], sub: null, lines: [null, null, null] };

    apply(G, best);
    log.push('T' + (G.turn + 1) + ' ' + best.master + (best.sub ? '+' + best.sub : '') + ' ' +
      best.lines.map(function (l) { return l ? l.prod : '-'; }).join(',') +
      (best.xfer && best.xfer.length ? '  转移#' + best.xfer.join('+') : ''));
    ENG.runTurn(G);
    if (verbose) {
      console.log('  T' + G.turn + '  ' + G.orders.map(function (o) {
        return '#' + (o.idx + 1) + ' ' + o.progress + '/' + o.total + (o.done ? '✓' : '');
      }).join(' ') + '  | ' + G.master.def.name + ' 罐' + G.master.jars.length +
        ' 液' + JSON.stringify(G.master.liquid) + (G.sub ? ' | 从' + G.sub.def.name : ''));
    }
  }
  return { win: !!(G.over && G.over.win), turn: G.turn, done: G.orders.filter(function (o) { return o.done; }).length, total: G.orders.length, plan: log, G: G };
}

/* ---------- 导出（供 _test_levels.js 当"可解性判定器"用） ---------- */
module.exports = { play: play, filledRequirementIsPossible: filledRequirementIsPossible };

/* ---------- 直接运行时：跑全部关卡 ---------- */
if (require.main !== module) { return; }
var only = process.argv[2] ? Number(process.argv[2]) : null;
var allWin = true;
for (var i = 0; i < global.LEVELS.length; i++) {
  var lv = global.LEVELS[i];
  if (only !== null && only !== i + 1) continue;
  console.log('=== L' + (i + 1) + ' ' + lv.name + ' ===');
  var r = play(i, only !== null);
  console.log('  => ' + (r.win ? 'WIN@' + r.turn : 'LOSE') + ' ' + r.done + '/' + r.total +
    '  回合上限 ' + lv.turns);
  if (!r.win) allWin = false;
  if (only !== null) r.plan.forEach(function (x) { console.log('    ' + x); });
}
console.log(allWin ? 'ALL-WIN' : 'NOT-ALL-WIN');
