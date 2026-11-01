/* 陶罐战线 · 战斗界面 */
(function () {
  'use strict';

  var BT = window.BATTLE;
  var SH = window.SHORT;
  var BENCHES = window.BENCHES;
  var PRODS = window.PRODS;
  var C = window.C;

  var B = null;
  var picker = null;   /* { kind:'bench'|'prod'|'enemy', side, slot } */

  var $ = function (id) { return document.getElementById(id); };
  function esc(s) { return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;'); }

  /* ---------------- 渲染 ---------------- */

  function render() {
    if (!B) return;
    renderTop();
    renderForecast();
    renderOrders();
    renderBenches();
    renderLines();
    renderLog();
    renderOver();
  }

  function renderTop() {
    var pct = Math.max(0, B.hp) / B.maxHp * 100;
    $('ename').textContent = B.enemy.name;
    $('hpfill').style.width = pct + '%';
    $('hptxt').textContent = Math.max(0, B.hp) + ' / ' + B.maxHp;
    $('turn').textContent = '第 ' + B.G.turn + ' 回合';
    $('next').disabled = B.status !== 'playing';
  }

  function renderForecast() {
    if (B.status !== 'playing') { $('forecast').style.display = 'none'; return; }
    $('forecast').style.display = '';
    $('forecast').innerHTML = B.preview
      ? '⚠ 它下一回合会：<b>' + BT.constraintText(B.preview) + '</b>　—— 现在就可以提前躲开它。'
      : '它暂时没有新动作。';

    var tags = B.constraints.map(function (c) {
      return '<span class="ctag">' + esc(BT.constraintText(c)) + '</span>';
    }).join('');
    $('consline').innerHTML = '<span class="lbl">正在生效的约束：</span>' +
      (tags || '<span class="lbl">（没有，还是完整的世界）</span>') +
      '<span class="lbl" style="margin-left:auto">罐位上限 ' + C.MAX_JARS + '</span>';
  }

  function reqChip(r) {
    var s = SH[r.shape] || r.shape;
    if (!r.liquid) return '<span class="reqchip">' + s + ' · 空</span>';
    return '<span class="reqchip fill">' + s + ' · ' + r.liquid + '</span>';
  }

  function renderOrders() {
    if (!B.G.orders.length) {
      $('orders').innerHTML = '<div style="color:#5f5b54">它还没有出题。</div>';
      return;
    }
    $('orders').innerHTML = B.G.orders.map(function (od, i) {
      var dead = BT.orderImpossible(B, od);
      var pct = od.total ? (od.progress / od.total * 100) : 0;
      return '<div class="order' + (dead ? ' dead' : '') + '">' +
        '<div class="oh">' +
          '<span class="onm">' + esc(od.label || ('题 ' + (i + 1))) + '</span>' +
          '<span class="cls">' + (od.cls === 'B' ? 'B·要两台' : 'A·主台') + '</span>' +
          '<span class="val">面值 ' + od.value + '</span>' +
        '</div>' +
        '<div class="reqs">' + od.reqs.map(reqChip).join('') + '</div>' +
        '<div class="progbar"><i style="width:' + pct + '%"></i></div>' +
        (dead ? '<div class="deadnote">这张题已经做不出来了 —— 它随时会收回去</div>' : '') +
        '</div>';
    }).join('');
  }

  function benchCard(side) {
    var G = B.G;
    var bench = side === 'master' ? G.master : G.sub;
    var label = side === 'master' ? '主台' : '从台';

    if (!bench) {
      return '<div class="benchcard">' +
        '<div class="bname"><span class="who">' + label + '</span>' +
        '<button data-act="pickbench" data-side="sub">＋ 配从台</button></div>' +
        '<div class="marks" style="color:#5f5b54">没有从台。B 类订单需要它。</div></div>';
    }

    var d = bench.def;
    var marks = '<span class="k">基础</span>' + d.base.map(function (s) { return SH[s] || s; }).join(' ') +
      '　<span class="k">附加</span>' + (d.add.length ? d.add.map(function (s) { return SH[s] || s; }).join(' ') : '—') +
      ((d.ban || []).length ? '　<span class="ban">禁 ' + d.ban.join(' ') + '</span>' : '');

    var jars = bench.jars.length
      ? bench.jars.map(function (j) {
          var dot = j.liquid ? '<span class="dot ' + (j.liquid === '酒' ? 'jiu' : 'mi') + '"></span>' : '';
          return '<span class="jarchip' + (j.liquid ? ' liq' : '') + '">' + dot + (SH[j.shape] || j.shape) + '</span>';
        }).join('')
      : '<span style="color:#5f5b54;font-size:11px">（空）</span>';

    var liq = window.LIQUIDS.map(function (L) {
      return L + ' <b>' + (bench.liquid[L] || 0) + '</b>';
    }).join('　');

    return '<div class="benchcard">' +
      '<div class="bname"><span class="who">' + label + '</span>' +
      '<button data-act="pickbench" data-side="' + side + '">' + esc(d.name) + ' ▾</button>' +
      (side === 'sub' ? '<button data-act="clearsub" style="margin-left:auto">撤</button>' : '') +
      '</div>' +
      '<div class="marks">' + marks + '</div>' +
      '<div class="jars">' + jars + '</div>' +
      '<div class="liqrow">' + liq + '　<span style="margin-left:auto">' +
        bench.jars.length + '/' + (side === 'master' ? C.MAX_JARS : C.MAX_SUB_JARS) + ' 罐</span></div>' +
      '</div>';
  }

  function renderBenches() {
    $('benches').innerHTML = benchCard('master') + benchCard('sub');
  }

  function renderLines() {
    var G = B.G;
    var html = '';
    for (var i = 0; i < 3; i++) {
      var ln = G.lines[i];
      if (!ln) {
        html += '<div class="lineslot empty">' +
          '<span class="nm">空槽 ' + (i + 1) + '</span>' +
          '<button data-act="pickprod" data-slot="' + i + '">选产线</button></div>';
      } else {
        var p = ln.prod;
        var desc = p.kind === 'jar'
          ? (SH[p.shape] || p.shape) + '罐 ×' + p.amount + '/回合'
          : p.liquid + ' ×' + p.amount + '/回合';
        html += '<div class="lineslot">' +
          '<span class="nm">' + desc + '</span>' +
          '<span class="tgt">产到' + (ln.target === 'sub' ? '从台' : '主台') + '</span>' +
          '<button data-act="toggletarget" data-slot="' + i + '">换台</button>' +
          '<button data-act="pickprod" data-slot="' + i + '">改</button></div>';
      }
    }
    $('lines').innerHTML = html;
  }

  function renderLog() {
    var el = $('log');
    var html = B.G.log.slice(-60).map(function (h) { return '<div>' + h + '</div>'; }).join('');
    el.innerHTML = html;
    el.scrollTop = el.scrollHeight;
  }

  function renderOver() {
    var el = $('over');
    if (B.status === 'playing') { el.className = ''; return; }
    el.className = 'show';
    var win = B.status === 'win';
    $('overbox').innerHTML =
      '<h3 class="' + (win ? 'win' : 'lose') + '">' + (win ? '打穿了' : '被锁死了') + '</h3>' +
      '<p>' + esc(B.msg) + '<br>' +
      '共 ' + B.stats.turns + ' 回合，吃掉 ' + B.stats.done + ' 张题，造成 ' + B.stats.dmg + ' 点伤害。</p>' +
      '<button class="primary" data-act="restart">再来一局</button>';
  }

  /* ---------------- 选择器 ---------------- */

  function openPicker(kind, opts) {
    picker = { kind: kind, side: opts.side, slot: opts.slot };
    var rows = '';
    if (kind === 'enemy') {
      rows = window.ENEMIES.map(function (e) {
        return '<div class="pickrow" data-act="chooseEnemy" data-id="' + e.id + '">' +
          '<span class="pn">' + esc(e.name) + '</span>' +
          '<span class="pd">' + e.hp + ' 血 · 每回合出 ' + e.draw + ' 张</span></div>';
      }).join('');
      $('picktitle').textContent = '选一个对手';
    } else if (kind === 'bench') {
      rows = BENCHES.map(function (b) {
        var d = BT.effectiveDef(b, B.constraints);
        var cur = opts.side === 'master'
          ? (B.masterBase && B.masterBase.id === b.id)
          : (B.subBase && B.subBase.id === b.id);
        return '<div class="pickrow' + (cur ? ' cur' : '') + '" data-act="chooseBench" data-id="' + b.id + '">' +
          '<span class="pn">' + esc(b.name) + '</span>' +
          '<span class="pd">基础 ' + d.base.map(function (s) { return SH[s] || s; }).join('') +
          ' · 附加 ' + (d.add.map(function (s) { return SH[s] || s; }).join('') || '—') +
          ' · 禁 ' + ((d.ban || []).join('') || '—') + '</span></div>';
      }).join('');
      $('picktitle').textContent = (opts.side === 'master' ? '主台' : '从台') + '换成哪一座？';
    } else {
      rows = PRODS.filter(function (p) { return p.kind === 'jar'; }).map(function (p) {
        return '<div class="pickrow" data-act="chooseProd" data-id="' + p.id + '">' +
          '<span class="pn">' + (SH[p.shape] || p.shape) + '罐产线</span>' +
          '<span class="pd">' + p.amount + ' 个 / 回合</span></div>';
      }).join('') +
      PRODS.filter(function (p) { return p.kind === 'liquid'; }).map(function (p) {
        return '<div class="pickrow" data-act="chooseProd" data-id="' + p.id + '">' +
          '<span class="pn">' + p.liquid + '产线</span>' +
          '<span class="pd">' + p.amount + ' 份 / 回合</span></div>';
      }).join('') +
      '<div class="pickrow" data-act="chooseProd" data-id="">' +
      '<span class="pn">撤掉这条产线</span><span class="pd">腾出槽位</span></div>';
      $('picktitle').textContent = '产线槽 ' + (opts.slot + 1) + ' 装什么？';
    }
    $('pickrows').innerHTML = rows;
    $('picker').className = 'show';
  }

  function closePicker() { $('picker').className = ''; picker = null; }

  /* ---------------- 交互 ---------------- */

  function nextTurn() {
    if (!B || B.status !== 'playing') return;
    BT.battleTurn(B);
    render();
  }

  function restart(enemyId) {
    C.MAX_JARS = BT.MAX_JARS_BASE();
    B = BT.newBattle(enemyId || B.enemy.id);
    closePicker();
    render();
  }

  function onAction(act, ds) {
    if (act === 'next') return nextTurn();
    if (act === 'restart') { $('over').className = ''; openPicker('enemy', {}); return; }
    if (act === 'pickbench') return openPicker('bench', { side: ds.side });
    if (act === 'clearsub') { BT.clearSub(B); return render(); }
    if (act === 'pickprod') return openPicker('prod', { slot: +ds.slot });
    if (act === 'toggletarget') {
      var ln = B.G.lines[+ds.slot];
      if (ln) ln.target = ln.target === 'sub' ? 'master' : 'sub';
      return render();
    }
    if (act === 'cancel') return closePicker();
    if (act === 'chooseEnemy') { closePicker(); return restart(ds.id); }
    if (act === 'chooseBench') {
      var def = null;
      for (var i = 0; i < BENCHES.length; i++) if (BENCHES[i].id === ds.id) def = BENCHES[i];
      if (def) BT.setBench(B, picker.side, def);
      closePicker();
      return render();
    }
    if (act === 'chooseProd') {
      var slot = picker.slot;
      var p = ds.id ? PRODS.filter(function (x) { return x.id === ds.id; })[0] : null;
      B.G.lines[slot] = p ? { prod: p, target: (B.G.lines[slot] && B.G.lines[slot].target) || 'master' } : null;
      closePicker();
      return render();
    }
  }

  document.addEventListener('click', function (ev) {
    var el = ev.target.closest ? ev.target.closest('[data-act]') : null;
    if (!el) return;
    onAction(el.getAttribute('data-act'), {
      side: el.getAttribute('data-side'),
      slot: el.getAttribute('data-slot'),
      id: el.getAttribute('data-id')
    });
  });

  $('next').addEventListener('click', nextTurn);

  /* 开局：先选对手 */
  openPicker('enemy', {});
})();
