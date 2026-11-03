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
  var logFilter = 'all';    /* 'all' | 'exec' | 委托 id：记录栏筛选（页面自己的视图状态） */
  var folded = {};          /* 记录 k → true：这一条被折成了一行 */
  var logKey = 1;           /* 记录的唯一键（seq 会重复：本地记录没有 seq） */
  var pendingFlow = null;   /* 推进前抓下的仓库罐子位置（"罐子流进主台"的动画用） */
  var pendingLevelSwitch = false; /* 刚发了换关命令：等内核的 state 回来再重取关卡清单 */

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
          // 换关的回包：先拿到新世界的 state，再重取关卡清单（否则页头还写着上一关）
          if (pendingLevelSwitch) { pendingLevelSwitch = false; loadLevels(); }
          if (!askedAll) { askedAll = true; send('checkAllFeasibility', {}); }
          break;
        case 'frame':
          handleFrame(d);
          break;
        case 'steps':
          mergeSteps(d);
          break;
        case 'allFeasibilityResults':
          applyFeas(asList(d));
          renderOrders();
          break;
        case 'feasibilityResult':
          if (d.orderId !== undefined && d.result) { feas[d.orderId] = d.result; renderOrders(); }
          break;
        case 'autoCheckResults':
          applyFeas(asList(d));
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

  /* 内核这两条回包的 data 是数组本身；早期代码按 { results: [...] } 取，导致订单卡永远拿不到结果。 */
  function asList(d) {
    if (Array.isArray(d)) return d;
    if (d && Array.isArray(d.results)) return d.results;
    return [];
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

    // 阶段：内核判定的 world phase（setup / running / settled），页面只显示、不自己拼。
    var phaseEl = $('phase');
    if (phaseEl) {
      var ph = state.phase || 'setup';
      phaseEl.textContent = ph === 'running' ? '阶段 执行中' : (ph === 'settled' ? '阶段 已结束' : '阶段 配置');
      phaseEl.className = 'pill' + (ph === 'running' ? ' run' : (ph === 'settled' ? ' done' : ''));
    }

    var running = (state.processes || []).filter(function (p) { return p.kind === 'order_execution'; }).length;
    var busy = $('busy');
    if (busy) {
      busy.style.display = running ? '' : 'none';
      busy.textContent = '执行中 ' + running;
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
      '<span>' + liquidCountText(ws) + '</span>' +
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

  /* 液位：内核的 maxLiquids 是**每种液体各自**的上限（Workshop.TryReceiveLiquid 逐液体钳制），
     所以不能把几种液体加起来跟它比 —— 那会把"酒2+蜜4=6 > 5"这种合法状态显示成越界。 */
  function liquidCountText(ws) {
    var parts = [];
    Object.keys(ws.liquids || {}).forEach(function (k) { parts.push(k + ' ' + ws.liquids[k]); });
    if (!parts.length) return '液位 空 / 每种 ' + ws.maxLiquids;
    return '液位 ' + parts.join(' · ') + ' / 每种 ' + ws.maxLiquids;
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
  // 液体档位与罐子对齐：分子是速率、分母是 10 ⇒ 0 / 0.2 / 0.4 / 0.6 份每 tick。
  // （内核用千分之一定点累加，0.2 就是 5 tick 产一份，不需要浮点。）
  var LIQUID_RATES = [0, 2, 4, 6];

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
    // 液体也是资源：它不属于任何生产者，是"世界自己产"的，所以单独一组卡。
    // 速率与罐子同为小数（累加器攒够一份才出货），卡面同时显示"攒了多少"——
    // 这就是"为什么这一回合没产液体"的直接答案。
    var liquidRates = (ps.liquidRates || {});
    var liquidAcc = (ps.liquidAccumulated || {});
    var liquidCards = Object.keys(liquidRates).map(function (liquid) {
      var rate = liquidRates[liquid] || 0;
      var acc = liquidAcc[liquid] || 0;
      var presets = LIQUID_RATES.map(function (r) { return r / 10; });
      var extra = presets.some(function (p) { return Math.abs(rate - p) < 1e-9; }) ? null : rate;
      var btns = LIQUID_RATES.map(function (r) {
        var shown = r / 10;
        var on = Math.abs(rate - shown) < 1e-9;
        return '<button data-act="lrate" data-l="' + esc(liquid) + '" data-r="' + (r / 10) +
          '" class="' + (on ? 'on' : '') + '">' + (shown === 0 ? '停' : shown.toFixed(1)) + '</button>';
      }).join('');
      // 真实速率不在档位里（例如关卡给了 1 份/回合）⇒ 补一枚"当前 1.0"的只读 chip，
      // 否则玩家看到的永远是"没有一张卡是亮的"，也就看不出世界在全速产液体。
      if (extra !== null) {
        btns = '<span class="ratecur" title="当前速率来自关卡数据，不在可选档位里">当前 ' +
          extra.toFixed(2) + '</span>' + btns;
      }
      return '<div class="linecard liqcard"><div class="lh"><b>' + esc(liquid) + '</b>' +
        '<span>· 世界自产 · ' + rate.toFixed(2) + ' 份/回合 · 攒 ' + acc.toFixed(2) + '</span></div>' +
        '<div class="rates">' + btns + '</div></div>';
    }).join('');

    $('linesBody').innerHTML = (html || '<div class="empty">这个世界没有生产者</div>') +
      (liquidCards ? '<div class="linesep">液体</div><div class="lines">' + liquidCards + '</div>' : '');
    $('linesHint').textContent = '每张卡决定每回合产多少（罐子按形状、液体按品种）· 罐子已开工 ' + active + ' 条';
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

      var canStart = !!(f && f.feasible);
      var proc = findProcess(o.id);
      if (proc && proc.subject) {
        subjectBy[o.id] = proc.subject;
        requestSteps(o.id);
      }
      if (proc) html += execBlock(o, proc);

      html += '<div class="row">' +
        '<button class="mini" data-act="check" data-i="' + i + '">分析</button>' +
        '<button class="mini' + (canStart ? ' primary' : '') + '" data-act="start" data-i="' + i +
        '" data-id="' + esc(o.id) + '"' +
        ((isDone || proc || !canStart) ? ' disabled' : '') + ' title="' +
        (canStart ? '内核确认现在能做，点它开始执行' : '还做不了：先在进度条上看缺什么') + '">' +
        (proc ? '执行中' : (canStart ? '开始' : '还做不了')) + '</button>' +
        '<button class="mini" data-act="up" data-id="' + esc(o.id) + '">↑</button>' +
        '<button class="mini" data-act="down" data-id="' + esc(o.id) + '">↓</button>' +
        '<button class="mini" data-act="del" data-id="' + esc(o.id) + '">删除</button>' +
        '</div>';
      html += '</div>';
    });
    $('ordersBody').innerHTML = html;
  }

  /* 这一单正在进行的过程（state.processes 只列进行中的过程）。 */
  function findProcess(orderId) {
    var list = state.processes || [];
    for (var k = 0; k < list.length; k++) {
      if (list[k].kind === 'order_execution' && list[k].subjectId === orderId) return list[k];
    }
    return null;
  }

  /* 这一单最近做的一步（过程日志里的最后一条），没有就用内核给的计划顶替。 */
  function lastStepOf(orderId) {
    for (var k = logs.length - 1; k >= 0; k--) {
      if (logs[k].orderId === orderId) return logs[k];
    }
    return null;
  }

  function planAt(order, step) {
    var f = feas[order.id];
    if (!f || !f.solverPath || !step) return '';
    var s = f.solverPath[step - 1];
    return s ? describeStep(s) : '';
  }

  /* 谁 / 在做什么 / 第几步 / 还剩几刻 / 卡在哪 —— 全部来自 state.processes。 */
  function execBlock(o, p) {
    var gs = state.globalState || {};
    var slaveTxt = (o.type === 'B' && gs.slaveName) ? (' · 从台 ' + gs.slaveName) : '';
    var last = lastStepOf(o.id);
    var doing;
    if (last) doing = last.text.replace(/^第 \d+ 步 · /, '');
    else if (p.currentStep) doing = planAt(o, p.currentStep);
    else doing = planAt(o, 1) ? ('下一步：' + planAt(o, 1)) : '等资源就绪';
    var pct = p.totalSteps ? Math.round((p.currentStep || 0) * 100 / p.totalSteps) : 0;
    var out = '<div class="oexec">' +
      '<div class="ohead2"><span class="run">● 执行中</span>' +
      '<span class="who">主台 ' + esc(gs.masterName || '（未布置）') + slaveTxt + '</span>' +
      '<span class="oprog">第 ' + esc(p.currentStep || 0) + ' / ' + esc(p.totalSteps || 0) + ' 步</span></div>' +
      '<div class="obar run"><i style="width:' + pct + '%"></i></div>' +
      '<div class="odoing">正在做：' + esc(doing || '等资源就绪') +
      (p.remainingTicks ? ' · 还剩 ' + esc(p.remainingTicks) + ' 刻' : '') + '</div>';
    if (p.blockedReason) out += '<div class="onote bad">卡住：' + esc(p.blockedReason) + '</div>';
    return out + '</div>';
  }

  /* 内核给的求解步骤 → 一句人话。**只做措辞，不做判断**。 */
  function describeStep(s) {
    if (!s) return '?';
    if (typeof s === 'string') return s;
    // 内核给每一步写了 description（和执行日志同一句话）—— 页面只转述，不再自造措辞
    if (s.description) return String(s.description);
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
    // 逐只罐子：玩家要能看出"下一回合会流进主台的是哪几只"，光有数字做不到。
    // 顺序 = 流入时的取用顺序（主台基础形状优先），所以画出来的顺序就是将被拿走的那几只。
    var jarChips = (wh.jarList || []).map(function (j) {
      var short = (j.shape || '').replace('罐', '');
      return '<span class="jarchip" title="' + esc(j.shape) + '">' + esc(short) + '</span>';
    }).join('');
    $('stageBody').innerHTML =
      '<div><span class="big">' + esc(wh.jars || 0) + '</span> <span class="lab">只罐子</span></div>' +
      (jarChips ? '<div class="jarrow">' + jarChips + '</div>' : '<div class="empty">仓库里还没有罐子</div>') +
      '<div style="display:flex;gap:6px;flex-wrap:wrap">' + (liqs || '<span class="empty">没有液体</span>') + '</div>' +
      (wh.discardedJars ? '<div class="lab">溢出丢弃 ' + esc(wh.discardedJars) + ' 只</div>' : '');
    pulseStage(wh);
  }

  /* 入库闪一下：只比较前后两次数值，不做任何推断。 */
  var prevStage = null;
  function pulseStage(wh) {
    var jars = wh.jars || 0;
    var liquids = wh.liquids || {};
    if (prevStage) {
      var addedJars = jars - prevStage.jars;
      if (addedJars > 0) {
        var chips = $('stageBody').querySelectorAll('.jarchip');
        for (var k = Math.max(0, chips.length - addedJars); k < chips.length; k++) {
          chips[k].classList.add('new');
        }
      }
      Object.keys(liquids).forEach(function (key) {
        if (liquids[key] > (prevStage.liquids[key] || 0)) {
          $('stageBody').querySelectorAll('.liq').forEach(function (el) {
            if (el.textContent.indexOf(key) === 0) el.classList.add('up');
          });
        }
      });
    }
    prevStage = { jars: jars, liquids: JSON.parse(JSON.stringify(liquids)) };
  }

  /* ---------------- 记录：内核过程日志的中文投影 ---------------- */
  /* 这份表只做措辞。内核记的是"做了什么"（pourIn / step_applied / blocked…），
     玩家要看的是"谁在为哪一单做哪一步"，两者一一对应、不新增判断。 */
  var OP_ZH = {
    plan_accepted: '定下做法', started: '开工', step_begin: '准备一步', step_applied: '做了一步',
    step_skipped: '跳过一步', blocked: '卡住了', resumed: '恢复', grace_expired: '宽限到期',
    failed: '失败', succeeded: '成功', aborted: '中止', modifier_on: '法令生效',
    modifier_off: '法令结束', phase: '阶段',
    pourIn: '灌液', pourOut: '倒空', reshape: '改形', reshape_full: '满罐改形',
    use_existing: '直接用', transfer: '搬到从台', transferJar: '搬罐', transferLiquid: '搬液',
    match: '正好可用', skip: '跳过'
  };

  var askedAll = false;   // 首屏只问一次"每单现在能不能做"
  var subjectBy = {};    // subjectId → subject（过程日志按 (subject, subjectId) 成对查询）
  var stepSeen = {};     // seq → true（去重：同一份日志会被反复拉取）
  var logs = [];
  var lastStepAt = {};   // orderId → 最近一步的时间戳（节流）
  var stateTimer = null;

  function opZh(op, kind) {
    if (op && OP_ZH[op]) return OP_ZH[op];
    if (kind && OP_ZH[kind]) return OP_ZH[kind];
    return op || kind || '';
  }

  function toneOf(kind) {
    if (kind === 'succeeded' || kind === 'started') return 'ok';
    if (kind === 'failed' || kind === 'aborted' || kind === 'blocked' || kind === 'grace_expired') return 'bad';
    if (kind === 'phase' || kind === 'modifier_on' || kind === 'modifier_off') return 'phase';
    return '';
  }

  /* 内核 summary 形如「pourIn 陶工(圆罐)#1 蜜」：把开头的英文动作名换成中文，其余原样。 */
  function stepText(e) {
    var op = e.operation || '';
    var detail = String(e.summary || '');
    if (op && detail.indexOf(op) === 0) detail = detail.slice(op.length).trim();
    var head = e.stepIndex ? ('第 ' + e.stepIndex + ' 步 · ') : '';
    return head + opZh(op, e.kind) + (detail ? ' ' + detail : '');
  }

  function handleFrame(d) {
    if (d.subject !== undefined && d.subject !== null && d.subjectId !== undefined && d.subjectId !== null) {
      subjectBy[d.subjectId] = d.subject;
      requestSteps(d.subjectId);
    }
    // 帧只说"世界变了"，画什么要问内核再画一遍（页面不做增量推断）
    if (d.kind === 'worldChanged' || d.kind === 'processChanged') refreshState();
  }

  function refreshState() {
    if (stateTimer) return;
    stateTimer = setTimeout(function () { stateTimer = null; send('getState', {}); }, 120);
  }

  function requestSteps(orderId) {
    var subject = subjectBy[orderId];
    if (!subject) return;
    var now = Date.now();
    if (lastStepAt[orderId] && now - lastStepAt[orderId] < 350) return;
    lastStepAt[orderId] = now;
    send('getSteps', { subject: subject, subjectId: orderId });
  }

  function mergeSteps(d) {
    if (d.subject !== undefined && d.subjectId !== undefined) subjectBy[d.subjectId] = d.subject;
    var entries = d.entries || [];
    var added = 0;
    entries.forEach(function (e) {
      if (e.seq === undefined || stepSeen[e.seq]) return;
      stepSeen[e.seq] = true;
      logs.push({ k: logKey++, seq: e.seq, tick: e.tick, orderId: d.subjectId, kind: e.kind, text: stepText(e), tone: toneOf(e.kind) });
      added++;
    });
    if (!added) return;
    logs.sort(function (a, b) { return (a.seq || 0) - (b.seq || 0); });
    if (logs.length > MAX_LOG) logs = logs.slice(-MAX_LOG);
    flushLogs();
    renderOrders();
  }

  function addLocalLog(text, tone) {
    logs.push({ k: logKey++, seq: 0, tick: (state && state.turns) ? state.turns.used : 0, text: text, tone: tone || '' });
    if (logs.length > MAX_LOG) logs = logs.slice(-MAX_LOG);
    flushLogs();
  }

  /* 记录栏：筛选条 + 折行。筛选只是"看哪些"，不改数据本身。 */
  var EXEC_KINDS = ['started', 'step_begin', 'step_applied', 'phase', 'succeeded', 'failed', 'aborted', 'blocked', 'resumed', 'grace_expired'];
  function isExecKind(kind) { return EXEC_KINDS.indexOf(kind) >= 0; }

  function renderLogbar() {
    var bar = $('logbar');
    if (!bar) return;
    var ids = [];
    logs.forEach(function (l) { if (l.orderId !== undefined && ids.indexOf(l.orderId) < 0) ids.push(l.orderId); });
    var html = '<button class="mini' + (logFilter === 'all' ? ' on' : '') + '" data-act="logfilter" data-f="all">全部</button>' +
      '<button class="mini' + (logFilter === 'exec' ? ' on' : '') + '" data-act="logfilter" data-f="exec">只看执行</button>';
    ids.sort(function (a, b) { return a - b; }).forEach(function (id) {
      html += '<button class="mini' + (logFilter === id ? ' on' : '') + '" data-act="logfilter" data-f="' + id + '">委托 #' + esc(id) + '</button>';
    });
    bar.innerHTML = html;
  }

  function toggleFold(key) {
    if (key === null || key === undefined) return;
    folded[key] = !folded[key];
    flushLogs();
  }

  function flushLogs() {
    var el = $('logBody');
    renderLogbar();
    var shown = logs.filter(function (l) {
      if (logFilter === 'exec') return isExecKind(l.kind);
      if (logFilter !== 'all') return l.orderId === logFilter;
      return true;
    });
    if (!shown.length) {
      el.innerHTML = '<div class="empty">' + (logs.length ? '这个筛选下没有记录' : '还没有记录') + '</div>';
      return;
    }
    // 只有"原本就在底部"时才自动跟到底，否则玩家往上翻会被拽回去
    var atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 24;
    el.innerHTML = shown.slice(-MAX_LOG).map(function (l) {
      var cls = 'ln' + (l.tone ? ' ' + l.tone : '') + (folded[l.k] ? ' fold' : '');
      var who = l.orderId !== undefined ? ('<span class="lwho">委托 #' + esc(l.orderId) + '</span> ') : '';
      return '<div class="' + cls + '" data-k="' + esc(l.k) + '" title="点一下折起/展开">' +
        '<b>t' + esc(l.tick) + '</b> ' + who + esc(l.text) + '</div>';
    }).join('');
    if (atBottom || logFilter !== 'all') el.scrollTop = el.scrollHeight;
  }

  /* 罐子流动画：推进前抓下仓库里罐子的位置，状态回来后把"少掉的那几只"从仓库飞到主台。
     只画位移，不改数据——数据永远来自内核返回的 state。 */
  function captureStageJars() {
    if (!state) return null;
    var stage = $('stageBody'), master = $('masterBody');
    if (!stage || !master) return null;
    var chips = stage.querySelectorAll('.jarchip');
    if (!chips.length) return null;
    return {
      rects: Array.prototype.map.call(chips, function (c) { return c.getBoundingClientRect(); }),
      to: master.getBoundingClientRect()
    };
  }

  function runFlowAnimation(flow) {
    if (!flow || !state) return;
    if (window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;
    var after = (state.warehouse && state.warehouse.jarList) ? state.warehouse.jarList.length : 0;
    var moved = Math.max(0, flow.rects.length - after);
    if (!moved) return;
    var n = Math.min(moved, flow.rects.length);
    for (var i = 0; i < n; i++) {
      (function (rect, k) {
        var g = document.createElement('div');
        g.className = 'ghost';
        g.style.left = rect.left + 'px';
        g.style.top = rect.top + 'px';
        g.style.width = rect.width + 'px';
        g.style.height = rect.height + 'px';
        document.body.appendChild(g);
        var dx = (flow.to.left + flow.to.width / 2) - (rect.left + rect.width / 2);
        var dy = (flow.to.top + 20) - (rect.top + rect.height / 2);
        if (g.animate) {
          g.animate([
            { transform: 'translate(0,0) scale(1)', opacity: 1 },
            { transform: 'translate(' + dx + 'px,' + dy + 'px) scale(.85)', opacity: .2 }
          ], { duration: 520, delay: k * 70, easing: 'cubic-bezier(.3,.85,.35,1)', fill: 'forwards' });
        }
        setTimeout(function () { g.remove(); }, 640 + k * 70);
      })(flow.rects[i], i);
    }
  }

  function loadLevels() {
    return fetch('/api/levels')
      .then(function (r) { return r.json(); })
      .then(function (d) {
        levels = (d.levels || []).map(function (l) {
          return { index: l.index, name: l.name, hint: l.hint, turns: l.turns, current: l.index === d.current ? d.current : null };
        });
        renderTop();
      })
      .catch(function () { /* 没有关卡端点就按沙盒显示 */ });
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
      if (pendingFlow) { var flow = pendingFlow; pendingFlow = null; runFlowAnimation(flow); }
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
    openPicker('关卡选择（换关会重开一局：旧订单与记录都会清空）', html);
  }

  /* ---------------- 事件 ---------------- */
  document.addEventListener('click', function (ev) {
    // 记录行可点：折成一行 / 展开（长摘要会把别的记录挤下去）
    var ln = ev.target.closest('.ln');
    if (ln) { toggleFold(ln.getAttribute('data-k')); return; }

    var t = ev.target.closest('[data-act]');
    if (!t) return;
    var act = t.getAttribute('data-act');
    switch (act) {
      case 'advance':
        pendingFlow = captureStageJars();
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
      case 'gotolevel':
      {
        var idx = +t.getAttribute('data-idx');
        closePicker();
        // 换关 = 起一局新世界（内核侧 LoadLevel）：旧世界的订单、过程、记录都不再有效，
        // 所以页面上属于上一局的缓存必须整批丢掉，否则会画出"上一关的订单还挂着"的假象。
        logs = []; stepSeen = {}; subjectBy = {}; feas = {};
        folded = {}; logFilter = 'all'; pendingFlow = null;
        pendingLevelSwitch = true;
        send('loadLevel', { index: idx });
        toast('换到第 ' + idx + ' 关…');
        break;
      }
      case 'logfilter':
      {
        var f = t.getAttribute('data-f');
        logFilter = (f === 'all' || f === 'exec') ? f : +f;
        flushLogs();
        break;
      }
      case 'pickmaster': openPicker('选择主台', benchOptions('setmaster')); break;
      case 'pickslave': openPicker('选择从台', benchOptions('setslave')); break;
      case 'setmaster': closePicker(); send('setMaster', { index: +t.getAttribute('data-idx') }); break;
      case 'setslave': closePicker(); send('setSlave', { index: +t.getAttribute('data-idx') }); break;
      case 'noslave': send('setSlave', { index: -1 }); break;
      case 'lrate':
        send('updateLiquidRate', { liquid: t.getAttribute('data-l'), rate: +t.getAttribute('data-r') });
        break;
      case 'rate':
        send('updateProducerRate', {
          producerName: t.getAttribute('data-p'),
          shape: t.getAttribute('data-s'),
          rate: +t.getAttribute('data-r')
        });
        break;
      case 'check': send('checkFeasibility', { orderIndex: +t.getAttribute('data-i') }); break;
      case 'start':
        // **按 id 下命令**：index 是列表位置，玩家点的时候和内核收到的时候可能已经不同
        // （排序、删除、并发命令都会动它）。ResolveOrderIndex 优先认 orderId，这才指得准。
        send('startOrder', { orderId: +t.getAttribute('data-id') });
        addLocalLog('让内核开始做这一单', 'phase');
        setTimeout(function () { send('getState', {}); send('checkAllFeasibility', {}); }, 400);
        break;
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
  loadLevels();

  connect();
})();
