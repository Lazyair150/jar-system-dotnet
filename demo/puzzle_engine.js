/* 陶罐解谜 · 引擎
   核心是「剪四」：不做穷尽的可行性判定，只做一个单调贪心的下界。
   下界可能低于真实最大值，但它永远不说谎 —— 说能长到 3，就一定真能长到 3。 */

(function () {
  'use strict';

  var SHAPES = window.SHAPES;
  var LIQUIDS = window.LIQUIDS;
  var C = window.C;

  /* ---------- 基础谓词（全部对应原系统源码） ---------- */

  /* Config/Rules.cs:31-48 GetPossibleTransforms
     基础标记 → 其它基础标记 + 全部附加标记
     附加标记 / 外来形状 → 只能退回基础标记
     且不能原地改形 */
  function canTransform(from, to, def) {
    if (from === to) return false;
    if (def.base.indexOf(from) >= 0) {
      return def.base.indexOf(to) >= 0 || def.add.indexOf(to) >= 0;
    }
    return def.base.indexOf(to) >= 0;
  }

  function isBanned(def, liquid) {
    return def.ban.indexOf(liquid) >= 0;
  }

  function isBaseMarked(def, shape) {
    return def.base.indexOf(shape) >= 0;
  }

  /* 找一份可用的改形成本液体：未被禁、库存 >= 1。原系统 TryPayReshapeCost。 */
  function pickCostLiquid(def, liquid) {
    for (var i = 0; i < LIQUIDS.length; i++) {
      var l = LIQUIDS[i];
      if (!isBanned(def, l) && (liquid[l] || 0) >= 1) return l;
    }
    return null;
  }

  /* ---------- 一个罐子能满足一个需求吗（至多一次操作） ---------- */

  /* 返回 { op, to, costLiquid } 或 null。
     op ∈ match / pourout / pourin / reshape / reshapefull
     一罐一操作（规则.txt:47-50）是硬约束：所以「改形 + 灌液」这种两次操作的路一律不可。 */
  function tryOp(jar, req, def, liquid) {
    var wantLiquid = req.liquid;
    var d = jar.home || def;                                /* 罐子按它所在的台来判定 */

    if (wantLiquid === null) {
      /* 需求：空罐 */
      if (jar.liquid === null) {
        if (jar.shape === req.shape) return { op: 'match' };
        if (canTransform(jar.shape, req.shape, d)) {
          var cost = pickCostLiquid(d, liquid);
          if (cost) return { op: 'reshape', to: req.shape, costLiquid: cost };
          return null;
        }
        return null;
      }
      /* 罐子有液体，需求要空罐 */
      if (isBanned(d, jar.liquid)) return null;             /* PourOut 拒绝被禁液体 */
      if (jar.shape === req.shape) {
        if ((liquid[jar.liquid] || 0) < d.pool) return { op: 'pourout', liquid: jar.liquid };
        return null;                                        /* 液池已满，倒不出来 */
      }
      /* 形状不对但可改形：满罐改形，液体自耗，结果为空罐（Workshop.cs:308-317） */
      if (canTransform(jar.shape, req.shape, d)) {
        return { op: 'reshapefull', to: req.shape };
      }
      return null;
    }

    /* 需求：装着 wantLiquid 的罐子 */
    if (isBanned(d, wantLiquid)) return null;               /* 被禁液体不能倒入 */
    if (jar.shape !== req.shape) return null;               /* 改形后还要灌液 = 两次操作 */
    if (jar.liquid === wantLiquid) return { op: 'match' };
    if (jar.liquid === null) {
      /* 原系统 Workshop.PourIn 的三道门：未标记 / 形状**基础标记** / 液体未被禁。
         第三条此前漏了 —— 于是"附加标记"的形状（三角罐、长罐、扁罐、尖罐、方罐? 视台而定）
         也能白拿一次倒液，等于凭空多出一条原系统里不存在的路。 */
      if (!isBaseMarked(d, jar.shape)) return null;
      if ((liquid[wantLiquid] || 0) >= 1) return { op: 'pourin', liquid: wantLiquid };
      return null;
    }
    return null;                                            /* 装着别的液体 = 两次操作 */
  }

  function opCost(op) {
    if (op.op === 'match') return -1;
    if (op.op === 'pourout') return 0;
    if (op.op === 'reshapefull') return 0;
    if (op.op === 'reshape') return 1;
    if (op.op === 'pourin') return 1;
    return 5;
  }

  function applyOp(jar, op, liquid) {
    if (op.op === 'match') return;
    if (op.op === 'pourout') { liquid[op.liquid] = (liquid[op.liquid] || 0) + 1; jar.liquid = null; return; }
    if (op.op === 'pourin') { liquid[op.liquid] -= 1; jar.liquid = op.liquid; return; }
    if (op.op === 'reshape') { liquid[op.costLiquid] -= 1; jar.shape = op.to; return; }
    if (op.op === 'reshapefull') { jar.shape = op.to; jar.liquid = null; return; }
  }

  /* ---------- 剪四：单调贪心，逐层往上长 ---------- */

  /* 准备动作：这一回合满足不了，但做一步能让「下回合」满足。
     典型是「形状不对的空罐先改形」和「装着别种液体的罐子先倒空」。
     这是幼苗的横枝 —— 一罐一操作不让你一回合做完，那就分两回合做。 */
  /* 铺路动作：这一回合达不成，但做一步能让「以后」达成。
     两跳形状链（圆 → 扁 → 尖）是这里最要紧的一条 —— 换台之后遗留的罐子
     往往离目标隔着两个标记，一次改形只能走一半。 */
  function prepOp(jar, req, def, liquid) {
    var d = jar.home || def;
    if (tryOp(jar, req, d, liquid)) return null;   /* 能一步达成就不算铺路 */

    /* (b) 形状已经对、液体的种类不对 → 先倒空，下回合再灌 */
    if (jar.shape === req.shape) {
      if (jar.liquid !== null && !isBanned(d, jar.liquid) &&
          (liquid[jar.liquid] || 0) < d.pool &&
          req.liquid !== null && !isBanned(d, req.liquid)) {
        return { op: 'pourout', liquid: jar.liquid };
      }
      return null;
    }

    /* (a) 形状要两跳 → 先改到中转形状 */
    if (!canTransform(jar.shape, req.shape, d)) {
      var mids = d.base.concat(d.add).filter(function (m) {
        return m !== jar.shape && canTransform(jar.shape, m, d) && canTransform(m, req.shape, d);
      });
      if (mids.length === 0) return null;
      var mid = mids[0];
      if (jar.liquid === null) {
        var c = pickCostLiquid(d, liquid);
        if (!c) return null;
        return { op: 'reshape', to: mid, costLiquid: c };
      }
      if (isBanned(d, jar.liquid)) return null;
      return { op: 'reshapefull', to: mid };
    }

    return null;
  }

  /* 返回 { height, total, steps, liquid }。
     height 是「这一回合能满足几个需求」，是下界。
     steps 里 prep=true 的是「铺路动作」：它不交付，罐子留在场上。 */
  function grow(order, pool, def, liquidIn) {
    var jars = pool.map(function (j) {
      return { shape: j.shape, liquid: j.liquid, ref: j.ref, home: j.home, used: false };
    });
    var liquid = {};
    for (var i = 0; i < LIQUIDS.length; i++) liquid[LIQUIDS[i]] = liquidIn[LIQUIDS[i]] || 0;

    var left = order.reqs.map(function (r) { return { shape: r.shape, liquid: r.liquid }; });
    var steps = [];
    var height = 0;
    var prepsLeft = Math.max(1, order.reqs.length);

    while (true) {
      /* ---- 1) 直接满足：MRV，挑可选罐子最少的那个需求先做 ---- */
      var best = null;
      for (var ri = 0; ri < left.length; ri++) {
        var req = left[ri];
        var fits = [];
        for (var ji = 0; ji < jars.length; ji++) {
          if (jars[ji].used) continue;
          var op = tryOp(jars[ji], req, def, liquid);
          if (op) fits.push({ ji: ji, op: op });
        }
        if (fits.length === 0) continue;
        if (best === null || fits.length < best.fits.length) best = { ri: ri, req: req, fits: fits };
      }
      if (best !== null) {
        best.fits.sort(function (a, b) { return opCost(a.op) - opCost(b.op); });
        var f = best.fits[0];
        var j1 = jars[f.ji];
        applyOp(j1, f.op, liquid);
        j1.used = true;
        steps.push({ ref: j1.ref, op: f.op });
        left.splice(best.ri, 1);
        height++;
        continue;
      }

      if (left.length === 0) break;

      /* ---- 2) 铺路：为还没满足的需求做一步准备动作 ---- */
      if (prepsLeft <= 0) break;
      var did = false;
      for (var pi = 0; pi < left.length && !did; pi++) {
        for (var pj = 0; pj < jars.length; pj++) {
          if (jars[pj].used) continue;
          var pr = prepOp(jars[pj], left[pi], def, liquid);
          if (!pr) continue;
          applyOp(jars[pj], pr, liquid);
          jars[pj].used = true;
          steps.push({ ref: jars[pj].ref, op: pr, prep: true });
          prepsLeft--;
          did = true;
          break;
        }
      }
      if (!did) break;
    }

    return { height: height, total: order.reqs.length, steps: steps, liquid: liquid };
  }

  /* ---------- 世界 ---------- */

  function makeBench(def) {
    var liq = {};
    for (var i = 0; i < LIQUIDS.length; i++) liq[LIQUIDS[i]] = 0;
    return { def: def, jars: [], liquid: liq };
  }

  function findBench(id) {
    for (var i = 0; i < window.BENCHES.length; i++) {
      if (window.BENCHES[i].id === id) return window.BENCHES[i];
    }
    return null;
  }

  function findProd(id) {
    for (var i = 0; i < window.PRODS.length; i++) {
      if (window.PRODS[i].id === id) return window.PRODS[i];
    }
    return null;
  }

  function newGame(levelIndex) {
    var lv = window.LEVELS[levelIndex];
    var G = {
      lvIndex: levelIndex,
      lv: lv,
      turn: 1,
      master: null,
      sub: null,
      lines: [],
      orders: [],
      log: [],
      /* 换台迁徙的进行态：{ side, def, turns }。null = 没在迁徙 */
      migrating: null,
      over: null
    };
    for (var i = 0; i < C.MAX_LINES; i++) G.lines.push(null);
    G.master = makeBench(lv.benches.length ? findBench(lv.benches[0]) : window.BENCHES[0]);
    for (var k = 0; k < lv.startJars.length; k++) {
      G.master.jars.push({ shape: lv.startJars[k].shape, liquid: lv.startJars[k].liquid });
    }
    for (var l = 0; l < LIQUIDS.length; l++) {
      G.master.liquid[LIQUIDS[l]] = lv.startLiquid[LIQUIDS[l]] || 0;
    }
    for (var o = 0; o < lv.orders.length; o++) {
      G.orders.push({
        idx: o,
        cls: lv.orders[o].cls,
        reqs: lv.orders[o].reqs.map(function (r) { return { shape: r.shape, liquid: r.liquid }; }),
        total: lv.orders[o].reqs.length,
        done: false,
        progress: 0
      });
    }
    return G;
  }

  function log(G, html) {
    G.log.push(html);
    if (G.log.length > 200) G.log.shift();
  }

  /* 产线产出。**只进主台** —— 原系统 ProductionService.cs:55 唯一调用点是
     `Warehouse.FlowToMaster(master, jars, liquid)`，从台收不到任何新产出；
     罐子形状还必须在该台的基础标记里（FlowToMaster 只送 master.IsBaseMarked 的形状），
     否则产不出来 —— 这是工作台选择之所以有意义的原因。
     从台要拿罐子只能靠下面的 transferJar（原系统 TransferCommand）。 */
  function produce(G) {
    var bench = G.master;
    for (var i = 0; i < G.lines.length; i++) {
      var ln = G.lines[i];
      if (!ln) continue;
      var pd = ln.prod;

      if (pd.kind === 'jar') {
        if (!isBaseMarked(bench.def, pd.shape)) {
          log(G, '主台的基础标记是 ' + bench.def.base.join('/') + '，造不出' + pd.shape + '（丢弃）');
          continue;
        }
        var added = 0;
        for (var n = 0; n < pd.amount; n++) {
          if (bench.jars.length >= C.MAX_JARS) break;
          bench.jars.push({ shape: pd.shape, liquid: null });
          added++;
        }
        if (added > 0) log(G, '主台产出 <b>' + added + '</b> 只空' + pd.shape);
        else log(G, '主台罐位已满（' + C.MAX_JARS + '），' + pd.shape + '丢弃');
      } else {
        var L = pd.liquid;
        if (isBanned(bench.def, L)) { log(G, '主台禁' + L + '，产出的' + L + '被丢弃'); continue; }
        var pcap = bench.def.pool;
        var cur = bench.liquid[L] || 0;
        var add = Math.min(pd.amount, Math.max(0, pcap - cur));
        bench.liquid[L] = cur + add;
        if (add > 0) log(G, '主台产出 <b>' + add + '</b> 份' + L);
        else log(G, '主台' + L + '已满（' + pcap + '），产出被丢弃');
      }
    }
  }

  /* 转移：把一只罐子从一台搬到另一台（原系统 TransferCommand → Workshop.TransferOut/TransferIn）。
     它是**物流动作**，不横置罐子（TransferCommand.Execute 不调用 Mark），
     只受目标台的罐位限制。罐子换了台 ⇒ 之后按新台的规则判定（这是 B 类订单的承重点）。 */
  function transferJar(G, jar, toSide) {
    if (!G.sub) return false;
    if (toSide === 'sub') {
      if (G.master.jars.indexOf(jar) < 0) return false;
      if (G.sub.jars.length >= C.MAX_SUB_JARS) return false;
      removeJar(G.master, jar);
      G.sub.jars.push(jar);
      log(G, '转移：把罐子搬到从台');
      return true;
    }
    if (G.sub.jars.indexOf(jar) < 0) return false;
    if (G.master.jars.length >= C.MAX_JARS) return false;
    removeJar(G.sub, jar);
    G.master.jars.push(jar);
    log(G, '转移：把罐子搬回主台');
    return true;
  }

  /* 从台残留回流主台（原系统 OrderEvent.RollbackSlaveToMaster → SlaveRollback.Execute）。
     触发点只有订单结束：Deliver() 成功路径（B 类）与 Fail() 都会调它。
     口径与 ResourceTransferrer 一致：**主台接不下的直接丢弃**，不留在从台、不回流仓库。
     简化之处（如实记）：原系统按归属退回（只退 `_transferredJarIds` 与 `_slaveOriginalJarIds`
     这两批罐子 + 未用液体），原型不区分归属，整台回流。 */
  function backflow(G) {
    if (!G.sub) return;
    var moved = 0, dropped = 0;
    while (G.sub.jars.length) {
      var j = G.sub.jars.shift();
      if (G.master.jars.length < C.MAX_JARS) { G.master.jars.push(j); moved++; }
      else dropped++;
    }
    for (var i = 0; i < LIQUIDS.length; i++) {
      var L = LIQUIDS[i];
      var n = G.sub.liquid[L] || 0;
      if (!n) continue;
      var cap = isBanned(G.master.def, L) ? 0 : G.master.def.pool;
      var cur = G.master.liquid[L] || 0;
      var add = Math.min(n, Math.max(0, cap - cur));
      G.master.liquid[L] = cur + add;
      G.sub.liquid[L] = n - add;
      moved += add;
      dropped += n - add;
    }
    if (moved || dropped) {
      log(G, '回流：' + moved + ' 项回主台' + (dropped ? '，' + dropped + ' 项主台接不下被丢弃' : ''));
    }
  }

  /* A 类只看主台；B 类主从联合，每只罐子按它所在的台判定能力，液体按合并池算。 */
  function evalOrder(G, od) {
    var pool, liq;
    if (od.cls === 'A') {
      pool = G.master.jars.map(function (j) {
        return { shape: j.shape, liquid: j.liquid, ref: j, home: G.master.def };
      });
      liq = G.master.liquid;
    } else {
      if (!G.sub) return { height: 0, total: od.total, steps: [], reason: '没有从台' };
      pool = G.master.jars.map(function (j) {
        return { shape: j.shape, liquid: j.liquid, ref: j, home: G.master.def };
      }).concat(G.sub.jars.map(function (j) {
        return { shape: j.shape, liquid: j.liquid, ref: j, home: G.sub.def };
      }));
      liq = {};
      for (var i = 0; i < LIQUIDS.length; i++) {
        var L = LIQUIDS[i];
        liq[L] = (G.master.liquid[L] || 0) + (G.sub.liquid[L] || 0);
      }
    }
    return grow(od, pool, G.master.def, liq);
  }

  /* 从合并池里扣液体：先扣主台，不够再扣从台。 */
  function takeLiquid(G, L, n) {
    var need = n;
    var m = G.master.liquid[L] || 0;
    var use = Math.min(m, need);
    G.master.liquid[L] = m - use;
    need -= use;
    if (need > 0 && G.sub) {
      var s = G.sub.liquid[L] || 0;
      var use2 = Math.min(s, need);
      G.sub.liquid[L] = s - use2;
    }
  }

  /* 提交方案。
     —— 订单完成：全部步骤执行，罐子被拿走交付。
     —— 订单未完成：**步骤照样执行、罐子留在场上**。这是「幼苗」能长起来的关键：
        上一回合已经改好的形状必须留下来，否则每回合都从头再来，永远长不到 2/2。
        交出罐子仍然只发生在完成时（原系统 OrderEvent.Deliver 也是 JarMatcher.MatchAll
        全匹配才 RemoveJar）。已发生的操作不回滚（规则.txt:33-37）。 */
  function commitPlan(G, plan, od) {
    var complete = plan.height === plan.total;
    var prepCount = 0;
    for (var i = 0; i < plan.steps.length; i++) {
      var st = plan.steps[i];
      if (st.prep) prepCount++;
      var jar = st.ref;
      var op = st.op;
      if (op.op === 'pourout') { addLiquid(G, op.liquid, 1); jar.liquid = null; }
      else if (op.op === 'pourin') { takeLiquid(G, op.liquid, 1); jar.liquid = op.liquid; }
      else if (op.op === 'reshape') { takeLiquid(G, op.costLiquid, 1); jar.shape = op.to; }
      else if (op.op === 'reshapefull') { jar.shape = op.to; jar.liquid = null; }
    }
    if (complete) {
      var delivered = 0;
      for (var j = 0; j < plan.steps.length; j++) {
        if (plan.steps[j].prep) continue;
        removeJar(G.master, plan.steps[j].ref);
        if (G.sub) removeJar(G.sub, plan.steps[j].ref);
        delivered++;
      }
      od.done = true;
      od.progress = od.total;
      log(G, '订单 #' + (od.idx + 1) + '（' + od.cls + ' 类，' + od.total + ' 需求）<b style="color:#5aa86b">完成</b>');
      /* B 类订单结束 ⇒ 从台残留回流主台。
         原系统两条结束路径都调 RollbackSlaveToMaster()：OrderEvent.Deliver()（成功，B 类，
         见 OrderEvent.cs:196-197）与 OrderEvent.Fail()（见 :209）。 */
      if (od.cls === 'B') backflow(G);
    } else {
      od.progress = plan.height;
      if (prepCount > 0) {
        log(G, '订单 #' + (od.idx + 1) + ' 差 ' + (plan.total - plan.height) + ' 个，本回合只做铺路（' + prepCount + ' 步）');
      }
    }
  }

  /* 倒出的液体进主台液池，超过容量的部分丢弃。 */
  function addLiquid(G, L, n) {
    var cap = isBanned(G.master.def, L) ? 0 : G.master.def.pool;
    var cur = G.master.liquid[L] || 0;
    G.master.liquid[L] = Math.min(cap, cur + n);
  }

  function removeJar(bench, jar) {
    var i = bench.jars.indexOf(jar);
    if (i >= 0) bench.jars.splice(i, 1);
  }

  /* 一回合：产出 → 逐条订单尝试自动执行 → 回合数 +1 */
  /* 一回合的「结算」部分：产出 → 逐条订单尝试自动执行。
     不管胜负、不推进回合数——战斗层要自己决定什么时候算输、什么时候推进。 */
  function runTurnRaw(G) {
    if (G.migrating) {
      G.migrating.turns--;
      if (G.migrating.turns > 0) {
        log(G, '迁徙中…（还剩 ' + G.migrating.turns + ' 回合不产出）');
        return;
      }
      var m = G.migrating;
      G.migrating = null;
      applyBenchDef(G, m.side, m.def);
      log(G, '迁徙完成：' + (m.side === 'master' ? '主台' : '从台') + ' 换成 ' + m.def.name + '（本回合仍不产出）');
      return;
    }
    produce(G);
    for (var i = 0; i < G.orders.length; i++) {
      var od = G.orders[i];
      if (od.done) continue;
      var r = evalOrder(G, od);
      od.progress = r.height;
      if (r.steps && r.steps.length > 0) commitPlan(G, r, od);
    }
    for (var k = 0; k < G.orders.length; k++) if (G.orders[k].done) G.orders[k].progress = G.orders[k].total;
  }

  /* 一回合（解谜版）：结算 → 判胜负 → 回合数 +1 */
  function runTurn(G) {
    if (G.over) return;
    log(G, '—— 第 ' + G.turn + ' 回合 ——');
    runTurnRaw(G);

    var allDone = G.orders.every(function (o) { return o.done; });
    if (allDone) {
      G.over = { win: true, msg: '全部订单完成，用了 ' + G.turn + ' 回合' };
      log(G, '<b style="color:#5aa86b">' + G.over.msg + '</b>');
      return;
    }
    if (G.turn >= G.lv.turns) {
      var doneCount = G.orders.filter(function (o) { return o.done; }).length;
      G.over = { win: false, msg: '回合用尽，完成 ' + doneCount + '/' + G.orders.length };
      log(G, '<b style="color:#e05a4e">' + G.over.msg + '</b>');
      return;
    }
    G.turn++;
  }

  /* 换工作台。两种情形，与内核一致：
     · 台上**没有资源**（罐子 0 且液体全 0）⇒ 直接换。依据：`JarSystem.BeginSwitch` 在旧台无资源时
       不起过程，直接 `SetMaster`。
     · 台上**有资源** ⇒ 走**迁徙**：内核是 `WorkshopSwitchEvent.Execute() = Begin → RunReshape → RunTransfer`
       且 `SwitchProcessAdvancer` 一 tick 一段。原型把两段折成「占 2 个回合」：这两回合不产出、不结算订单。
       迁徙进行中不接受第二次换台 —— 内核 `BeginSwitch` 也拒第二个 `workshop_migration`。 */
  function setBench(G, side, def) {
    var isSub = side === 'sub';
    if (isSub && !G.sub) { G.sub = makeBench(def); return G.sub; }
    var b = isSub ? G.sub : G.master;
    if (b.def === def) return b;

    if (G.migrating) {
      log(G, '已在迁徙中，不能同时换第二次台');
      return b;
    }
    var hasRes = b.jars.length > 0;
    for (var k = 0; k < LIQUIDS.length; k++) if ((b.liquid[LIQUIDS[k]] || 0) > 0) hasRes = true;

    if (!hasRes) {
      log(G, '空台直接换：' + def.name);
      return applyBenchDef(G, side, def);
    }
    G.migrating = { side: side, def: def, turns: 2 };
    log(G, '换台迁徙开始（' + b.def.name + ' → ' + def.name + '，占 2 回合，期间不产出不结算）');
    return b;
  }

  /* 真正落定新的能力参数（ReplaceCapability 的语义：只换规则，不动罐子）；
     液池容量可能变小，超出部分被钳制并记日志。 */
  function applyBenchDef(G, side, def) {
    var b = side === 'master' ? G.master : G.sub;
    b.def = def;
    for (var i = 0; i < LIQUIDS.length; i++) {
      var L = LIQUIDS[i];
      var cap = (isBanned(def, L) ? 0 : def.pool);
      if ((b.liquid[L] || 0) > cap) {
        log(G, '换台后 ' + L + ' 容量变成 ' + cap + '，多出的 ' + ((b.liquid[L] || 0) - cap) + ' 份被丢弃');
        b.liquid[L] = cap;
      }
      if ((b.liquid[L] || 0) < 0) b.liquid[L] = 0;
    }
    return b;
  }

  window.ENG = {
    canTransform: canTransform,
    isBanned: isBanned,
    isBaseMarked: isBaseMarked,
    tryOp: tryOp,
    grow: grow,
    newGame: newGame,
    runTurn: runTurn,
    runTurnRaw: runTurnRaw,
    evalOrder: evalOrder,
    setBench: setBench,
    findBench: findBench,
    findProd: findProd,
    log: log,
    makeBench: makeBench,
    produce: produce,
    transferJar: transferJar
  };
})();
