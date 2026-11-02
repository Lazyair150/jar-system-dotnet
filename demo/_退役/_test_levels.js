/* 关卡可解性 + 难度预算回归：用计划型打手（`_plan_levels.js`）逐关验证
   「在回合上限内可通关」，并钉住**回合上限与打手成绩的差值**。

   为什么不再用手写的分阶段配置：换台现在**有代价**（台上有资源时走 2 回合迁徙，期间不产出不结算），
   手写的那几套配置每改一次规则就得重调十几处，而且"手调配置打不过"无法区分"关卡不可解"和"我没想到"。
   打手给出的是一条完整可复核的通关记录 —— 它赢就等于可解。

   为什么要钉差值：`turns` 是手填的数字，打手成绩却会随规则/引擎改动漂移。
   两者一旦分家，屏幕上只会出现"这关突然变得很难/很简单"，没有任何东西会红。
   所以把"余量"写成**设计意图**（这一关允许玩家试错几次），并断言
   `turns === 打手成绩 + 余量` —— 改引擎后打手快了/慢了，这里立刻指出该重填哪个数。

   若要看得更细（每回合用哪台、铺哪条线、搬哪只罐子），跑：
     node _plan_levels.js <关号> */
global.window = global;
require('./puzzle_data.js');
require('./puzzle_engine.js');

var P = require('./_plan_levels.js');

/* 余量 = 这一关允许玩家试错几个回合。
   注意它**不是**难度曲线：第五关打手只用 9 回合，比第四关的 12 少，
   因为第五关教的是"转移"这个动作，不是难度峰值；第六关打手只要 1 回合，
   但它教"顺序会改变结果"，玩家很可能先按错顺序跑一回合才看懂谁先动 —— 所以余量给 3。 */
var MARGINS = [
  { m: 1, why: '教学关：一次改形就走完，允许一次试错' },
  { m: 1, why: '从台 + 装液，两条需求，一次试错足够' },
  { m: 1, why: '禁令逼换台，而换台要占 2 回合，余量不能再省' },
  { m: 2, why: '最长的一关（四条需求、跨三种活），允许两次试错' },
  { m: 1, why: '转移关：想到转移就顺，想到之前怎么试都没用' },
  { m: 3, why: '抢资源关：玩家很可能先按错顺序跑一回合才看懂"谁先动"' }
];

var failed = 0;
if (MARGINS.length !== global.LEVELS.length) {
  console.log('! 难度预算表有 ' + MARGINS.length + ' 项，而关卡有 ' + global.LEVELS.length +
    ' 关 —— 加关卡时必须同时给出这一关的余量（否则余量会悄悄变成"随手写的数"）');
  failed++;
}

console.log('关 | 上限 | 打手 | 余量 | 结论');
for (var i = 0; i < global.LEVELS.length; i++) {
  var lv = global.LEVELS[i];
  var r = P.play(i, false);
  var pol = MARGINS[i] || { m: NaN, why: '（未登记）' };
  var slack = lv.turns - r.turn;
  var ok = r.win && slack === pol.m;
  if (!ok) failed++;

  console.log('L' + (i + 1) + ' ' + lv.name + ' | ' + lv.turns + ' | ' +
    (r.win ? 'WIN@' + r.turn : 'LOSE') + ' ' + r.done + '/' + r.total + ' | ' +
    (isNaN(slack) ? '?' : slack) + ' | ' + (ok ? 'OK' : 'FAIL'));

  if (!r.win) {
    console.log('    ! 打手没赢：关卡不可解，或在回合上限内不可解');
  } else if (slack !== pol.m) {
    console.log('    ! 余量不符：上限 ' + lv.turns + ' − 打手 ' + r.turn + ' = ' + slack +
      '，而设计意图是 ' + pol.m + '（' + pol.why + '）');
  } else if (pol.why) {
    console.log('    余量 ' + pol.m + '：' + pol.why);
  }

  /* 第五关的教学点是转移：计划里必须真的出现过「转移#」 */
  if (i === 4 && r.win) {
    var usedXfer = r.plan.some(function (line) { return line.indexOf('转移#') >= 0; });
    console.log('    ' + (usedXfer ? 'OK' : '!') + ' 转移动作被用到：' + (usedXfer ? '是' : '否'));
    if (!usedXfer) failed++;
  }
}

console.log(failed === 0
  ? '全部关卡在回合上限内可通关，且余量与设计意图一致'
  : ('FAIL ' + failed + ' 处'));
if (failed) process.exitCode = 1;
