/* 流动层跨语言对拍：内核 Warehouse.FlowToMaster ↔ 原型 flowToMaster
   夹具由 `dotnet run --project test/JarSystem.Test -c Release -- --dump-flow demo/_flow_fixture.json` 生成。
   为什么要这道门：2026-11 原型把"产线产出"实现成"命令当前主台生产、形状不合格就丢弃"，
   而当时的跨语言对拍只覆盖了操作层（_check_rulebook.js），流动层一份没有 ⇒ 没有任何检查会红。 */
'use strict';
var fs = require('fs');
global.window = global;
require('./puzzle_data.js');
require('./puzzle_engine.js');

var path = __dirname + '/_flow_fixture.json';
if (!fs.existsSync(path)) {
  console.log('缺夹具：先跑 dotnet run --project test/JarSystem.Test -c Release -- --dump-flow demo/_flow_fixture.json');
  process.exit(1);
}
var fix = JSON.parse(fs.readFileSync(path, 'utf8'));

function sameList(a, b) { return a.length === b.length && a.every(function (x, i) { return x === b[i]; }); }
function sameDict(a, b) {
  var ka = Object.keys(a).sort(), kb = Object.keys(b).sort();
  if (!sameList(ka, kb)) return false;
  return ka.every(function (k) { return a[k] === b[k]; });
}

var bad = 0, checked = 0;
fix.cases.forEach(function (c) {
  checked++;
  var G = {
    master: { def: ENG.findBench(c.bench), jars: [], liquid: { '酒': 0, '蜜': 0 } },
    wh: ENG.newWarehouse(),
    log: [], lines: [], orders: [], turn: 1, over: null,
    feed: ENG.newFeed(), rev: 0
  };
  c.whJars.forEach(function (s) { G.wh.jars.push({ shape: s, liquid: null }); });
  Object.keys(c.whLiquid).forEach(function (l) { G.wh.liquid[l] = c.whLiquid[l]; });

  ENG.flowToMaster(G);

  var gotJars = G.master.jars.map(function (j) { return j.shape; });
  var gotWh = G.wh.jars.map(function (j) { return j.shape; });
  var problems = [];
  if (!sameList(gotJars, c.masterJars)) problems.push('主台罐 ' + JSON.stringify(gotJars) + ' ≠ ' + JSON.stringify(c.masterJars));
  if (!sameDict(G.master.liquid, c.masterLiquid)) problems.push('主台液体 ' + JSON.stringify(G.master.liquid) + ' ≠ ' + JSON.stringify(c.masterLiquid));
  if (!sameList(gotWh, c.whJarsLeft)) problems.push('仓库剩余罐 ' + JSON.stringify(gotWh) + ' ≠ ' + JSON.stringify(c.whJarsLeft));
  if (!sameDict(G.wh.liquid, c.whLiquidLeft)) problems.push('仓库剩余液体 ' + JSON.stringify(G.wh.liquid) + ' ≠ ' + JSON.stringify(c.whLiquidLeft));
  if (G.wh.discardedJars !== c.discardedJars) problems.push('丢弃罐 ' + G.wh.discardedJars + ' ≠ ' + c.discardedJars);

  if (problems.length) {
    bad++;
    console.log('  ! ' + c.bench + ' whJars=' + JSON.stringify(c.whJars));
    problems.forEach(function (p) { console.log('      ' + p); });
  }
});

if (bad) {
  console.log('FLOW-MISMATCH：比对 ' + checked + ' 例，不一致 ' + bad + ' 例');
  process.exitCode = 1;
} else {
  console.log('FLOW-OK (cases=' + checked + ', 内核与原型逐例一致)');
}
