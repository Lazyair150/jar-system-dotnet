/* 无头冒烟测试：用最小 DOM 桩跑一遍界面，确认渲染与点击不会抛异常 */
'use strict';

function fakeEl(id) {
  return {
    id: id, innerHTML: '', className: '', parentElement: null, _attrs: {},
    getAttribute: function (k) { return this._attrs[k] === undefined ? null : this._attrs[k]; },
    setAttribute: function (k, v) { this._attrs[k] = v; },
    classList: { add: function () {}, remove: function () {}, toggle: function () {}, contains: function () { return false; } },
    querySelector: function () { return null; },
    querySelectorAll: function () { return []; },
    appendChild: function () {},
    scrollTop: 0, scrollHeight: 0, style: {}
  };
}

var els = {};
var listeners = {};
var body = fakeEl('body');

global.window = global;
global.__NO_ANIM = true;   /* 过程播放不做动画：冒烟只验结构不验节奏 */
global.document = {
  readyState: 'complete',
  body: body,
  getElementById: function (id) { if (!els[id]) els[id] = fakeEl(id); return els[id]; },
  addEventListener: function (t, fn) { (listeners[t] = listeners[t] || []).push(fn); },
  querySelectorAll: function () { return []; },
  querySelector: function () { return null; }
};

require('./puzzle_data.js');
require('./puzzle_engine.js');
require('./event_text.js');
require('./puzzle_ui.js');   /* 立即 boot() → render() */

console.log('boot 完成，topbar 长度 =', els.topbar.innerHTML.length,
            '| masterPanel 长度 =', els.masterPanel.innerHTML.length,
            '| ordersPanel 长度 =', els.ordersPanel.innerHTML.length,
            '| hintbar 长度 =', els.hintbar.innerHTML.length);

var click = listeners.click[0];
function fire(attrs) {
  var el = fakeEl('x');
  Object.keys(attrs).forEach(function (k) { el.setAttribute(k, attrs[k]); });
  click({ target: el });
}

/* 走一遍：换台 → 选台 → 换产线 → 选产线 → 产出一只罐子 → 装备从台 → 转移 → 连按回合到结束 */
fire({ 'data-act': 'pickbench', 'data-side': 'master' });
console.log('打开选台面板，picker 长度 =', els.picker.innerHTML.length);
fire({ 'data-act': 'setbench', 'data-side': 'master', 'data-id': 'xuanwu' });

fire({ 'data-act': 'pickline', 'data-i': '0' });
console.log('打开选产线面板，picker 长度 =', els.picker.innerHTML.length);
fire({ 'data-act': 'setline', 'data-i': '0', 'data-id': 'j圆' });
fire({ 'data-act': 'pickline', 'data-i': '1' });
fire({ 'data-act': 'setline', 'data-i': '1', 'data-id': 'l蜜' });
console.log('产线配置完成，linesPanel 长度 =', els.linesPanel.innerHTML.length);

/* 产线只往主台产（ProductionService.cs:55 唯一调用点是 FlowToMaster）：
   先跑一回合拿到罐子，再装备从台、把它转移过去。 */
fire({ 'data-act': 'turn' });
fire({ 'data-act': 'setbench', 'data-side': 'sub', 'data-id': 'qinglong' });
fire({ 'data-act': 'transfer', 'data-side': 'master', 'data-i': '0' });
console.log('转移后 从台面板长度 =', els.subPanel ? els.subPanel.innerHTML.length : '(无 subPanel)');

var logs = [];
for (var i = 0; i < 6; i++) {
  fire({ 'data-act': 'turn' });
  logs.push('T' + i + ' 订单=' + els.ordersPanel.innerHTML.replace(/<[^>]*>/g, ' ').replace(/\s+/g, ' ').trim().slice(0, 90));
}
logs.forEach(function (l) { console.log('  ' + l); });

/* 跨回合回看：历史回合必须有按钮；点进去停在第一帧；前后走不越界；能回到实时。
   回看态必须拦住推进（棋盘画的是快照，推进等于在副本上按真实世界的按钮）。 */
