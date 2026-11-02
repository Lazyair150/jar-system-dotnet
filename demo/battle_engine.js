/* 陶罐战线 · 战斗内核
   战斗层不碰 puzzle_engine 的一行逻辑：约束靠「烘焙派生工作台定义」实现，
   订单自动结算靠 ENG.runTurnRaw，锁死判定靠 ENG.grow。
   —— 引擎是判据，战斗只是给它换了一套世界规则和胜负条件。 */

(function () {
  'use strict';

  var ENG = window.ENG;
  var C = window.C;
  var BC = window.BATTLE_C;
  var ENEMIES = window.ENEMIES;
  var ORDER_POOL = window.ORDER_POOL;
  var LIQUIDS = window.LIQUIDS;

  var MAX_JARS_BASE = C.MAX_JARS;      /* 记下解谜版的原始罐位，战斗结束后要还原 */
  var orderSeq = 0;

  /* ---------------- 约束烘焙 ---------------- */

  function cloneDef(d) {
    return {
      id: d.id, name: d.name,
      base: d.base.slice(), add: d.add.slice(),
      ban: (d.ban || []).slice(), pool: d.pool
    };
  }

  /* 把一组约束烘焙进工作台定义，得到一个派生定义。
     派生定义就是个普通 BENCHES 对象，引擎完全不知道自己在被改造。 */
  function effectiveDef(baseDef, constraints) {
    var d = cloneDef(baseDef);
    for (var i = 0; i < constraints.length; i++) {
      var c = constraints[i];
      if (c.kind === 'banLiquid') {
        if (d.ban.indexOf(c.target) < 0) d.ban.push(c.target);
      } else if (c.kind === 'banShape') {
        d.base = d.base.filter(function (s) { return s !== c.target; });
        d.add = d.add.filter(function (s) { return s !== c.target; });
      }
    }
    return d;
  }

  function shrinkCount(constraints) {
    var n = 0;
    for (var i = 0; i < constraints.length; i++) {
      if (constraints[i].kind === 'shrinkJars') n++;
    }
    return Math.min(n, BC.MAX_JAR_SHRINK);
  }

  /* 把当前约束真正作用到世界上：换掉两台的 def，重算罐位上限。 */
  function applyConstraints(B) {
    var G = B.G;
    C.MAX_JARS = MAX_JARS_BASE - shrinkCount(B.constraints);
    G.master.def = effectiveDef(B.masterBase, B.constraints);
    /* 罐位缩了以后，超出的罐子不回收（原系统 ReplaceCapability 也是这个口径：
       标记约束的是操作合法性，不是存储合法性），只是产线再也产不进去。 */
    if (G.sub) G.sub.def = effectiveDef(B.subBase, B.constraints);
  }

  /* ---------------- 造局 ---------------- */

  function findEnemy(id) {
    for (var i = 0; i < ENEMIES.length; i++) if (ENEMIES[i].id === id) return ENEMIES[i];
    return ENEMIES[0];
  }

  function allBenchIds() {
    return window.BENCHES.map(function (b) { return b.id; });
  }
  function allProdIds() {
    return window.PRODS.map(function (p) { return p.id; });
  }

  /* 战报不再是"另一条叙述"，而是**帧流的投影**：
     每条战报都是一个 `note` 帧，和产出/流入/交付走同一条时间线、同一套信封（seq/tick/revision/快照）。
     界面那边用 event_text.js 的同一份措辞去念，所以解谜与战斗不会讲出两种故事。 */
  function emitB(B, ev) { ENG.emitEvent(B.G, ev); }
  function log(B, html) { emitB(B, { kind: 'note', text: html }); }

  function makeOrder(tmpl) {
    return {
      oid: ++orderSeq,
      idx: 0,
      cls: tmpl.cls,
      label: tmpl.label,
      value: tmpl.value,
      tid: tmpl.id,
      reqs: tmpl.reqs.map(function (r) { return { shape: r.shape, liquid: r.liquid }; }),
      total: tmpl.reqs.length,
      done: false,
      progress: 0
    };
  }

  function newBattle(enemyId) {
    var en = findEnemy(enemyId);
    var lv = {
      name: en.name, hint: en.desc, turns: 999999,
      benches: allBenchIds(), prods: allProdIds(),
      startJars: [], startLiquid: {}, orders: []
    };
    var G = {
      lvIndex: 0, lv: lv, turn: 1,
      master: ENG.makeBench(window.BENCHES[0]),
      sub: null,
      lines: [null, null, null],
      wh: ENG.newWarehouse(),          /* 产线产出先进仓库，再由 flowToMaster 流入主台 */
      feed: ENG.newFeed(),             /* 帧信封：战斗与解谜共用同一条过程流 */
      history: [],                     /* 回合历史：帧流按回合归档，战斗页也要能往前翻 */
      rev: 0,
      orders: [], log: [], over: null
    };

    var B = {
      G: G,
      enemy: en,
      hp: en.hp,
      maxHp: en.hp,
      masterBase: window.BENCHES[0],
      subBase: null,
      constraints: [],
      preview: null,
      planIdx: 0,
      status: 'playing',
      msg: '',
      deadStreak: 0,
      stats: { done: 0, dmg: 0, turns: 0 }
    };

    /* 开战帧必须排在最前：它之后才是它出题/预告约束。
       顺序在这里是可观察的语义（"先宣战，再出题"），写反了界面就会先看到题、再看到敌人。 */
    emitB(B, { kind: 'battleStart', enemy: en.name, hp: en.hp });
    applyConstraints(B);
    drawOrders(B, en.draw);
    B.preview = nextPreview(B);
    emitB(B, { kind: 'note', text: '它下一回合会：&lt;' + constraintText(B.preview) + '&gt;' });
    return B;
  }

  /* ---------------- 敌人出题 ---------------- */

  function nextPreview(B) {
    var plan = B.enemy.plan;
    var c = plan[B.planIdx % plan.length];
    return { kind: c.kind, target: c.target };
  }

  function drawOrders(B, n) {
    var G = B.G;
    var pool = B.enemy.pool;
    for (var k = 0; k < n; k++) {
      var onField = G.orders.map(function (o) { return o.tid; });
      var cands = pool.filter(function (id) { return onField.indexOf(id) < 0; });
      if (!cands.length) cands = pool;
      var pickId = cands[Math.floor(Math.random() * cands.length)];
      var tmpl = null;
      for (var i = 0; i < ORDER_POOL.length; i++) {
        if (ORDER_POOL[i].id === pickId) { tmpl = ORDER_POOL[i]; break; }
      }
      if (!tmpl) continue;
      var od = makeOrder(tmpl);
      if (G.orders.length >= BC.FIELD_MAX) {
        var dropped = G.orders.shift();
        emitB(B, { kind: 'enemyDraw', count: 1, label: dropped.label || '?', note: 'squeeze' });
      }
      G.orders.push(od);
      emitB(B, { kind: 'enemyDraw', count: 1, label: od.label, value: od.value, cls: od.cls });
    }
    reindex(G);
  }

  function reindex(G) {
    for (var i = 0; i < G.orders.length; i++) G.orders[i].idx = i;
  }

  /* 约束生效后，场上有些题已经永久做不出来了。它把那些收回去。 */
  function sweepImpossible(B) {
    var G = B.G;
    var kept = [];
    for (var i = 0; i < G.orders.length; i++) {
      var od = G.orders[i];
      if (!od.done && reqsImpossible(B, od.reqs)) {
        emitB(B, { kind: 'enemySweep', count: 1, label: od.label });
        continue;
      }
      kept.push(od);
    }
    G.orders = kept;
    reindex(G);
  }

  /* ---------------- 锁死判定 ----------------
     不能用「当前罐子」判：玩家没配产线时台上一只罐子都没有，那会把人冤枉死。
     也不能用「当前配置」判：玩家随时可以免费重配。
     唯一站得住的判据是**世界事实**——约束压缩之后，这个形状/液体还**存在不存在**
     「做出来」的可能。只要还有任何一种配置能把某只罐子做成，就不算锁死。

     可做出来的条件（原系统的形状转换规则 Config/Rules.cs:31-48）：
       · 台的基础标记里有这个形状  → 产线直接产
       · 台的附加标记里有这个形状、且该台基础标记非空、且世界上还有可用液体 → 改形过去
       · 需求要装的那种液体，这个台没有禁
     全部工作台都不满足 ⇒ 这张题永久不可解。 */

  function benchCanProvide(d, req, hasLiquid) {
    if (req.liquid && (d.ban || []).indexOf(req.liquid) >= 0) return false;
    if (d.base.indexOf(req.shape) >= 0) return true;                       /* 产线直接产 */
    if (d.add.indexOf(req.shape) >= 0 && d.base.length > 0 && hasLiquid) return true;  /* 改形过去 */
    return false;
  }

  function anyLiquidLeft(constraints) {
    for (var i = 0; i < window.BENCHES.length; i++) {
      var d = effectiveDef(window.BENCHES[i], constraints);
      for (var L = 0; L < LIQUIDS.length; L++) {
        if ((d.ban || []).indexOf(LIQUIDS[L]) < 0) return true;
      }
    }
    return false;
  }

  /* 这张题是不是「世界上已经没有任何办法做出来」 */
  function reqsImpossible(B, reqs) {
    var hasLiq = anyLiquidLeft(B.constraints);
    for (var i = 0; i < reqs.length; i++) {
      var r = reqs[i];
      var ok = false;
      for (var j = 0; j < window.BENCHES.length; j++) {
        var d = effectiveDef(window.BENCHES[j], B.constraints);
        if (benchCanProvide(d, r, hasLiq)) { ok = true; break; }
      }
      if (!ok) return true;
    }
    return false;
  }

  function orderImpossible(B, od) { return reqsImpossible(B, od.reqs); }

  /* 当前罐子能立刻长到第几层（给界面做提示用，不参与判负） */
  function potentialHeight(B, od) {
    var G = B.G;
    var best = 0;
    for (var i = 0; i < window.BENCHES.length; i++) {
      var d = effectiveDef(window.BENCHES[i], B.constraints);
      var pool = G.master.jars.map(function (j) {
        return { shape: j.shape, liquid: j.liquid, ref: j, home: d };
      });
      var liq = {};
      for (var L = 0; L < LIQUIDS.length; L++) liq[LIQUIDS[L]] = G.master.liquid[LIQUIDS[L]] || 0;

      if (od.cls === 'B') {
        var subJars = G.sub ? G.sub.jars : [];
        for (var s = 0; s < subJars.length; s++) {
          pool.push({ shape: subJars[s].shape, liquid: subJars[s].liquid, ref: subJars[s], home: d });
        }
        if (G.sub) for (var L2 = 0; L2 < LIQUIDS.length; L2++) liq[LIQUIDS[L2]] += G.sub.liquid[LIQUIDS[L2]] || 0;
      }

      var r = ENG.grow(od, pool, d, liq);
      if (r.height > best) best = r.height;
      if (best >= od.total) break;
    }
    return best;
  }

  /* 锁死 = 这个敌人**手上任何一张题**都已经做不出来了。
     不是看场上有几张，是看它的整个题池 —— 场上的题会被换下去，题池不会。
     只要题池里还有一张能做，就不算锁死。 */
  function isDeadlocked(B) {
    for (var i = 0; i < B.enemy.pool.length; i++) {
      var tid = B.enemy.pool[i];
      var tmpl = null;
      for (var j = 0; j < ORDER_POOL.length; j++) {
        if (ORDER_POOL[j].id === tid) { tmpl = ORDER_POOL[j]; break; }
      }
      if (!tmpl) continue;
      if (!reqsImpossible(B, tmpl.reqs)) return false;
    }
    return true;
  }

  /* ---------------- 一个战斗回合 ---------------- */

  function battleTurn(B) {
    if (B.status !== 'playing') return B;

    var G = B.G;
    B.stats.turns++;

    /* 1. 上一回合预告的约束，现在生效 */
    if (B.preview) {
      B.constraints.push(B.preview);
      B.planIdx++;
      emitB(B, { kind: 'constraintIn', text: constraintText(B.preview) });
      B.preview = null;
      applyConstraints(B);
    }

    log(B, '—— 第 ' + G.turn + ' 回合 ——');

    /* 2. 结算：产出 + 逐条订单自动尝试 */
    var before = G.orders.map(function (o) { return o.done; });
    ENG.runTurnRaw(G);

    /* 3. 收账 */
    var dmg = 0, doneNow = [];
    for (var i = 0; i < G.orders.length; i++) {
      if (G.orders[i].done && !before[i]) { doneNow.push(G.orders[i]); dmg += G.orders[i].value; }
    }
    if (doneNow.length) {
      B.stats.done += doneNow.length;
      B.stats.dmg += dmg;
      B.hp -= dmg;
      emitB(B, { kind: 'damage', count: doneNow.length, dmg: dmg, hp: Math.max(0, B.hp) });

      /* 夺回：完成订单就松一条约束（松最新的那条） */
      var freed = 0;
      for (var k = 0; k < doneNow.length; k++) {
        if (!B.constraints.length) break;
        var gone = B.constraints.pop();
        freed++;
        emitB(B, { kind: 'constraintFreed', text: constraintText(gone) });
      }
      if (freed) applyConstraints(B);

      /* 已完成的订单离场 */
      for (var m = 0; m < doneNow.length; m++) {
        var at = G.orders.indexOf(doneNow[m]);
        if (at >= 0) G.orders.splice(at, 1);
      }
      reindex(G);
    }

    /* 4. 判赢 */
    if (B.hp <= 0) {
      B.status = 'win';
      B.msg = '打穿了 ' + B.enemy.name + '（用了 ' + B.stats.turns + ' 回合）';
      emitB(B, { kind: 'battleWin', enemy: B.enemy.name, turns: B.stats.turns });
      return B;
    }

    /* 5. 它把已经做不出来的题收回去（它们再占着位置也没意义），然后继续出题 */
    sweepImpossible(B);
    drawOrders(B, B.enemy.draw);

    /* 6. 判锁死（连续两回合才判负，给产线一次补救机会） */
    if (isDeadlocked(B)) {
      B.deadStreak++;
      if (B.deadStreak >= 2) {
        B.status = 'lose';
        B.msg = '被锁死了 —— 场上没有任何一张题还能往上长';
        emitB(B, { kind: 'battleLose', enemy: B.enemy.name, turns: B.stats.turns });
        return B;
      }
      emitB(B, { kind: 'deadlocked', fatal: false });
    } else {
      B.deadStreak = 0;
    }

    /* 7. 预告下一条约束 */
    B.preview = nextPreview(B);

    /* 8. 推进 */
    G.turn++;
    return B;
  }

  function constraintText(c) {
    if (!c) return '（无）';
    if (c.kind === 'banShape') return '封住形状「' + c.target + '」';
    if (c.kind === 'banLiquid') return '禁掉液体「' + c.target + '」';
    if (c.kind === 'shrinkJars') return '缩掉 1 格罐位';
    return '？';
  }

  /* 中途换台要同步 base，再重新烘焙。从台不存在时直接建一个。 */
  function setBench(B, side, def) {
    var G = B.G;
    if (side === 'master') {
      B.masterBase = def;
      G.master.def = effectiveDef(def, B.constraints);
    } else {
      B.subBase = def;
      if (!G.sub) {
        G.sub = ENG.makeBench(effectiveDef(def, B.constraints));
      } else {
        G.sub.def = effectiveDef(def, B.constraints);
      }
    }
  }

  function clearSub(B) {
    B.subBase = null;
    B.G.sub = null;
  }

  window.BATTLE = {
    newBattle: newBattle,
    battleTurn: battleTurn,
    applyConstraints: applyConstraints,
    effectiveDef: effectiveDef,
    potentialHeight: potentialHeight,
    isDeadlocked: isDeadlocked,
    orderImpossible: orderImpossible,
    reqsImpossible: reqsImpossible,
    constraintText: constraintText,
    findEnemy: findEnemy,
    setBench: setBench,
    clearSub: clearSub,
    allBenchIds: allBenchIds,
    allProdIds: allProdIds,
    MAX_JARS_BASE: function () { return MAX_JARS_BASE; }
  };
})();
