/* 世界视图（S2/S3）：**只读表达器 + 内核命令面板**。
   ---------------------------------------------------------------------------
   这一页只做两件事，全部来自内核，页面里没有任何规则：
   1 显示：`getState` 的世界、WebSocket 的 `frame`（核心 PresentationFeed 的投影）、
      `GET /api/invariants` 的自洽性检查结果。
   2 转发玩家意图：把点击翻译成**内核命令**（setMaster / setSlave / createRandomOrder /
      checkFeasibility / startOrder / cancelProcess / deleteOrder / manualTick / startTick / stopTick）。

   一条硬边界：**页面不推导、不补全、不猜测**。
   - 它不因为"某订单看起来能执行"而显示进度 —— 进度来自 `checkFeasibility` 的答复；
   - 它不因为"某工作台看起来缺罐子"而报警 —— 那是 `WorldInvariants` 的职责；
   - 世界变化时它只是**再问一次内核**（getState + /api/invariants），不自己算。
*/
(function () {
  'use strict';

  var MAX_FRAMES = 300;
  var frames = [];
  var sock = null;
  var lastState = null;

  function $(id) { return document.getElementById(id); }
  function esc(s) {
    return String(s === null || s === undefined ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/"/g, '&quot;');
  }
  function send(type, data) {
    if (sock && sock.readyState === 1) sock.send(JSON.stringify({ type: type, data: data || {} }));
  }

  /* ---------------- 帧时间线 ---------------- */
  function renderFrames() {
    var el = $('frames');
    if (!frames.length) { el.innerHTML = '<div class="empty">等待帧…</div>'; return; }
    var html = frames.slice(-MAX_FRAMES).map(function (f) {
      var who = f.subject ? ' ' + esc(f.subject) +
        (f.subjectId === null || f.subjectId === undefined ? '' : '#' + esc(f.subjectId)) : '';
      return '<div class="frame"><span class="seq">#' + esc(f.seq) + '</span> ' +
        '<span class="t">t' + esc(f.tick) + ' r' + esc(f.revision) + '</span> ' +
        '<span class="k-' + esc(f.kind) + '">' + esc(f.kind) + '</span>:' + esc(f.op) + who + '</div>';
    }).join('');
    el.innerHTML = html;
    el.scrollTop = el.scrollHeight;
  }

  /* ---------------- 世界状态 + 命令面板 ---------------- */
  function renderState(st) {
    lastState = st;
    var el = $('state');
    if (!st || !st.workshops) { el.innerHTML = '<div class="empty">没有状态</div>'; return; }

    var opts = function (selected) {
      var h = '<option value="">（不选）</option>';
      st.workshops.forEach(function (w, i) {
        h += '<option value="' + i + '"' + (i === selected ? ' selected' : '') + '>' +
          esc(w.name) + '</option>';
      });
      return h;
    };
    var masterIdx = -1, slaveIdx = -1;
    st.workshops.forEach(function (w, i) { if (w.isMaster) masterIdx = i; if (w.isSlave) slaveIdx = i; });

    var html = '<div class="cmd"><span class="lbl">主台</span>' +
      '<select data-act="master">' + opts(masterIdx) + '</select>' +
      '<span class="lbl">从台</span>' +
      '<select data-act="slave">' + opts(slaveIdx) + '</select></div>';

    html += st.workshops.map(function (w) {
      var tags = '';
      if (w.isMaster) tags += '<span class="tag master">主台</span>';
      if (w.isSlave) tags += '<span class="tag slave">从台</span>';
      var jars = (w.jars || []).map(function (j) {
        return esc(j.shape) + (j.liquid ? ':' + esc(j.liquid) : '');
      }).join('、') || '（空）';
      var liq = Object.keys(w.liquids || {}).map(function (k) { return k + '×' + w.liquids[k]; }).join('、') || '（无）';
      return '<div class="ws"><div class="nm">' + esc(w.name) + tags + '</div>' +
        '<div class="jars">罐 ' + (w.jars || []).length + '/' + esc(w.maxJars) + '：' + jars + '</div>' +
        '<div class="jars">液：' + liq + '</div></div>';
    }).join('');

    var procs = (st.processes || []).map(function (p) {
      return '<div class="ord">过程 #' + esc(p.id) + ' ' + esc(p.kind) + ' ' + esc(p.subject) +
        ' <span class="tag">' + esc(p.state) + '</span>' +
        ' <button data-act="cancel" data-id="' + esc(p.id) + '">取消</button></div>';
    }).join('') || '<div class="empty">没有活动过程</div>';

    var orders = (st.orders || []).map(function (o, i) {
      var reqs = (o.requirements || []).map(function (r) {
        return esc(r.shape) + (r.liquid ? ':' + esc(r.liquid) : '');
      }).join(' + ');
      var feas = o.feasibility || o.feasible;
      var feasTxt = o.feasibility
        ? (o.feasibility.feasible ? '可行' : (o.feasibility.undetermined ? '未判定' : '不可行'))
        : '未分析';
      return '<div class="ord">#' + (i + 1) + '（id ' + esc(o.id) + '）[' + esc(o.type) + '] ' + reqs +
        ' <span class="tag">' + esc(o.status) + '</span>' +
        ' <span class="tag">' + esc(feasTxt) + '</span>' +
        '<button data-act="check" data-id="' + esc(o.id) + '">分析</button>' +
        '<button data-act="start" data-id="' + esc(o.id) + '">开始</button>' +
        '<button data-act="delete" data-id="' + esc(o.id) + '">删除</button>' +
        (feas === undefined ? '' : '') + '</div>';
    }).join('') || '<div class="empty">没有订单（先点下面的「随机派单」）</div>';

    html += '<div class="ws" style="border-top:1px solid #2e2b24;margin-top:6px;padding-top:6px">' +
      '<div class="nm">活动过程</div>' + procs + '</div>';
    html += '<div class="ws" style="border-top:1px solid #2e2b24;margin-top:6px;padding-top:6px">' +
      '<div class="nm">订单</div>' + orders +
      '<div class="cmd" style="padding:6px 0 0"><button data-act="random">随机派单</button>' +
      '<button data-act="checkAll">全部分析</button></div></div>';

    el.innerHTML = html;
  }

  /* ---------------- 不变量 ---------------- */
  function renderViolations(v) {
    var el = $('violations');
    if (!v) { el.innerHTML = '<div class="empty">检查中…</div>'; return; }
    if (v.ok) { el.innerHTML = '<div class="okbox">✅ 世界自洽（0 条违规）</div>'; return; }
    el.innerHTML = '<div class="okbox" style="color:#e05a4e">⚠ ' + v.count + ' 条违规</div>' +
      v.violations.map(function (x) {
        return '<div class="vio"><div class="code">' + esc(x.code) + '</div>' +
          '<div class="msg">' + esc(x.message) + '</div></div>';
      }).join('');
  }

  async function pollInvariants() {
    try {
      var r = await fetch('/api/invariants');
      if (!r.ok) {
        renderViolations({ ok: false, count: 1, violations: [{ code: 'HTTP-' + r.status, message: '不变量端点返回 ' + r.status }] });
        return;
      }
      renderViolations(await r.json());
    } catch (e) {
      renderViolations({ ok: false, count: 1, violations: [{ code: 'NETWORK', message: String(e) }] });
    }
  }

  /* ---------------- 连接 ---------------- */
  function connect() {
    var proto = location.protocol === 'https:' ? 'wss://' : 'ws://';
    sock = new WebSocket(proto + location.host + '/ws');

    sock.onopen = function () {
      $('conn').textContent = '已连接';
      send('getState');
      pollInvariants();
    };

    sock.onmessage = function (ev) {
      var msg;
      try { msg = JSON.parse(ev.data); } catch (e) { return; }
      var data = msg.data || {};

      if (msg.type === 'frame') {
        frames.push(data);
        if (frames.length > MAX_FRAMES * 2) frames = frames.slice(-MAX_FRAMES);
        renderFrames();
        if (data.kind === 'worldChanged' || data.kind === 'processChanged') {
          send('getState');
          pollInvariants();
        }
        return;
      }
      if (msg.type === 'state') { renderState(data); return; }
      if (msg.type === 'feasibilityResult') {
        frames.push({ seq: '-', tick: '-', revision: '-', kind: 'note',
          op: '可行性 #' + (data.orderId) + ' → ' + (data.result && data.result.feasible ? '可行' : '不可行/未判定') +
              (data.result && data.result.reason ? '（' + data.result.reason + '）' : '') });
        renderFrames();
        return;
      }
      if (msg.type === 'error') {
        frames.push({ seq: '-', tick: '-', revision: '-', kind: 'error', op: data.message });
        renderFrames();
      }
    };

    sock.onclose = function () {
      $('conn').textContent = '已断开，2 秒后重连…';
      setTimeout(connect, 2000);
    };
  }

  /* ---------------- 交互：点击 → 内核命令 ---------------- */
  document.addEventListener('click', function (ev) {
    var el = ev.target.closest('[data-act]');
    if (!el) return;
    var act = el.getAttribute('data-act');
    var id = el.getAttribute('data-id');

    if (act === 'random') { send('createRandomOrder'); return; }
    if (act === 'checkAll') { send('checkAllFeasibility'); return; }
    if (act === 'tick') { send('manualTick'); return; }
    if (act === 'run') { send('startTick'); return; }
    if (act === 'pause') { send('stopTick'); return; }
    if (act === 'refresh') { send('getState'); pollInvariants(); return; }
    if (act === 'clear') { frames = []; renderFrames(); return; }
    if (act === 'check') { send('checkFeasibility', { orderId: Number(id) }); return; }
    if (act === 'start') { send('startOrder', { orderId: Number(id) }); return; }
    if (act === 'delete') { send('deleteOrder', { orderId: Number(id) }); return; }
    if (act === 'cancel') { send('cancelProcess', { processId: Number(id) }); return; }
  });

  document.addEventListener('change', function (ev) {
    var el = ev.target.closest('[data-act]');
    if (!el) return;
    var act = el.getAttribute('data-act');
    var v = el.value === '' ? null : Number(el.value);
    if (act === 'master') { send(v === null ? 'setMaster' : 'setMaster', { index: v === null ? -1 : v }); pollInvariants(); return; }
    if (act === 'slave') { send('setSlave', { index: v === null ? -1 : v }); pollInvariants(); return; }
  });

  connect();
  setInterval(pollInvariants, 2000);
  setInterval(function () { if (!frames.length) return; var el = $('frames'); if (el) el.scrollTop = el.scrollHeight; }, 1000);
})();
