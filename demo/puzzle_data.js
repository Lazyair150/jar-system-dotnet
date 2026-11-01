/* 陶罐解谜 · 静态数据
   全部规则来自原系统：形状/液体来自 Config/Vocabulary.cs，工作台标记来自 Config/Rules.cs，
   形状转换来自 Config/Rules.cs:31-48 GetPossibleTransforms。 */

window.SHAPES = ['圆罐', '方罐', '三角罐', '高罐', '长罐', '扁罐', '鼓罐', '尖罐'];
window.LIQUIDS = ['酒', '蜜'];

/* 单字简称，界面用 */
window.SHORT = {
  '圆罐': '圆', '方罐': '方', '三角罐': '三', '高罐': '高',
  '长罐': '长', '扁罐': '扁', '鼓罐': '鼓', '尖罐': '尖'
};

/* 工作台：baseMarks 决定「入罐 / 改形的落点」，additionalMarks 是死胡同（只能退回基础），
   ban 是被禁液体（既不能倒入、也不能存储、也不能当改形成本）。
   pool = 每种液体各自的存储上限（原系统 Domain/Workshop.cs:174-181 AddLiquid 的 MaxLiquids）。 */
window.BENCHES = [
  { id: 'xuanwu', name: '玄武台', base: ['圆罐', '方罐'], add: ['三角罐'], ban: ['酒'], pool: 5 },
  { id: 'zhuque', name: '朱雀台', base: ['高罐'], add: ['长罐'], ban: ['酒'], pool: 4 },
  { id: 'qinglong', name: '青龙台', base: ['三角罐', '长罐'], add: ['扁罐'], ban: ['蜜'], pool: 4 },
  { id: 'baihu', name: '白虎台', base: ['扁罐', '鼓罐'], add: ['尖罐'], ban: ['酒'], pool: 4 },
  { id: 'qilin', name: '麒麟台', base: ['尖罐', '圆罐'], add: ['方罐'], ban: ['蜜'], pool: 5 }
];

/* 产线：把东西产进主台。amount = 每回合产量。
   罐子产线不看 baseMarks（产线是基础设施，不是「入罐」操作）；
   液体产线受主台禁令约束（被禁液体不能存储）。 */
window.PRODS = [
  { id: 'j圆', kind: 'jar', shape: '圆罐', amount: 2 },
  { id: 'j方', kind: 'jar', shape: '方罐', amount: 2 },
  { id: 'j三', kind: 'jar', shape: '三角罐', amount: 1 },
  { id: 'j高', kind: 'jar', shape: '高罐', amount: 2 },
  { id: 'j长', kind: 'jar', shape: '长罐', amount: 2 },
  { id: 'j扁', kind: 'jar', shape: '扁罐', amount: 1 },
  { id: 'j鼓', kind: 'jar', shape: '鼓罐', amount: 1 },
  { id: 'j尖', kind: 'jar', shape: '尖罐', amount: 1 },
  { id: 'l酒', kind: 'liquid', liquid: '酒', amount: 2 },
  { id: 'l蜜', kind: 'liquid', liquid: '蜜', amount: 2 }
];

/* 常量 */
window.C = {
  MAX_JARS: 6,      /* 主台罐位上限 */
  MAX_LINES: 3,     /* 产线槽数 */
  MAX_SUB_JARS: 4   /* 从台罐位上限 */
};

