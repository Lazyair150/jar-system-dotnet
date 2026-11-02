/* 跨语言规则对拍：原型 JS 的 `ENG.isAllowed` vs 内核 `Domain/WorkshopRulebook`。

   为什么要这道门（这已经是第 6 项待办，也是历史上出过三次同类 bug 的地方）：
   规则在仓库里有三份实现 —— 内核执行器 `Domain/Workshop.cs`、内核规则表 `Domain/WorkshopRulebook.cs`、
   原型 `demo/puzzle_engine.js`。前两者由 `test/JarSystem.Tests/WorkshopRulebookTests.cs` 的 23,328 次
   组合对拍钉住；原型这份之前**没有任何东西**盯着，而"灌液漏了基础标记"那次 bug 就出在这里。

   做法：C# 侧把规则表判定导成夹具（`_rulebook_fixture.json`，600 条），
   JS 侧按同样的网格自己算一遍，逐条比对。夹具里只记"被判定为合法的操作"，
   所以"不允许"就等于"不在列表里" —— 少列和多列都会被抓到。

   重新生成夹具（改过内核规则才需要）：
     dotnet run --project test\JarSystem.Test -c Release -- --dump-rulebook demo/_rulebook_fixture.json
   校验：
     node _check_rulebook.js */
global.window = global;
require('./puzzle_data.js');
require('./puzzle_engine.js');

var fs = require('fs');
var path = require('path');

var FIXTURE = path.join(__dirname, '_rulebook_fixture.json');
if (!fs.existsSync(FIXTURE)) {
  console.log('找不到夹具：' + FIXTURE);
  console.log('先生成：dotnet run --project test\\JarSystem.Test -c Release -- --dump-rulebook demo/_rulebook_fixture.json');
  process.exitCode = 1;
  return;
}

var ENG = global.window.ENG;
var rows = JSON.parse(fs.readFileSync(FIXTURE, 'utf8'));

/* 用夹具里的工作台 id 去 puzzle_data 的 BENCHES 里取同一套能力参数。
   取不到就说明两边关卡数据不一致 —— 那也是要报的错，不许静默跳过。 */
function benchDef(id) {
  for (var i = 0; i < window.BENCHES.length; i++) {
    if (window.BENCHES[i].id === id) return window.BENCHES[i];
  }
  return null;
}

/* 与 C# `DumpRulebook` 完全同一个网格：pourin ×液体 / pourout / reshape ×形状×成本 / reshapefull ×形状 */
function allowedInJs(def, jar, acc) {
  var liquid = {};
  for (var i = 0; i < window.LIQUIDS.length; i++) liquid[window.LIQUIDS[i]] = acc[window.LIQUIDS[i]] || 0;

  var out = [];
  for (var a = 0; a < window.LIQUIDS.length; a++) {
    var l = window.LIQUIDS[a];
    if (ENG.isAllowed(def, jar, { op: 'pourin', liquid: l }, liquid)) out.push('pourin:' + l);
  }
  if (ENG.isAllowed(def, jar, { op: 'pourout' }, liquid)) out.push('pourout');

  for (var s = 0; s < window.SHAPES.length; s++) {
    var to = window.SHAPES[s];
    for (var c = 0; c < window.LIQUIDS.length; c++) {
      var cost = window.LIQUIDS[c];
      if (ENG.isAllowed(def, jar, { op: 'reshape', to: to, costLiquid: cost }, liquid)) {
        out.push('reshape:' + to + ',' + cost);
      }
    }
    if (ENG.isAllowed(def, jar, { op: 'reshapefull', to: to }, liquid)) out.push('reshapefull:' + to);
  }
  return out;
}

var mismatches = 0;
var checked = 0;
var missingBench = {};

for (var r = 0; r < rows.length; r++) {
  var row = rows[r];
  var def = benchDef(row.w);
  if (!def) { missingBench[row.w] = true; continue; }

  /* 夹具的 acc 形如 ["酒:2","蜜:1"]；转回字典 */
  var acc = {};
  for (var k = 0; k < row.acc.length; k++) {
    var parts = row.acc[k].split(':');
    acc[parts[0]] = Number(parts[1]);
  }

  var jar = { shape: row.shape, liquid: row.liquid };
  var js = allowedInJs(def, jar, acc);
  var cs = row.allow;

  var a = js.slice().sort().join('|');
  var b = cs.slice().sort().join('|');
  checked++;
  if (a !== b) {
    mismatches++;
    if (mismatches <= 5) {
      var onlyJs = js.filter(function (x) { return cs.indexOf(x) < 0; });
      var onlyCs = cs.filter(function (x) { return js.indexOf(x) < 0; });
      console.log('  ! ' + row.w + ' 形状=' + row.shape + ' 罐内=' + (row.liquid || '空') +
        ' 账目=[' + row.acc.join(',') + ']');
      if (onlyJs.length) console.log('      JS 多出：' + onlyJs.join(' '));
      if (onlyCs.length) console.log('      内核多出：' + onlyCs.join(' '));
    }
  }
}

var missing = Object.keys(missingBench);
if (missing.length) {
  console.log('  ! 夹具里的工作台在 demo 关卡数据里找不到：' + missing.join(','));
}

console.log('=== 规则对拍（原型 JS ↔ 内核规则表）===');
console.log('  比对 ' + checked + ' 条组合，不一致 ' + mismatches + ' 条');
if (mismatches || missing.length) { console.log('  => 规则对拍 FAIL'); process.exitCode = 1; }
else { console.log('  => 规则对拍 OK'); }
