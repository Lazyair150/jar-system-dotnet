/* 陶罐解谜（游戏页）
   ---------------------------------------------------------------------------
   这一页只做两件事：
   1 显示：内核 `getState` 说世界是什么样，就画成什么样（工作台=卡、罐子=方片、
      产线=速率卡、订单=带进度条的卡、中央仓库=数字）。
   2 转发玩家意图：每一次点击都翻译成**内核命令**（setMaster / setSlave /
      checkAllFeasibility / startOrder / deleteOrder / moveOrder / updateProducerRate /
      manualTick / restartLevel / createRandomOrder）。

   硬边界（与 world.html 相同）：页面不推导、不补全、不猜测。
   进度条的分子分母来自内核 `checkAllFeasibility` 的 `directCount / 需求条数`；
   不可行的理由直接是内核给的 `blockage`，页面一个字都不编。
*/
(function () {
  'use strict';

  var sock = null;
  var state = null;
  var feas = {};          /* orderId → feasibilityResult（内核答复，页面只存不算） */
  var levels = [];
  var MAX_LOG = 200;

  function $(id) { return document.getElementById(id); }
  function esc(s) {
    return String(s === null || s === undefined ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/"/g, '&quot;');
  }
  function send(type, data) {
    if (sock && sock.readyState === 1) send_ok(type, data);
    else toast('还没连上内核');
  }
  function send_ok(type, data) {
    sock.send(JSON.stringify({ type: type, data: data || {} }));
  }

  var toastTimer = null;
  function toast(msg) {
    var t = $('toast');
    t.textContent = msg;
    t.classList.add('show');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { t.classList.remove('show'); }, 2200);
  }

  /* ---------------- 连接 ---------------- */
  function connect() {
    var proto = location.protocol === 'https:' ? 'wss://' : 'ws://';
    sock = new WebSocket(proto + location.host + '/ws');
    sock.onopen = function () {
      setConn(true);
      send_ok('getState', {});
      send_ok('checkAllFeasibility', {});
    };
    sock.onclose = function () {
      setConn(false);
      setTimeout(connect, 1200);
    };
    sock.onerror = function () { setConn(false); };
    sock.onmessage = function (ev) {
      var msg;
      try { msg = JSON.parse(ev.data); } catch (e) { return; }
      var d = msg.data || {};
      switch (msg.type) {
        case 'state':
          state = d;
          render();
          break;
        case 'frame':
          logLine(d.kind, d.op, d.subject, d.tick);
          break;
        case 'allFeasibilityResults':
          applyFeas(d.results || []);
          renderOrders();
          break;
        case 'feasibilityResult':
          if (d.orderId !== undefined && d.result) { feas[d.orderId] = d.result; renderOrders(); }
          break;
        case 'autoCheckResults':
          applyFeas(d.results || []);
          renderOrders();
          break;
        case 'orderExecuted':
          if (d.reason) toast(d.reason);
          break;
        case 'error':
          toast(d.message || '内核拒绝了这个操作');
          break;
      }
    };
  }

  function setConn(ok) {
    var el = $('conn');
    el.textContent = ok ? '已连接' : '断开';
    el.className = 'pill' + (ok ? ' good' : ' warn');
  }

  function applyFeas(results) {
    results.forEach(function (r) {
      if (r && r.orderId !== undefined && r.result) feas[r.orderId] = r.result;
    });
  }

  /* ---------------- 顶栏 / 提示 ---------------- */
  function renderTop() {
    var lv = currentLevel();
    $('lvname').textContent = lv ? (lv.name || ('第 ' + lv.index + ' 关')) : '出厂沙盒';
    var t = state.turns || { used: 0, limit: 0 };
    $('turns').textContent = t.limit > 0
      ? ('回合 ' + t.used + ' / ' + t.limit)
      : ('回合 ' + t.used + '（不限）');
    $('lvhint').textContent = lv && lv.hint ? lv.hint : '';

    var g = state.level || { goalOrders: 0, completedOrders: 0, outcome: 'inProgress', reason: '' };
    var goalEl = $('goal');
    if (g.goalOrders > 0) {
      goalEl.textContent = '目标 ' + g.completedOrders + ' / ' + g.goalOrders + ' 单';
      goalEl.className = 'pill' + (g.outcome === 'won' ? ' good' : (g.outcome === 'lost' ? ' warn' : ''));
    } else {
      goalEl.textContent = '不判胜（沙盒）';
      goalEl.className = 'pill';
    }

    var over = g.outcome === 'won' || g.outcome === 'lost' || (t.limit > 0 && t.used >= t.limit);
    var btn = $('advanceBtn');
    btn.disabled = over;
    btn.textContent = g.outcome === 'won' ? '本关通过'
      : g.outcome === 'lost' ? '本关结束' : '推进一回合';
    if (g.outcome === 'lost' && g.reason) btn.textContent = '本关结束';
  }

  function currentLevel() {
    var cur = null;
    for (var i = 0; i < levels.length; i++) if (levels[i].current) cur = levels[i].current;
    if (!cur) return null;
    for (var j = 0; j < levels.length; j++) if (levels[j].index === cur) return levels[j];
    return null;
  }

  /* ---------------- 工作台卡 ---------------- */
  function jarChip(jar) {
    var cls = 'jarchip' + (jar.liquid === '蜜' ? ' wet-mi' : (jar.liquid === '酒' ? ' wet-jiu' : ''));
    return '<span class="' + cls + '" title="' + esc(jar.shape + (jar.liquid ? ':' + jar.liquid : '')) + '">' +
      esc(jar.shape.replace('罐', '')) + (jar.liquid ? '<small>·' + esc(jar.liquid) + '</small>' : '') + '</span>';
  }

  function benchBody(ws) {
    if (!ws) return '<div class="empty">还没有选工作台</div>';
    var html = '';
    html += '<div class="marks">' +
      '<span>基础：' + esc((ws.baseMarks || []).join('、') || '—') + '</span>' +
      '<span>附加：' + esc((ws.additionalMarks || []).join('、') || '—') + '</span>' +
      '<span class="mk ban">禁：' + esc((ws.ban || []).join('、') || '—') + '</span>' +
      '<span>罐位 ' + (ws.jars || []).length + ' / ' + ws.maxJars + '</span>' +
      '<span>液位 ' + liquidCount(ws) + ' / ' + ws.maxLiquids + '</span>' +
      '</div>';

    var jars = (ws.jars || []).map(jarChip).join('');
    html += '<div class="row">' + (jars || '<span class="empty">台面上没有罐子</span>') + '</div>';

    var liqs = Object.keys(ws.liquids || {}).map(function (k) {
      var banned = (ws.ban || []).indexOf(k) >= 0 || (ws.ban || []).indexOf('禁' + k) >= 0;
      var cls = 'liq' + (k === '蜜' ? ' liq-mi' : (k === '酒' ? ' liq-jiu' : '')) + (banned ? ' banned' : '');
      return '<span class="' + cls + '" title="' + (banned ? '这台禁它：能存不能用' : '可倒出/可做改形成本') + '">' +
        esc(k) + ' ×' + esc(ws.liquids[k]) + '</span>';
    }).join('');
    html += '<div class="row">' + (liqs || '<span class="empty">池里没有液体</span>') + '</div>';
    return html;
  }

  function liquidCount(ws) {
    var n = 0;
    Object.keys(ws.liquids || {}).forEach(function (k) { n += ws.liquids[k]; });
    return n;
  }

  function renderBenches() {
    var master = null, slave = null;
    (state.workshops || []).forEach(function (w) {
      if (w.isMaster) master = w;
      if (w.isSlave) slave = w;
    });
    $('masterName').textContent = master ? master.name : '（未布置）';
    $('masterBody').innerHTML = benchBody(master);
    $('subName').textContent = slave ? slave.name : '（不用从台）';
    $('subHint').textContent = slave ? 'B 类订单会在这里收尾' : '只在 B 类订单需要时才有用';
    $('subBody').innerHTML = slave ? benchBody(slave) : '<div class="empty">点「换从台」挑一台</div>';
  }

  /* ---------------- 产线卡 ---------------- */
  var RATES = [0, 0.2, 0.4, 0.6];

  function renderLines() {
    var ps = state.producerStatus || { producers: [] };
    var html = '';
    var active = 0;
    (ps.producers || []).forEach(function (p) {
      var shapes = Object.keys(p.rates || {});
      shapes.forEach(function (shape) {
        var rate = p.rates[shape] || 0;
        if (rate > 0) active++;
        var btns = RATES.map(function (r) {
          var on = Math.abs((rate || 0) - r) < 1e-9;
          return '<button data-act="rate" data-p="' + esc(p.name) + '" data-s="' + esc(shape) +
            '" data-r="' + r + '" class="' + (on ? 'on' : '') + '">' + r + '</button>';
        }).join('');
        html += '<div class="linecard"><div class="lh"><b>' + esc(shape) + '</b>' +
          '<span>· ' + esc(p.name) + '</span></div><div class="rates">' + btns + '</div></div>';
      });
    });
    $('linesBody').innerHTML = html || '<div class="empty">这个世界没有生产者</div>';
    $('linesHint').textContent = '每张卡决定这个形状每回合产几只 · 已开工 ' + active + ' 条';
  }

  /* ---------------- 订单 ---------------- */
  function reqChip(req, ok) {
    var label = req.shape.replace('罐', '') + (req.liquid ? '·' + req.liquid : '');
    var cls = 'reqchip' + (req.liquid === '蜜' ? ' wet-mi' : (req.liquid === '酒' ? ' wet-jiu' : '')) + (ok ? ' ok' : '');
    return '<span class="' + cls + '" title="' + esc(req.shape + (req.liquid ? ':' + req.liquid : '')) + '">' + esc(label) + '</span>';
  }

  function renderOrders() {
    var orders = state.orders || [];
    if (!orders.length) {
      $('ordersBody').innerHTML = '<div class="empty">还没有订单（点右上角「随机派一单」，或重开本关）</div>';
      return;
    }
    var html = '';
    orders.forEach(function (o, i) {
      var f = feas[o.id];
      var reqs = o.requirements || [];
      var done = f && f.feasible ? reqs.length : (f && f.directCount ? f.directCount : 0);
      var pct = reqs.length ? Math.round(done * 100 / reqs.length) : 0;
      var isDone = (o.status === 'success' || o.status === 'failed');
      html += '<div class="order' + (isDone ? ' done' : '') + '">';
      html += '<div class="ohead"><span class="oid">#' + esc(o.id) + '</span>' +
        '<span class="ocls' + (o.type === 'B' ? ' b' : '') + '">' + esc(o.type) + ' 类</span>' +
        (o.status === 'success' ? '<span class="obadge">已完成</span>' :
          (o.status === 'failed' ? '<span class="obadge" style="color:var(--red);background:rgba(224,90,78,.16)">已失败</span>' : '')) +
        '<span class="oprog">' + done + ' / ' + reqs.length + '</span></div>';

      html += '<div class="obar' + (f && f.blockage && !f.feasible ? ' bad' : '') + '"><i style="width:' + pct + '%"></i></div>';
      html += '<div class="oreqs">' + reqs.map(function (r) { return reqChip(r, false); }).join('') + '</div>';

      if (f) {
        var note = f.feasible ? '现在就能做：' + (f.solverPath ? f.solverPath.length : 0) + ' 步'
          : (f.blockage || f.reason || '还不能做');
        html += '<div class="onote ' + (f.feasible ? 'good' : 'bad') + '">' + esc(note) + '</div>';
        if (f.feasible && f.solverPath && f.solverPath.length) {
          html += '<div class="oplan"><span class="olab">做法</span>' +
            f.solverPath.slice(0, 6).map(function (s, k) {
              return '<span class="opc">' + (k + 1) + '. ' + esc(describeStep(s)) + '</span>';
            }).join('') + '</div>';
        }
      } else {
        html += '<div class="onote">点「分析」问内核这单能不能做</div>';
      }

      html += '<div class="row">' +
        '<button class="mini" data-act="check" data-i="' + i + '">分析</button>' +
        '<button class="mini" data-act="start" data-i="' + i + '"' + (isDone ? ' disabled' : '') + '>开始</button>' +
        '<button class="mini" data-act="up" data-id="' + esc(o.id) + '">↑</button>' +
        '<button class="mini" data-act="down" data-id="' + esc(o.id) + '">↓</button>' +
        '<button class="mini" data-act="del" data-id="' + esc(o.id) + '">删除</button>' +
        '</div>';
      html += '</div>';
    });
    $('ordersBody').innerHTML = html;
  }

  /* 内核给的求解步骤 → 一句人话。**只做措辞，不做判断**。 */
  function describeStep(s) {
    if (!s) return '?';
    if (typeof s === 'string') return s;
    var op = s.op || s.operation || '';
    var jar = s.jarId !== undefined ? ('罐 ' + s.jarId) : '';
    var to = s.targetShape || s.shape || '';
    switch (op) {
      case 'pour_in': return '给 ' + jar + ' 灌 ' + (s.liquid || '液');
      case 'pour_out': return jar + ' 倒空';
      case 'reshape_empty': return jar + ' 改形成 ' + to + (s.costLiquid ? '（耗 ' + s.costLiquid + '）' : '');
      case 'reshape_full': return jar + ' 凭罐内液体改形成 ' + to;
      case 'transfer': return '把 ' + jar + ' 送到从台';
      case 'match': return jar + ' 正好可用';
      default: return op + (to ? ' → ' + to : '') + (jar ? ' ' + jar : '');
    }
  }

  /* ---------------- 中央仓库 ---------------- */
  function renderStage() {
    var wh = state.warehouse || {};
    var liqs = Object.keys(wh.liquids || {}).map(function (k) {
      return '<span class="liq' + (k === '蜜' ? ' liq-mi' : (k === '酒' ? ' liq-jiu' : '')) + '">' +
        esc(k) + ' ×' + esc(wh.liquids[k]) + '</span>';
    }).join(' ');
    $('stageBody').innerHTML =
      '<div><span class="big">' + esc(wh.jars || 0) + '</span> <span class="lab">只罐子</span></div>' +
      '<div style="display:flex;gap:6px;flex-wrap:wrap">' + (liqs || '<span class="empty">没有液体</span>') + '</div>' +
      (wh.discardedJars ? '<div class="lab">溢出丢弃 ' + esc(wh.discardedJars) + ' 只</div>' : '');
  }

  /* ---------------- 记录 ---------------- */
  var logs = [];
  function logLine(kind, op, subject, tick) {
    logs.push({ kind: kind, op: op, subject: subject, tick: tick });
    if (logs.length > MAX_LOG) logs = logs.slice(-MAX_LOG);
    var el = $('logBody');
    el.scrollTop = el.scrollHeight;
  }
  function flushLogs() {
    var el = $('logBody');
    if (!logs.length) { el.innerHTML = '<div class="empty">还没有记录</div>'; return; }
    el.innerHTML = logs.slice(-MAX_LOG).map(function (l) {
      return '<div class="ln"><b>t' + esc(l.tick) + '</b> ' + esc(l.kind) + ' · ' + esc(l.op) +
        (l.subject ? ' · ' + esc(l.subject) : '') + '</div>';
    }).join('');
    el.scrollTop = el.scrollHeight;
  }

  /* ---------------- 渲染总入口 ---------------- */
  var renderQueued = false;
  function render() {
    if (renderQueued) return;
    renderQueued = true;
    requestAnimationFrame(function () {
      renderQueued = false;
      if (!state) return;
      renderTop();
      renderBenches();
      renderLines();
      renderOrders();
      renderStage();
      flushLogs();
    });
  }

  /* ---------------- 选择器 ---------------- */
  function openPicker(title, optionsHtml) {
    $('pickbox').innerHTML = '<div class="ph">' + esc(title) + '</div>' + optionsHtml +
      '<button class="opt close" data-act="closepicker">取消</button>';
    $('picker').classList.remove('hidden');
  }
  function closePicker() { $('picker').classList.add('hidden'); }

  function benchOptions(act) {
    return (state.workshops || []).map(function (w) {
      return '<button class="opt" data-act="' + act + '" data-idx="' + w.index + '">' +
        '<span class="on">' + esc(w.name) + '</span>' +
        '<span class="od">基础 ' + esc((w.baseMarks || []).join('、')) +
        ' · 附加 ' + esc((w.additionalMarks || []).join('、') || '—') +
        ' · 禁 ' + esc((w.ban || []).join('、') || '—') +
        ' · 罐位 ' + w.maxJars + '</span></button>';
    }).join('');
  }

  function openLevels() {
    var html = (levels || []).map(function (l) {
      return '<button class="opt" data-act="gotolevel" data-idx="' + l.index + '">' +
        '<span class="on">第 ' + l.index + ' 关 · ' + esc(l.name) + '</span>' +
        '<span class="od">' + esc(l.hint || '') + '（' + l.turns + ' 回合）</span></button>';
    }).join('');
    openPicker('关卡选择（切关要重启宿主：run.bat 里加 --level N）', html);
  }

  /* ---------------- 事件 ---------------- */
  document.addEventListener('click', function (ev) {
    var t = ev.target.closest('[data-act]');
    if (!t) return;
    var act = t.getAttribute('data-act');
    switch (act) {
      case 'advance':
        send('manualTick', { count: 1 });
        setTimeout(function () { send('checkAllFeasibility', {}); }, 260);
        break;
      case 'restart':
        if (!confirm('重开本关？世界回到开局，订单按关卡模板重新派发。')) return;
        send('restartLevel', {});
        logs = [];
        break;
      case 'levels': openLevels(); break;
      case 'closepicker': closePicker(); break;
      case 'gotolevel': closePicker(); toast('换关请重启：run.bat 里 --level ' + t.getAttribute('data-idx')); break;
      case 'pickmaster': openPicker('选择主台', benchOptions('setmaster')); break;
      case 'pickslave': openPicker('选择从台', benchOptions('setslave')); break;
      case 'setmaster': closePicker(); send('setMaster', { index: +t.getAttribute('data-idx') }); break;
      case 'setslave': closePicker(); send('setSlave', { index: +t.getAttribute('data-idx') }); break;
      case 'noslave': send('setSlave', { index: -1 }); break;
      case 'rate':
        send('updateProducerRate', {
          producerName: t.getAttribute('data-p'),
          shape: t.getAttribute('data-s'),
          rate: +t.getAttribute('data-r')
        });
        break;
      case 'check': send('checkFeasibility', { orderIndex: +t.getAttribute('data-i') }); break;
      case 'start': send('startOrder', { index: +t.getAttribute('data-i') }); break;
      case 'del': send('deleteOrder', { orderId: +t.getAttribute('data-id') }); break;
      case 'up': send('moveOrder', { orderId: +t.getAttribute('data-id'), dir: -1 }); break;
      case 'down': send('moveOrder', { orderId: +t.getAttribute('data-id'), dir: 1 }); break;
      case 'neworder': send('createRandomOrder', {}); break;
    }
  });

  document.addEventListener('keydown', function (ev) {
    if (ev.code === 'Space' && !$('advanceBtn').disabled) { ev.preventDefault(); $('advanceBtn').click(); }
    if (ev.code === 'Escape') closePicker();
  });

  /* ---------------- 启动 ---------------- */
  fetch('/api/levels')
    .then(function (r) { return r.json(); })
    .then(function (d) {
      levels = (d.levels || []).map(function (l) {
        return { index: l.index, name: l.name, hint: l.hint, turns: l.turns, current: l.index === d.current ? d.current : null };
      });
      renderTop();
    })
    .catch(function () { /* 没有关卡端点就按沙盒显示 */ });

  connect();
})();