/* 关卡。reqs 里 liquid 为 null 表示「空罐」，否则表示「必须装着这种液体」。 */
window.LEVELS = [
  {
    name: '第一关 · 学会改形',
    hint: '玄武台。圆罐和方罐是它的基础标记，三角罐是附加标记 —— 只能靠改形得到。',
    turns: 5,
    benches: ['xuanwu', 'zhuque', 'qinglong'],
    prods: ['j圆', 'j方', 'j三', 'j高', 'j长', 'l酒', 'l蜜'],
    startJars: [],
    startLiquid: {},
    orders: [
      { cls: 'A', reqs: [{ shape: '圆罐', liquid: null }, { shape: '圆罐', liquid: null }] },
      { cls: 'A', reqs: [{ shape: '方罐', liquid: null }, { shape: '三角罐', liquid: null }] }
    ]
  },
  {
    name: '第二关 · 从台与装液',
    hint: '主台用玄武台做三角罐（改形），从台用朱雀台做高罐（高是它的基础标记）。蜜要两路产。',
    turns: 6,
    benches: ['xuanwu', 'zhuque', 'qinglong', 'qilin'],
    prods: ['j圆', 'j方', 'j三', 'j高', 'j长', 'j扁', 'l酒', 'l蜜'],
    startJars: [],
    startLiquid: { '蜜': 1 },
    orders: [
      { cls: 'A', reqs: [{ shape: '三角罐', liquid: null }, { shape: '圆罐', liquid: '蜜' }] },
      { cls: 'B', reqs: [{ shape: '高罐', liquid: null }, { shape: '高罐', liquid: '蜜' }] },
      { cls: 'A', reqs: [{ shape: '圆罐', liquid: '蜜' }, { shape: '圆罐', liquid: '蜜' }] }
    ]
  },
  {
    name: '第三关 · 禁令逼你换台',
    hint: '分三段换主台：玄武台做三角罐（蜜）→ 青龙台做扁罐（三角改形过去，灌酒）→ 白虎台做鼓罐（鼓是它的基础标记）。换台之后旧形状的罐子会留下，它们在新台下多半只能改到新台的基础形状上，所以换台前别把罐位塞满。',
    turns: 10,
    benches: ['xuanwu', 'zhuque', 'qinglong', 'baihu', 'qilin'],
    prods: ['j圆', 'j方', 'j三', 'j高', 'j长', 'j扁', 'j鼓', 'j尖', 'l酒', 'l蜜'],
    startJars: [],
    startLiquid: {},
    orders: [
      { cls: 'B', reqs: [{ shape: '扁罐', liquid: null }, { shape: '长罐', liquid: '酒' }] },
      { cls: 'A', reqs: [{ shape: '三角罐', liquid: null }, { shape: '三角罐', liquid: '酒' }] },
      { cls: 'A', reqs: [{ shape: '鼓罐', liquid: null }, { shape: '鼓罐', liquid: null }] }
    ]
  },
  {
    name: '第四关 · 四种活',
    hint: '四段活，每段换一次主台：玄武台(圆/方，灌蜜) → 青龙台(三角/长，灌酒) → 朱雀台(高，加从台做尖罐) → 白虎台(扁/鼓，扁改形到尖，灌蜜)。',
    turns: 14,
    benches: ['xuanwu', 'zhuque', 'qinglong', 'baihu', 'qilin'],
    prods: ['j圆', 'j方', 'j三', 'j高', 'j长', 'j扁', 'j鼓', 'j尖', 'l酒', 'l蜜'],
    startJars: [],
    startLiquid: {},
    orders: [
      { cls: 'A', reqs: [{ shape: '圆罐', liquid: '蜜' }, { shape: '方罐', liquid: '蜜' }] },
      { cls: 'A', reqs: [{ shape: '三角罐', liquid: null }, { shape: '三角罐', liquid: '酒' }] },
      { cls: 'B', reqs: [{ shape: '高罐', liquid: null }, { shape: '高罐', liquid: '蜜' }] },
      { cls: 'B', reqs: [{ shape: '尖罐', liquid: null }, { shape: '扁罐', liquid: '蜜' }] }
    ]
  },
  {
    /* 教学目的：**转移**。B 类订单可以一部分在主台做、一部分在从台做，
       而"在哪台做"是被规则锁死的：
         · 长罐:酒 —— 长罐是青龙台的基础标记，圆罐在青龙台根本变不出来；且青龙台忌蜜。
         · 圆罐:蜜 —— 圆罐是玄武台的基础标记，玄武台不忌蜜；而玄武台忌酒、长罐也变不出来。
       所以这条订单必须**主台玄武 + 从台青龙**同时干，而从台开局是空的 ⇒
       罐子只能从主台**转移**过去（原系统 TransferCommand）。产线只往主台产。 */
    name: '第五关 · 转移',
    hint: '这一单在主台做不完：长罐只有在青龙台才算基础标记（能灌酒），圆罐只有在玄武台才算基础标记（能灌蜜），而青龙台忌蜜、玄武台忌酒。所以要从台来干另一半 —— 但从台开局是空的，罐子得从主台**转移**过去。注意：产线只往主台产，罐子换了台之后按新台的规则判定。',
    turns: 10,
    benches: ['xuanwu', 'zhuque', 'qinglong', 'baihu', 'qilin'],
    prods: ['j圆', 'j方', 'j三', 'j高', 'j长', 'j扁', 'j鼓', 'j尖', 'l酒', 'l蜜'],
    startJars: [],
    startLiquid: {},
    orders: [
      { cls: 'B', reqs: [{ shape: '圆罐', liquid: '蜜' }, { shape: '长罐', liquid: '酒' }] },
      { cls: 'B', reqs: [{ shape: '长罐', liquid: null }, { shape: '长罐', liquid: null }] }
    ]
  }
];
