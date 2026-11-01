/* 关卡可解性回归：用计划型打手（`_plan_levels.js`）逐关验证「在回合上限内可通关」。

   为什么不再用手写的分阶段配置：换台现在**有代价**（台上有资源时走 2 回合迁徙，期间不产出不结算），
   手写的那几套配置每改一次规则就得重调十几处，而且"手调配置打不过"无法区分"关卡不可解"和"我没想到"。
   打手给出的是一条完整可复核的通关记录 —— 它赢就等于可解。

   若要看得更细（每回合用哪台、铺哪条线、搬哪只罐子），跑：
     node _plan_levels.js <关号> */
global.window = global;
require('./puzzle_data.js');
require('./puzzle_engine.js');

var P = require('./_plan_levels.js');

var failed = 0;
for (var i = 0; i < global.LEVELS.length; i++) {
  var lv = global.LEVELS[i];
  var r = P.play(i, false);
  console.log('=== ' + lv.name + ' ===');
  console.log('  => ' + (r.win ? 'WIN@' + r.turn : 'LOSE') + ' ' + r.done + '/' + r.total +
    '  回合上限 ' + lv.turns);
  if (!r.win) failed++;
  /* 第五关的教学点是转移：计划里必须真的出现过「转移#」 */
  if (i === 4 && r.win) {
    var usedXfer = r.plan.some(function (line) { return line.indexOf('转移#') >= 0; });
    console.log('     转移动作被用到：' + (usedXfer ? '是' : '否'));
    if (!usedXfer) failed++;
  }
}
console.log(failed === 0 ? '全部关卡在回合上限内可通关' : ('FAIL ' + failed + ' 处'));
if (failed) process.exitCode = 1;
