/* 世界视图（S2）：**只读表达器**。
   ---------------------------------------------------------------------------
   这一页只有三件事，全部来自内核，页面里没有任何规则：
   1 世界状态：`getState` 的 `workshops` / `orders`（内核怎么描述，这里就怎么显示）；
   2 帧时间线：WebSocket 的 `frame` 消息（核心 `PresentationFeed` 的投影，只有引用没有载荷）；
   3 不变量：`GET /api/invariants` 的结果（内核判断"这个状态是否自相矛盾"）。

   一条硬边界：**页面不推导、不补全、不猜测**。它不会因为"某订单看起来应该能执行"而显示进度，
   也不会因为"某工作台看起来缺了罐子"而报警 —— 那是内核与不变量检查器的职责。
   页面唯一的加工是把 JSON 变成可读的文本。
*/
(function () {
  'use strict';

  var MAX_FRAMES = 300;
  var frames = [];
  var sock = null;
  var connected = false;

  function $(id) { return document.getElementById(id); }
  function esc(s) {
    return String(s === null || s === undefined ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;');
  }
  function send(type, data) {
    if (sock && sock.readyState === 1) sock.send(JSON.stringify({ type: type, data: data || {} }));
  }

  /* ---------------- 帧时间线 ---------------- */
  function renderFrames() {
    var el = $('frames');
    if (!frames.length) { el.innerHTML = '<div class="empty">等待帧…</div>'; return; }
    var html = frames.slice(-MAX_FRAMES).map(function (f) {
      var who = f.subject ? ' ' + esc(f.subject) + (f.subjectId === null || f.subjectId === undefined ? '' : '#' + esc(f.subjectId)) : '';
      return '<div class="frame"><span class="seq">#' + f.seq + '</span> ' +
        '<span class="t">t' + f.tick + ' r' + f.revision + '</span> ' +
        '<span class="k-' + esc(f.kind) + '">' + esc(f.kind) + '</span>:' + esc(f.op) + esc(who) + '</div>';
    }).join('');
    el.innerHTML = html;
    el.scrollTop = el.scrollHeight;
  }

  /* ---------------- 世界状态 ---------------- */
  function renderState(st) {
    var el = $('state');
    if (!st || !st.workshops) { el.innerHTML = '<div class="empty">没有状态</div>'; return; }

    var html = st.workshops.map(function (w) {
      var tags = '';
      if (w.isMaster) tags += '<span class="tag master">主台</span>';
      if (w.isSlave) tags += '<span class="tag slave">从台</span>';
      var jars = (w.jars || []).map(function (j) {
        return esc(j.shape) + (j.liquid ? ':' + esc(j.liquid) : '');
      }).join('、') || '（空）';
      var liq = Object.keys(w.liquids || {}).map(function (k) { return k + '×' + w.liquids[k]; }).join('、') || '（无）';
      return '<div class="ws"><div class="nm">' + esc(w.name) + tags + '</div>' +
        '<div class="jars">罐 ' + (w.jars || []).length + '/' + w.maxJars + '：' + jars + '</div>' +
        '<div class="jars">液：' + liq + '</div></div>';
    }).join('');

    var orders = (st.orders || []).map(function (o, i) {
      var reqs = (o.requirements || []).map(function (r) {
        return esc(r.shape) + (r.liquid ? ':' + esc(r.liquid) : '');
      }).join(' + ');
      return '<div class="ord">#' + (i + 1) + ' [' + esc(o.type) + '] ' + reqs +
        ' <span class="tag">' + esc(o.status) + '</span></div>';
    }).join('') || '<div class="empty">没有订单</div>';

    el.innerHTML = html +
      '<div class="ws" style="border-top:1px solid #2e2b24;margin-top:6px;padding-top:6px">' +
      '<div class="nm">订单</div>' + orders + '</div>';
  }

  /* ---------------- 不变量 ---------------- */
  function renderViolations(v) {
    var el = $('violations');
    if (!v) { el.innerHTML = '<div class="empty">检查中…</div>'; return; }
    if (v.ok) { el.innerHTML = '<div class="okbox">✅ 世界自洽（' + v.count + ' 条违规）</div>'; return; }
    el.innerHTML = '<div class="okbox" style="color:#e05a4e">⚠ ' + v.count + ' 条违规</div>' +
      v.violations.map(function (x) {
        return '<div class="vio"><div class="code">' + esc(x.code) + '</div>' +
          '<div class="msg">' + esc(x.message) + '</div></div>';
      }).join('');
  }

  async function pollInvariants() {
    try {
      var r = await fetch('/api/invariants');
      if (!r.ok) { renderViolations({ ok: false, count: 1, violations: [{ code: 'HTTP-' + r.status, message: '不变量端点返回 ' + r.status }] }); return; }
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
      connected = true;
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
        /* 世界变了 ⇒ 状态与不变量都可能变。但**不猜测**：只是重新问一次内核。 */
        if (data.kind === 'worldChanged' || data.kind === 'processChanged') {
          send('getState');
          pollInvariants();
        }
        return;
      }
      if (msg.type === 'state') { renderState(data); return; }
      if (msg.type === 'error') {
        frames.push({ seq: '-', tick: '-', revision: '-', kind: 'error', op: data.message });
        renderFrames();
      }
    };

    sock.onclose = function () {
      connected = false;
      $('conn').textContent = '已断开，2 秒后重连…';
      setTimeout(connect, 2000);
    };
  }

  /* ---------------- 交互 ---------------- */
  $('btn-tick').addEventListener('click', function () { send('manualTick'); });
  $('btn-state').addEventListener('click', function () { send('getState'); pollInvariants(); });
  $('btn-clear').addEventListener('click', function () { frames = []; renderFrames(); });

  connect();
  setInterval(pollInvariants, 2000);
})();
