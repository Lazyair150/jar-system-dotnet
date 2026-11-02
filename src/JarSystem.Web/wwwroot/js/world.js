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
  /* 页面自己的"待提交配置"，只为表单服务，不参与任何判定。 */
  var ui = { cloneIdx: -1 };
  /* 每张订单各自一份可行性结论（key = 订单 id）。**页面不推导它**：只存内核给的答复。 */
  var FEAS = {};
  /* 「一回合」驱动的状态机：collect = 等可行性结果 → start = 已下单，等 tick → idle */
  var autoState = 'idle';
  var autoRound = 0;

  function $(id) { return document.getElementById(id); }
  function esc(s) {
    return String(s === null || s === undefined ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/"/g, '&quot;');
  }
  function send(type, data) {
    if (sock && sock.readyState === 1) sock.send(JSON.stringify({ type: type, data: data || {} }));
  }

  /* 阶段条：B 类订单的执行分三段（主台阶段 → 跨台转移 → 从台阶段）。
     三段的**边界**不来自任何规则，而是内核 feasibilityResult 里自报的：
     `masterPath.length`（主台段步数）、`transferJarIds` / `transferLiquids`（转移内容）、
     `solverPath.length`（总步数）。页面只把这些数字画成颜色与文字。 */
  function phaseStrip(feas, proc) {
    if (!feas) return '';
    var masterLen = (feas.masterPath || []).length;
    var total = (feas.solverPath || []).length;
    if (!masterLen || !total) return '';                 /* 没有主台段 ⇒ A 类或单段路径，不画 */
    var slaveLen = Math.max(0, total - masterLen);
    var cur = proc ? (proc.currentStep || 0) : 0;
    var segs = [
      { cls: 'ph-master', n: masterLen, label: '主台阶段 ' + masterLen + ' 步' },
      { cls: 'ph-xfer', n: 0, label: '转移 ' + ((feas.transferJarIds || []).length) + ' 只罐' },
      { cls: 'ph-slave', n: slaveLen, label: '从台阶段 ' + slaveLen + ' 步' }
    ];
    var done = Math.min(cur, masterLen);
    var inSlave = cur > masterLen;
    var html = '<div class="phases">';
    segs.forEach(function (s) {
      var on = s.cls === 'ph-master' ? (cur > 0 && !inSlave)
        : (s.cls === 'ph-xfer' ? (cur >= masterLen && !inSlave && cur > 0)
          : inSlave);
      html += '<span class="ph ' + s.cls + (on ? ' on' : '') + '">' + esc(s.label) + '</span>';
    });
    html += '</div>';
    var liq = Object.keys(feas.transferLiquids || {}).map(function (k) {
      return k + '×' + feas.transferLiquids[k];
    }).join('、');
    html += '<div class="jars">' + (done ? '主台段已完成 ' + done + '/' + masterLen : '主台段未开始') +
      (liq ? '；转移液体：' + esc(liq) : '') + '</div>';
    return html;
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

    /* 配置面（A）：只用已有命令，不碰内核。
       新建工作台按"复制一张现有卡片"来做 —— 因为 getState 没有下发形状词表，
       让玩家自由勾标记就必须先把词表下发（属 B1 的 setWorkshopCapability）。
       在那之前，卡片式复制是唯一不会骗人的做法：复制出来的卡与源卡规则完全一致。 */
    if (st.workshops.length) {
      html += '<div class="cmd"><span class="lbl">新建工作台</span>' +
        '<select data-act="clonews">' + st.workshops.map(function (w, i) {
          return '<option value="' + i + '">' + esc(w.name) + '</option>';
        }).join('') + '</select>' +
        '<button data-act="clonewsDo">复制这张卡</button></div>';
    }

    html += st.workshops.map(function (w, wi) {
      var tags = '';
      if (w.isMaster) tags += '<span class="tag master">主台</span>';
      if (w.isSlave) tags += '<span class="tag slave">从台</span>';
      var jars = (w.jars || []).map(function (j) {
        return esc(j.shape) + (j.liquid ? ':' + esc(j.liquid) : '');
      }).join('、') || '（空）';
      var liq = Object.keys(w.liquids || {}).map(function (k) { return k + '×' + w.liquids[k]; }).join('、') || '（无）';
      var mk = function (arr, cls) {
        return (arr || []).map(function (m) { return '<span class="chip ' + cls + '">' + esc(m) + '</span>'; }).join('');
      };
      return '<div class="ws"><div class="nm">' + esc(w.name) + tags + '</div>' +
        '<div class="jars">基础 ' + (mk(w.baseMarks, 'b') || '（无）') + '</div>' +
        '<div class="jars">附加 ' + (mk(w.additionalMarks, 'a') || '（无）') + '</div>' +
        '<div class="jars">禁 ' + (mk(w.ban, 'x') || '（无）') + '</div>' +
        '<div class="jars">罐 ' + (w.jars || []).length + '/' + esc(w.maxJars) + '：' + jars + '</div>' +
        '<div class="jars">液 ' + (w.maxLiquids === undefined ? '' : '/' + esc(w.maxLiquids) + ' ') + '：' + liq + '</div>' +
        '<div class="cmd"><span class="lbl">容量</span>' +
        ['maxJars', 'maxLiquids'].map(function (k) {
          var cur = k === 'maxJars' ? w.maxJars : w.maxLiquids;
          var lab = k === 'maxJars' ? '罐' : '液';
          return '<button data-act="cap" data-i="' + wi + '" data-k="' + k + '" data-d="-1">' + lab + ' −</button>' +
            '<span class="lbl">' + esc(cur) + '</span>' +
            '<button data-act="cap" data-i="' + wi + '" data-k="' + k + '" data-d="1">' + lab + ' ＋</button>';
        }).join('') + '</div></div>';
    }).join('');

    /* 产线卡：每张卡 = 内核里的一个生产者，卡面列出它产什么、多快。
       速率只给 4 个档位（0 / 0.2 / 0.4 / 0.6），不是自由输入 —— 这是"有限可选项"那条设计原则。
       页面上不落地任何"这张卡产多少"的规则：点档位只是发 updateProducerRate，世界怎么变由内核决定。 */
    var RATES = [0, 0.2, 0.4, 0.6];
    var ps = st.producerStatus || {};
    var prods = (ps.producers || []);
    var produceHtml = prods.length ? prods.map(function (p) {
      var rows = Object.keys(p.rates || {}).map(function (s) {
        var cur = (p.rates || {})[s];
        return '<div class="cmd"><span class="lbl">' + esc(s) + '</span>' +
          RATES.map(function (r) {
            var on = Math.abs(cur - r) < 1e-9 ? ' on' : '';
            return '<button class="chip' + on + '" data-act="rate" data-p="' + esc(p.name) +
              '" data-s="' + esc(s) + '" data-r="' + r + '">' + r + '</button>';
          }).join('') + '</div>';
      }).join('');
      return '<div class="card"><div class="cnm">' + esc(p.name) + '</div>' + rows + '</div>';
    }).join('') : '<div class="empty">内核未报告生产者</div>';
    html += '<div class="ws"><div class="nm">产线卡（生产者 · 每 tick 产量）</div>' + produceHtml + '</div>';

    var procs = (st.processes || []).map(function (p) {
      return '<div class="ord">过程 #' + esc(p.id) + ' ' + esc(p.kind) + ' ' + esc(p.subject) +
        ' <span class="tag">' + esc(p.state) + '</span>' +
        ' <button data-act="cancel" data-id="' + esc(p.id) + '">取消</button></div>';
    }).join('') || '<div class="empty">没有活动过程</div>';

    var orders = (st.orders || []).map(function (o, i) {
      var reqs = (o.requirements || []).map(function (r) {
        return esc(r.shape) + (r.liquid ? ':' + esc(r.liquid) : '');
      }).join(' + ');
      var feas = FEAS[o.id] || o.feasibility || null;
      var total = (o.requirements || []).length || 1;
      var pct, feasTxt, note = '';
      if (!feas) { pct = 0; feasTxt = '未分析'; }
      else if (feas.feasible) { pct = 100; feasTxt = '可行（进度拉满）'; }
      else {
        pct = Math.max(0, Math.min(99, Math.round(100 * (feas.directCount || 0) / total)));
        feasTxt = feas.undetermined ? '未判定' : '不可行';
        note = feas.blockage || feas.reason || '';
      }
      return '<div class="ord">#' + (i + 1) + '（id ' + esc(o.id) + '）[' + esc(o.type) + '] ' + reqs +
        ' <span class="tag">' + esc(o.status) + '</span>' +
        ' <span class="tag">' + esc(feasTxt) + '</span>' +
        '<button data-act="check" data-id="' + esc(o.id) + '">分析</button>' +
        '<button data-act="start" data-id="' + esc(o.id) + '">开始</button>' +
        '<button data-act="delete" data-id="' + esc(o.id) + '">删除</button>' +
        '<div class="bar"><i style="width:' + pct + '%"></i></div>' +
        phaseStrip(feas, (st.processes || []).filter(function (p) {
          return p.kind === 'order_execution' && p.subjectId === o.id;
        })[0]) +
        (note ? '<div class="jars">' + esc(note) + '</div>' : '') + '</div>';
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
      if (msg.type === 'allFeasibilityResults') {
        (Array.isArray(data) ? data : []).forEach(function (x) { if (x && x.orderId != null) FEAS[x.orderId] = x.result; });
        renderState(lastState);
        if (autoState === 'collect') driveAutoRound();
        return;
      }
      if (msg.type === 'feasibilityResult') {
        if (data.orderId != null) FEAS[data.orderId] = data.result;
        frames.push({ seq: '-', tick: '-', revision: '-', kind: 'note',
          op: '可行性 #' + (data.orderId) + ' → ' + (data.result && data.result.feasible ? '可行' : '不可行/未判定') +
              (data.result && data.result.reason ? '（' + data.result.reason + '）' : '') });
        renderFrames();
        renderState(lastState);
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
  /* 「一回合」= 让内核先分析、把**当前完整可执行**的单都下下去，再推一刻，然后重新分析。
     注意：页面只决定**什么时候问、问完发什么命令**；"谁能执行"完全来自 checkFeasibility 的答复。 */
  function startAutoRound() {
    if (autoState !== 'idle') return;
    autoState = 'collect';
    autoRound = 0;
    send('checkAllFeasibility');
  }

  function driveAutoRound() {
    autoState = 'idle';
    var started = 0;
    (lastState && lastState.orders ? lastState.orders : []).forEach(function (o) {
      var f = FEAS[o.id];
      if (!f || !f.feasible) return;
      if (o.status === '执行中') return;            /* 内核状态词表：执行中（Constants.OrderStatus.EXECUTING） */
      send('startOrder', { orderId: o.id });
      started++;
    });
    frames.push({ seq: '-', tick: '-', revision: '-', kind: 'note',
      op: '一回合：' + (started ? ('下 ' + started + ' 单') : '没有可完整执行的单') });
    renderFrames();
    if (!started) return;
    send('manualTick');
    autoRound++;
    if (autoRound < 3) { autoState = 'collect'; setTimeout(function () { send('checkAllFeasibility'); }, 400); }
  }

  document.addEventListener('click', function (ev) {
    var el = ev.target.closest('[data-act]');
    if (!el) return;
    var act = el.getAttribute('data-act');
    var id = el.getAttribute('data-id');

    if (act === 'random') { send('createRandomOrder'); return; }
    if (act === 'checkAll') { send('checkAllFeasibility'); return; }
    if (act === 'tick') { send('manualTick'); return; }
    if (act === 'autoround') { startAutoRound(); return; }
    if (act === 'run') { send('startTick'); return; }
    if (act === 'pause') { send('stopTick'); return; }
    if (act === 'refresh') { send('getState'); pollInvariants(); return; }
    if (act === 'clear') { frames = []; renderFrames(); return; }
    if (act === 'check') { send('checkFeasibility', { orderId: Number(id) }); return; }
    if (act === 'start') { send('startOrder', { orderId: Number(id) }); return; }
    if (act === 'delete') { send('deleteOrder', { orderId: Number(id) }); return; }
    if (act === 'cancel') { send('cancelProcess', { processId: Number(id) }); return; }

    /* ---- 配置面（A）：全部转发内核已有命令 ---- */
    if (act === 'clonewsDo') {
      var st0 = lastState; if (!st0 || !st0.workshops) return;
      var sel = document.querySelector('select[data-act="clonews"]');
      var si = sel ? Number(sel.value) : 0;
      var src = st0.workshops[si]; if (!src) return;
      send('createWorkshop', { name: src.name + '·副本', baseMarks: src.baseMarks || [],
        additionalMarks: src.additionalMarks || [], ban: src.ban || [] });
      return;
    }
    if (act === 'cap') {
      var stc = lastState; if (!stc || !stc.workshops) return;
      var wc = stc.workshops[Number(el.getAttribute('data-i'))]; if (!wc) return;
      var key = el.getAttribute('data-k');
      var cur = key === 'maxJars' ? wc.maxJars : wc.maxLiquids;
      var nv = Math.max(1, Math.min(12, (cur || 0) + Number(el.getAttribute('data-d'))));
      if (nv === cur) return;
      var payload = { index: Number(el.getAttribute('data-i')), maxJars: wc.maxJars, maxLiquids: wc.maxLiquids };
      payload[key] = nv;
      send('updateWorkshopCapacity', payload);
      return;
    }
    if (act === 'rate') {
      send('updateProducerRate', { producerName: el.getAttribute('data-p'),
        shape: el.getAttribute('data-s'), rate: Number(el.getAttribute('data-r')) });
      return;
    }
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
