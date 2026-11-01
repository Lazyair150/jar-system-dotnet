/* 战斗界面冒烟：用最小 DOM 桩跑一遍真实交互路径 */
'use strict';

var els = {};
function mkEl(id) {
  return {
    id: id, textContent: '', innerHTML: '', className: '',
    style: {}, disabled: false, scrollTop: 0, scrollHeight: 0,
    _click: null,
    addEventListener: function (t, f) { if (t === 'click') this._click = f; }
  };
}
['app','ename','hpfill','hptxt','turn','next','forecast','consline','orders',
 'benches','lines','log','over','overbox','picker','picktitle','pickrows'].forEach(function (id) {
  els[id] = mkEl(id);
});

var docClick = null;
global.document = {
  getElementById: function (id) { return els[id] || (els[id] = mkEl(id)); },
  addEventListener: function (t, f) { if (t === 'click') docClick = f; }
};
global.window = global;
global.closestStub = null;

require('./puzzle_data.js');
require('./puzzle_engine.js');
require('./battle_data.js');
require('./battle_engine.js');

var seed = 777;
Math.random = function () { seed = (seed * 1103515245 + 12345) & 0x7fffffff; return seed / 0x7fffffff; };

require('./battle_ui.js');

/* 模拟点击一个带 data-* 的元素 */
function click(attrs) {
  docClick({
    target: {
      closest: function () {
        return { getAttribute: function (k) { return attrs[k] === undefined ? null : attrs[k]; } };
      }
    }
  });
}

var bad = [];
function check(cond, msg) { if (!cond) bad.push(msg); }

/* 1. 开局应该弹出选对手 */
check(els.picker.className === 'show', '开局没有弹选对手面板');
check(els.picktitle.textContent.indexOf('对手') >= 0, '选对手面板标题不对');
check(els.pickrows.innerHTML.indexOf('锈蚀') >= 0, '选对手面板里没有锈蚀');

/* 2. 选锈蚀 → 进入战斗 */
click({ 'data-act': 'chooseEnemy', 'data-id': 'rust' });
check(els.picker.className === '', '选完对手面板没关');
check(els.ename.textContent === '锈蚀', '敌名没渲染');
check(els.hptxt.textContent === '18 / 18', '血量没渲染，实际=' + els.hptxt.textContent);
check(els.turn.textContent.indexOf('第 1 回合') >= 0, '回合数没渲染');
check(els.orders.innerHTML.indexOf('面值') >= 0, '订单没渲染');
check(els.benches.innerHTML.indexOf('玄武台') >= 0, '主台没渲染');
check(els.lines.innerHTML.indexOf('空槽') >= 0, '产线槽没渲染');
check(els.forecast.innerHTML.indexOf('它下一回合会') >= 0, '预告没渲染');

/* 3. 配产线 */
click({ 'data-act': 'pickprod', 'data-slot': '0' });
check(els.picker.className === 'show', '产线选择器没弹');
check(els.pickrows.innerHTML.indexOf('圆罐产线') >= 0, '产线列表里没有圆罐线');
click({ 'data-act': 'chooseProd', 'data-id': 'j圆' });
check(els.lines.innerHTML.indexOf('圆罐') >= 0, '产线没装上');

click({ 'data-act': 'pickprod', 'data-slot': '1' });
click({ 'data-act': 'chooseProd', 'data-id': 'j方' });

/* 4. 换主台 */
click({ 'data-act': 'pickbench', 'data-side': 'master' });
check(els.picktitle.textContent.indexOf('主台') >= 0, '换台面板标题不对');
click({ 'data-act': 'chooseBench', 'data-id': 'zhuque' });
check(els.benches.innerHTML.indexOf('朱雀台') >= 0, '主台没换成功');

/* 5. 配从台 */
click({ 'data-act': 'pickbench', 'data-side': 'sub' });
click({ 'data-act': 'chooseBench', 'data-id': 'xuanwu' });
check(els.benches.innerHTML.indexOf('从台') >= 0, '从台没渲染');
check(els.benches.innerHTML.indexOf('朱雀台') >= 0, '从台配好后主台丢了');

/* 6. 换回玄武台当主台，连按回合 */
click({ 'data-act': 'pickbench', 'data-side': 'master' });
click({ 'data-act': 'chooseBench', 'data-id': 'xuanwu' });

for (var i = 0; i < 30; i++) {
  els.next._click();
  check(els.log.innerHTML.length > 0, '日志空了');
}

check(els.turn.textContent.indexOf('回合') >= 0, '回合数没更新');
check(els.over.className === 'show' || els.over.className === '', '结局面板状态异常');
check(els.overbox.innerHTML.indexOf('打穿了') >= 0 || els.overbox.innerHTML.indexOf('被锁死了') >= 0,
  '30 回合后没有结局');

/* 7. 重开 */
click({ 'data-act': 'restart' });
check(els.picker.className === 'show', '重开没有弹选对手');
click({ 'data-act': 'chooseEnemy', 'data-id': 'glut' });
check(els.ename.textContent === '贪食', '换对手失败');
check(els.hptxt.textContent === '16 / 16', '贪食血量不对，实际=' + els.hptxt.textContent);

if (bad.length) {
  console.log('❌ 界面冒烟失败：');
  bad.forEach(function (b) { console.log('   · ' + b); });
  process.exit(1);
}
console.log('✅ 战斗界面冒烟通过：选对手、配产线、换主台、配从台、连按 30 回合、结局面板、重开全部无异常');
