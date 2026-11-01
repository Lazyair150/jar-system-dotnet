/* 陶罐解谜 · 界面 */

(function () {
  'use strict';

  var ENG = window.ENG;
  var C = window.C;
  var LIQUIDS = window.LIQUIDS;
  var SHORT = window.SHORT;

  var G = null;
  var ui = { picker: null };

  function $(id) { return document.getElementById(id); }
  function esc(s) { return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;'); }

  /* 顶部一闪而过的提示，不打断操作 */
  var toastTimer = null;
  function showToast(msg) {
    var t = $('toast');
    if (!t) return;
    t.textContent = msg;
    t.className = 'show';
    if (toastTimer) clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { t.className = ''; }, 2400);
  }

  /* ---------- 小组件 ---------- */

  function jarChip(j, extraClass) {
    var cls = 'jarchip' + (j.liquid ? ' wet wet-' + (j.liquid === '蜜' ? 'mi' : 'jiu') : '');
    if (extraClass) cls += ' ' + extraClass;
    var label = SHORT[j.shape] || j.shape;
    var tip = j.shape + (j.liquid ? ' · 装' + j.liquid : ' · 空');
    return '<span class="' + cls + '" title="' + tip + '">' + label + '</span>';
  }

  function reqChip(r) {
    var cls = 'reqchip' + (r.liquid ? ' wet wet-' + (r.liquid === '蜜' ? 'mi' : 'jiu') : '');
    var label = SHORT[r.shape] || r.shape;
    var tip = r.shape + (r.liquid ? ' · 需装' + r.liquid : ' · 需空');
    return '<span class="' + cls + '" title="' + tip + '">' + label + '</span>';
  }

  function liquidChips(b) {
    var out = '';
    for (var i = 0; i < LIQUIDS.length; i++) {
      var L = LIQUIDS[i];
      var n = b.liquid[L] || 0;
      var banned = ENG.isBanned(b.def, L);
      out += '<span class="liq liq-' + (L === '蜜' ? 'mi' : 'jiu') + (banned ? ' banned' : '') + '" ' +
        'title="' + L + (banned ? '（本台禁此液体）' : '') + '">' + L + ' ' + (banned ? '禁' : n + '/' + b.def.pool) + '</span>';
    }
    return out;
  }

  function benchCard(b, side) {
    var d = b.def;
    var h = '';
    h += '<div class="panel-hd">';
    h += '<span class="ptitle">' + (side === 'master' ? '主台' : '从台') + '</span>';
    h += '<span class="bname">' + esc(d.name) + '</span>';
    h += '<span class="pill">罐位 ' + b.jars.length + '/' + (side === 'master' ? C.MAX_JARS : C.MAX_SUB_JARS) + '</span>';
    /* 换台有代价：台上有资源时走 2 回合迁徙，期间不产出不结算、也不接受第二次换台。
       没有这个提示，玩家点了"换台"会觉得按钮坏了（能力要到迁徙结束才落定）。 */
    if (G.migrating && G.migrating.side === side) {
      h += '<span class="pill warn">迁徙中 → ' + esc(G.migrating.def.name) + ' 还剩' + G.migrating.turns + '回合</span>';
    }
    h += '<button class="mini" data-act="pickbench" data-side="' + side + '">换台</button>';
    h += '</div>';
    h += '<div class="marks">';
    h += '<span class="mk">基础：' + d.base.join(' ') + '</span>';
    h += '<span class="mk">附加：' + d.add.join(' ') + '</span>';
    h += '<span class="mk ban">禁：' + (d.ban.length ? d.ban.join(' ') : '无') + '</span>';
    h += '</div>';
    h += '<div class="row">' + liquidChips(b) + '</div>';
    h += '<div class="row jars">';
    if (b.jars.length === 0) h += '<span class="empty">（没有罐子）</span>';
    for (var i = 0; i < b.jars.length; i++) {
      h += jarChip(b.jars[i]);
      /* 转移：物流动作（原系统 TransferCommand → TransferOut/TransferIn），不横置罐子，受目标台罐位限制 */
      if (G.sub) {
        h += '<button class="xfer" data-act="transfer" data-side="' + side + '" data-i="' + i + '" ' +
          'title="把这只罐子转到另一台（换台之后按新台的规则判定）">' +
          (side === 'master' ? '→从' : '→主') + '</button>';
      }
    }
    h += '</div>';
    return h;
  }

  function linesPanel() {
    var h = '<div class="panel-hd"><span class="ptitle">产线 → 仓库 → 主台</span>' +
      '<span class="pill">' + G.lines.filter(Boolean).length + '/' + C.MAX_LINES + '</span>' +
      '<span class="hint">产线是资源生产线，产出**不依赖主台**；每回合仓库按上限流入主台（罐 3 / 每种液 2）</span></div>';
    h += '<div class="lines">';
    for (var i = 0; i < G.lines.length; i++) {
      var ln = G.lines[i];
      if (!ln) {
        h += '<button class="lineslot empty" data-act="pickline" data-i="' + i + '">+ 加产线</button>';
      } else {
        var pd = ln.prod;
        var txt = pd.kind === 'jar'
          ? (SHORT[pd.shape] || pd.shape) + ' ×' + pd.amount
          : pd.liquid + ' ×' + pd.amount;
        h += '<div class="linebox">';
        h += '<button class="lineslot" data-act="pickline" data-i="' + i + '">' + txt + '</button>';
        h += '</div>';
      }
    }
    h += '</div>';

    /* 仓库：形状与禁令只在"与工作台打交道"时起作用，所以没流入的东西在这里等着。 */
    var byShape = {};
    for (var k = 0; k < G.wh.jars.length; k++) {
      var s = G.wh.jars[k].shape;
      byShape[s] = (byShape[s] || 0) + 1;
    }
    var parts = [];
    Object.keys(byShape).forEach(function (s) { parts.push((SHORT[s] || s) + '×' + byShape[s]); });
    var wl = [];
    LIQUIDS.forEach(function (L) {
      if ((G.wh.liquid[L] || 0) > 0) wl.push(L + '×' + G.wh.liquid[L]);
    });
    var dis = G.wh.discardedJars + (G.wh.discardedLiquid['酒'] || 0) + (G.wh.discardedLiquid['蜜'] || 0);
    h += '<div class="whbar">仓库　罐 ' + G.wh.jars.length + (parts.length ? '（' + parts.join(' ') + '）' : '') +
      '　液 ' + (wl.length ? wl.join(' ') : '—') +
      (dis ? '　已丢弃 ' + dis : '') + '</div>';
    return h;
  }

  function orderCard(od) {
    var r = ENG.evalOrder(G, od);
    od.progress = od.done ? od.total : r.height;

    var h = '<div class="order' + (od.done ? ' done' : '') + '">';
    h += '<div class="ohead">';
    h += '<span class="oid">#' + (od.idx + 1) + '</span>';
    h += '<span class="ocls' + (od.cls === 'B' ? ' b' : '') + '">' + od.cls + ' 类</span>';
    h += '<span class="oprog">' + od.progress + '/' + od.total + '</span>';
    if (od.done) h += '<span class="obadge">完成</span>';
    h += '</div>';

    h += '<div class="obar"><i style="width:' + Math.round(100 * od.progress / od.total) + '%"></i></div>';

    h += '<div class="oreqs">';
    for (var i = 0; i < od.reqs.length; i++) h += reqChip(od.reqs[i]);
    h += '</div>';

    if (!od.done && od.cls === 'B' && !G.sub) {
      h += '<div class="onote bad">需要从台才能执行</div>';
    } else if (!od.done) {
      /* 引擎给的具体原因优先于泛泛的进度提示：例如
         「交付必须在从台：还要 1 只从主台转过去，从台只剩 0 个罐位」——
         没有这一行，玩家只会看到 0/2 和一句"一只也配不上"，不知道该动哪只手。 */
      if (r.reason) h += '<div class="onote bad">' + esc(r.reason) + '</div>';
      else if (od.progress === od.total) h += '<div class="onote good">下回合可以完成</div>';
      else if (od.progress === 0) h += '<div class="onote bad">一只也配不上 —— 检查产线形状 / 液体禁令 / 罐位是否满</div>';
      else h += '<div class="onote">还差 ' + (od.total - od.progress) + ' 个需求</div>';
    }

    if (!od.done && r.steps && r.steps.length) {
      h += '<div class="oplan"><span class="olab">引擎打算</span>';
      for (var s = 0; s < r.steps.length; s++) {
        h += '<span class="opc">' + opText(r.steps[s]) + '</span>';
      }
      h += '</div>';
    }

    h += '</div>';
    return h;
  }

  function opText(st) {
    var j = st.ref;
    var op = st.op;
    var head = (SHORT[j.shape] || j.shape);
    if (op.op === 'match') return head + ' 照用';
    if (op.op === 'pourout') return head + ' 倒出' + op.liquid;
    if (op.op === 'pourin') return head + ' 灌' + op.liquid;
    if (op.op === 'reshape') return head + '→' + (SHORT[op.to] || op.to) + ' 耗1' + op.costLiquid;
    if (op.op === 'reshapefull') return head + '→' + (SHORT[op.to] || op.to) + ' 吃掉罐内' + (j.liquid || '液体');
    return head + ' ？';
  }

  /* ---------- 主渲染 ---------- */

  function render() {
    var lv = G.lv;
    var doneCount = G.orders.filter(function (o) { return o.done; }).length;

    var top = '';
    top += '<span class="lvname">第 ' + (G.lvIndex + 1) + ' / ' + window.LEVELS.length + ' 关 · ' +
      esc(lv.name) + '</span>';
    top += '<span class="turn">第 ' + G.turn + ' / ' + lv.turns + ' 回合</span>';
    top += '<span class="pill">订单 ' + doneCount + '/' + G.orders.length + '</span>';
    if (G.over) {
      top += '<span class="result ' + (G.over.win ? 'win' : 'lose') + '">' + esc(G.over.msg) + '</span>';
    }
    top += '<span class="spacer"></span>';
    top += '<button class="mini" data-act="help">玩法</button>';
    top += '<button class="mini" data-act="restart">重开本关</button>';
    if (G.lvIndex > 0) top += '<button class="mini" data-act="prevlv">上一关</button>';
    if (G.lvIndex < window.LEVELS.length - 1) top += '<button class="mini" data-act="nextlv">下一关</button>';
    $('topbar').innerHTML = top;
    $('hintbar').innerHTML = lv.hint ? '<span class="lvhint">' + esc(lv.hint) + '</span>' : '';

    $('masterPanel').innerHTML = benchCard(G.master, 'master');
    if (G.sub) {
      $('subPanel').innerHTML = benchCard(G.sub, 'sub');
      $('subPanel').className = 'panel';
    } else {
      $('subPanel').innerHTML = '<div class="panel-hd"><span class="ptitle">从台</span></div>' +
        '<div class="row"><button class="mini" data-act="pickbench" data-side="sub">装备从台</button>' +
        '<span class="hint">B 类订单需要从台</span></div>';
      $('subPanel').className = 'panel dim';
    }

    $('linesPanel').innerHTML = linesPanel();

    var oh = '<div class="panel-hd"><span class="ptitle">订单</span>' +
      '<span class="hint">进度是「现在就能长到第几层」，会随配置实时变化</span></div>';
    for (var i = 0; i < G.orders.length; i++) oh += orderCard(G.orders[i]);
    $('ordersPanel').innerHTML = oh;

    var lh = '<div class="panel-hd"><span class="ptitle">记录</span></div><div class="log">';
    var start = Math.max(0, G.log.length - 40);
    for (var k = start; k < G.log.length; k++) lh += '<div class="ln">' + G.log[k] + '</div>';
    lh += '</div>';
    $('logPanel').innerHTML = lh;
    var lg = $('logPanel').querySelector('.log');
    if (lg) lg.scrollTop = lg.scrollHeight;

    var bt = '';
    if (G.over) {
      bt += '<span class="result ' + (G.over.win ? 'win' : 'lose') + '">' + esc(G.over.msg) + '</span>';
      bt += '<button class="mini" data-act="restart">重开本关</button>';
      if (G.over.win && G.lvIndex < window.LEVELS.length - 1) {
        bt += '<button class="primary" data-act="nextlv">进入下一关 →</button>';
      }
    } else {
      var turnLabel = '推进一回合';
      if (G.migrating) {
        turnLabel += '（迁徙中，本回合不产出 · 还剩 ' + G.migrating.turns + ' 回合）';
      }
      bt += '<button class="primary big" data-act="turn">' + turnLabel + '</button>';
    }
    $('bottomBar').innerHTML = bt;

    renderPicker();
  }

  /* ---------- 选择器 ---------- */

  function renderPicker() {
    var p = $('picker');
    if (!ui.picker) { p.className = 'hidden'; p.innerHTML = ''; return; }
    p.className = '';
    if (ui.picker.kind === 'help') {
      /* 帮助面板必须走渲染管线：以前是直接写 innerHTML，任何一次重渲染
         （例如推进一回合）都会把 renderPicker 的空状态写回去，面板当场消失。 */
      p.innerHTML = helpHtml();
      return;
    }
    var h = '<div class="pickbox">';
    if (ui.picker.kind === 'bench') {
      h += '<div class="ph">选工作台 · ' + (ui.picker.side === 'master' ? '主台' : '从台') + '</div>';
      var avail = G.lv.benches;
      for (var i = 0; i < avail.length; i++) {
        var d = ENG.findBench(avail[i]);
        h += '<button class="opt" data-act="setbench" data-side="' + ui.picker.side + '" data-id="' + d.id + '">';
        h += '<span class="on">' + esc(d.name) + '</span>';
        h += '<span class="od">基础 ' + d.base.join('·') + ' ｜ 附加 ' + d.add.join('·') +
          ' ｜ 禁 ' + (d.ban.length ? d.ban.join('·') : '无') + ' ｜ 液池 ' + d.pool + '/种</span>';
        h += '</button>';
      }
      if (ui.picker.side === 'sub') {
        h += '<button class="opt danger" data-act="clearsub">卸下从台</button>';
      }
    } else {
      h += '<div class="ph">选产线 · 槽 ' + (ui.picker.i + 1) + '</div>';
      for (var k = 0; k < G.lv.prods.length; k++) {
        var pd = ENG.findProd(G.lv.prods[k]);
        var txt = pd.kind === 'jar' ? ('罐子产线：' + pd.shape + ' ×' + pd.amount + '/回合')
          : ('液体产线：' + pd.liquid + ' ×' + pd.amount + '/回合');
        h += '<button class="opt" data-act="setline" data-i="' + ui.picker.i + '" data-id="' + pd.id + '">' +
          '<span class="on">' + esc(txt) + '</span></button>';
      }
      if (G.lines[ui.picker.i]) h += '<button class="opt danger" data-act="setline" data-i="' + ui.picker.i + '" data-id="">清空此槽</button>';
    }
    h += '<button class="opt close" data-act="closepicker">取消</button>';
    h += '</div>';
    p.innerHTML = h;
  }

  function openPicker(obj) { ui.picker = obj; render(); }
  function closePicker() { ui.picker = null; render(); }

  /* ---------- 交互 ---------- */

  document.addEventListener('click', function (e) {
    var el = e.target;
    while (el && el !== document.body && !el.getAttribute('data-act')) el = el.parentElement;
    if (!el || el === document.body) return;
    var act = el.getAttribute('data-act');

    if (act === 'turn') { ENG.runTurn(G); ui.picker = null; render(); return; }
    if (act === 'restart') { G = ENG.newGame(G.lvIndex); ui.picker = null; render(); return; }
    if (act === 'prevlv') { G = ENG.newGame(Math.max(0, G.lvIndex - 1)); ui.picker = null; render(); return; }
    if (act === 'nextlv') { G = ENG.newGame(Math.min(window.LEVELS.length - 1, G.lvIndex + 1)); ui.picker = null; render(); return; }
    if (act === 'help') { showHelp(); return; }
    if (act === 'pickbench') { openPicker({ kind: 'bench', side: el.getAttribute('data-side') }); return; }
    if (act === 'pickline') { openPicker({ kind: 'line', i: Number(el.getAttribute('data-i')) }); return; }
    if (act === 'closepicker') { closePicker(); return; }

    if (act === 'setbench') {
      var d = ENG.findBench(el.getAttribute('data-id'));
      var side = el.getAttribute('data-side');
      if (d) {
        if (G.migrating) {
          /* 迁徙中拒绝第二次换台（内核 BeginSwitch 也拒第二个 workshop_migration）。
             不给提示的话，玩家会以为点没生效。 */
          showToast('正在迁徙中（还剩 ' + G.migrating.turns + ' 回合），不能再换台');
        } else {
          var wasRes = (side === 'master' ? G.master : (G.sub || { jars: [], liquid: {} }));
          ENG.setBench(G, side, d);
          if (G.migrating) showToast('台上有资源 → 开始迁徙，占 2 回合且期间不产出');
        }
        ui.picker = null; render();
      }
      return;
    }
    if (act === 'clearsub') {
      G.sub = null; ui.picker = null; render(); return;
    }
    if (act === 'setline') {
      var i = Number(el.getAttribute('data-i'));
      var pid = el.getAttribute('data-id');
      var old = G.lines[i];
      G.lines[i] = pid ? { prod: ENG.findProd(pid) } : null;
      ui.picker = null; render(); return;
    }
    if (act === 'transfer') {
      var ts = el.getAttribute('data-side');
      var ti = Number(el.getAttribute('data-i'));
      var bench = ts === 'master' ? G.master : G.sub;
      var jar = bench && bench.jars[ti];
      if (!jar) return;
      var ok = ENG.transferJar(G, jar, ts === 'master' ? 'sub' : 'master');
      if (!ok) showToast(ts === 'master' ? '从台罐位已满，搬不过去' : '主台罐位已满，搬不回来');
      ui.picker = null;
      render();
      return;
    }
  });

  /* ---------- 玩法说明 ---------- */

  function showHelp() { openPicker({ kind: 'help' }); }

  function helpHtml() { return '<div class="pickbox help">' +
      '<div class="ph">玩法</div>' +
      '<p><b>你只做一件事：配置。</b>引擎自己跑。</p>' +

      '<p><b>一回合</b>：产线产出 → 逐条订单自动尝试执行 → 回合数 +1。订单的罐子从台上被拿走（交付即消耗）。</p>' +

      '<p><b>进度 N/M 是什么</b>：不是「能不能完成」，是「现在就能稳定长到第几层」。它是个<b>下界</b>——可能低于真实最优，但永远不会说谎。你换一条产线、换一个工作台，这个数字就会跳。</p>' +

      '<p><b>引擎会做的四种动作</b>（每个罐子最多用一次，这就是「一罐一操作」）：</p>' +
      '<ul>' +
      '<li><b>直接匹配</b>：罐子形状和液体都对</li>' +
      '<li><b>倒出液体</b>：满罐倒空 → 罐子变空，液体进池子（池子该液体已满则不行，被禁液体也不能倒出）</li>' +
      '<li><b>倒入液体</b>：空罐 + 池中 1 份液体 → 装满（液体被禁则不行）</li>' +
      '<li><b>改形</b>：空罐改形要消耗 <b>1 份</b>可用液体；满罐改形不消耗池子里的液体，但<b>罐内液体被吃掉、结果是一只空罐</b>（罐内液体被禁则不可）</li>' +
      '</ul>' +

      '<p><b>为什么形状很重要</b>：改形只能「基础标记 → 其它基础标记 + 全部附加标记」，或者「附加标记/外来形状 → 只能退回基础标记」。<b>附加标记是死胡同。</b></p>' +

      '<p><b>为什么禁令很重要</b>：被禁液体不能倒入、不能存储、不能当改形成本。玄武台和朱雀台都禁酒，白虎台也禁酒，青龙台和麒麟台禁蜜。</p>' +

      '<p><b>一罐一操作的含义</b>：一只形状不对的<b>空</b>罐可以改形成对的形状（但还是空的）；一只形状对的空罐可以灌液。但「改形 + 灌液」是两次操作，<b>做不到</b>。所以需要装液罐的订单，只能靠「本来就是那个形状的空罐 + 灌液」或「本来就装着那种液体的罐子」。</p>' +

      '<p><b>资源是流动的：产线 → 仓库 → 主台</b>。产线是<b>世界里的生产线</b>，它产出什么都照产，<b>不看当前主台</b>；产出先进<b>仓库</b>（罐 200 / 液 200，溢出才丢弃并计数）。每回合仓库按上限往主台送：罐子最多 <b>3 只</b>、每种液体最多 <b>2 份</b>。<b>任何形状的罐子都能进主台</b> —— 基础标记约束的是"能不能操作它"（灌注 / 改形），不是"能不能存它"。被主台<b>禁</b>的液体则一份也进不去，留在仓库里等换台。</p>' +

      '<p><b>转移</b>：每只罐子旁边有 <b>→从</b> / <b>→主</b> 按钮，点一下就把这只罐子搬到另一台。转移本身<b>不花回合</b>，只受目标台罐位限制。罐子换了台，之后按<b>新台</b>的规则判定 —— 这是 B 类订单的承重点。</p>' +

      '<p><b>B 类订单必须在从台交付</b>：所有需求罐最后都得落在从台。主台上的罐子要交付就得转移过去，而能不能转过去受<b>从台罐位</b>限制。从台只有 4 格，所以别把它塞满。</p>' +

      '<p><b>换台有代价</b>：台上有资源时，换台要走一次<b>迁徙</b>，占 <b>2 个回合</b>，这两回合<b>不产出、不结算订单</b>，顶上会显示「迁徙中」。台是空的（没有罐子、液体全 0）时才可以直接换。迁徙结束前还会把游离液体灌进旧台基础形状的空罐带走，而不是白丢。</p>' +

      '<p><b>订单结束时从台会回流</b>：B 单完成后，从台上剩下的罐子和液体都退回主台，主台接不下的直接丢弃。所以别把东西堆在从台。</p>' +

      '<p><b>配置随时可改</b>，不消耗回合；改产线、换台、转移都不点「推进一回合」就不会推进。</p>' +

      '<p class="dim">这一版是纯解谜：赢了没有奖励，输了重来。它要验证的就一件事——「调对」这个动作本身有没有快感。</p>' +
      '<button class="opt close" data-act="closepicker">知道了</button>' +
      '</div>'; }

  /* ---------- 启动 ---------- */

  function boot() {
    G = ENG.newGame(0);
    render();
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', boot);
  } else {
    boot();
  }
})();