var stage = els.stagePanel ? els.stagePanel.innerHTML : '';
if (stage.indexOf('data-act="pbturn"') < 0) {
  console.log('  ! 过程面板里没有回看按钮：' + stage.slice(0, 120));
  process.exitCode = 1;
}
fire({ 'data-act': 'pbturn', 'data-i': '0' });
if (els.stagePanel.innerHTML.indexOf('回看 第') < 0) {
  console.log('  ! 点回看后没有进入回看态：' + els.stagePanel.innerHTML.slice(0, 120));
  process.exitCode = 1;
}
fire({ 'data-act': 'pbnext' });
fire({ 'data-act': 'pbprev' });
fire({ 'data-act': 'pbprev' });          /* 已经在第一帧：必须停住，不许走到 0 或负数 */
if (els.stagePanel.innerHTML.indexOf('<span class="evprog">1 /') < 0) {
  console.log('  ! 第一帧继续往前走了：' + els.stagePanel.innerHTML.slice(0, 160));
  process.exitCode = 1;
}
var beforeTurn = els.ordersPanel.innerHTML;
fire({ 'data-act': 'turn' });            /* 回看态下推进必须被拒 */
if (els.ordersPanel.innerHTML !== beforeTurn) {
  console.log('  ! 回看中点推进竟然改动了世界');
  process.exitCode = 1;
}
fire({ 'data-act': 'pblive' });
if (els.stagePanel.innerHTML.indexOf('回看 第') >= 0) {
  console.log('  ! 返回实时后还停在回看态');
  process.exitCode = 1;
}
console.log('回看：进入/前后走/回到实时 全部通过');

/* 新加的可玩性元素：顶栏要报「第 N / 5 关」；第五关无从台时必须提示「需要从台」；
   玩法面板必须已更新到「迁徙 / 转移」这套当前机制。 */
var LVN = window.LEVELS.length;   /* 关卡总数不写死：会加关卡 */
var topText = els.topbar.innerHTML;
if (topText.indexOf('第 1 / ' + LVN + ' 关') < 0) { console.log('  ! 顶栏没有关卡序号：' + topText.slice(0, 80)); process.exitCode = 1; }
fire({ 'data-act': 'restart' });
for (var lv = 0; lv < LVN - 2; lv++) fire({ 'data-act': 'nextlv' });
var top5 = els.topbar.innerHTML;
if (top5.indexOf('第 ' + (LVN - 1) + ' / ' + LVN + ' 关') < 0) { console.log('  ! 跳不到倒数第二关：' + top5.slice(0, 80)); process.exitCode = 1; }
fire({ 'data-act': 'nextlv' });
if (els.topbar.innerHTML.indexOf('第 ' + LVN + ' / ' + LVN + ' 关') < 0) { console.log('  ! 走不到最后一关'); process.exitCode = 1; }
fire({ 'data-act': 'nextlv' });
if (els.topbar.innerHTML.indexOf('第 ' + LVN + ' / ' + LVN + ' 关') < 0) { console.log('  ! 最后一关之后还能越界'); process.exitCode = 1; }
fire({ 'data-act': 'prevlv' });
fire({ 'data-act': 'turn' });
var orders5 = els.ordersPanel.innerHTML.replace(/<[^>]*>/g, ' ');
if (orders5.indexOf('需要从台') < 0 && orders5.indexOf('从台') < 0) {
  console.log('  ! 第五关无从台时没有提示：' + orders5.slice(0, 120));
  process.exitCode = 1;
}
fire({ 'data-act': 'help' });
if (els.picker.innerHTML.indexOf('迁徙') < 0) { console.log('  ! 玩法面板没提到迁徙'); process.exitCode = 1; }
fire({ 'data-act': 'closepicker' });

fire({ 'data-act': 'restart' });
fire({ 'data-act': 'nextlv' });
fire({ 'data-act': 'prevlv' });
fire({ 'data-act': 'help' });
console.log('玩法面板长度 =', els.picker.innerHTML.length);
fire({ 'data-act': 'closepicker' });
if (process.exitCode === 1) console.log('\n❌ 冒烟有失败项（见上面 ！开头的行）');
else console.log('\n✅ 冒烟通过：渲染、选台、选产线、产出、装备从台、转移罐子、连按回合、重开、换关、玩法面板全部无异常');
