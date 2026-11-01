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

/* 走一遍：换台 → 选台 → 换产线 → 选产线 → 切换产线目标 → 连按回合到结束 */
fire({ 'data-act': 'pickbench', 'data-side': 'master' });
console.log('打开选台面板，picker 长度 =', els.picker.innerHTML.length);
fire({ 'data-act': 'setbench', 'data-side': 'master', 'data-id': 'xuanwu' });

fire({ 'data-act': 'pickline', 'data-i': '0' });
console.log('打开选产线面板，picker 长度 =', els.picker.innerHTML.length);
fire({ 'data-act': 'setline', 'data-i': '0', 'data-id': 'j圆' });
fire({ 'data-act': 'pickline', 'data-i': '1' });
fire({ 'data-act': 'setline', 'data-i': '1', 'data-id': 'l蜜' });
fire({ 'data-act': 'toggleline', 'data-i': '1' });
console.log('产线配置完成，linesPanel 长度 =', els.linesPanel.innerHTML.length);

var logs = [];
for (var i = 0; i < 6; i++) {
  fire({ 'data-act': 'turn' });
  logs.push('T' + i + ' 订单=' + els.ordersPanel.innerHTML.replace(/<[^>]*>/g, ' ').replace(/\s+/g, ' ').trim().slice(0, 90));
}
logs.forEach(function (l) { console.log('  ' + l); });

fire({ 'data-act': 'restart' });
fire({ 'data-act': 'nextlv' });
fire({ 'data-act': 'prevlv' });
fire({ 'data-act': 'help' });
console.log('玩法面板长度 =', els.picker.innerHTML.length);
fire({ 'data-act': 'closepicker' });
console.log('\n✅ 冒烟通过：渲染、选台、选产线、切换目标、连按回合、重开、换关、玩法面板全部无异常');
