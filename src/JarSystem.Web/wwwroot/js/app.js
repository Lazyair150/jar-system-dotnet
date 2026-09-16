// ============================================================
// 连接与重连（T1）
// 原实现只在加载时 new 一次 WebSocket，且没有 onclose/onerror：
// 后端一重启，页面就永久静默失效。现在改为 connect() + 指数退避重连。
// ============================================================
let ws = null;
let reconnectDelayMs = 1000;

let currentState = /** @type {StateDto|null} */ (null);
let selectedOrderId = null;
let autoCheckEnabled = false;
let editingCapacityIndex = null;   // 正在编辑容量的工作台索引（避免被 state 重绘打断）
let capacityDraft = null;          // 编辑中的草稿值 { jars: string, liquids: string }

// 世界元数据（S4）：由后端在连接建立时一次性下发（world 消息），
// 前端据此渲染形状下拉框/标记选择器 —— 不再维护一份形状清单副本（原 js/constants.js）。
/** @type {{ shapes: string[], liquids: string[] }} */
let worldMeta = { shapes: [], liquids: [] };

// S4b-2（卡顿修复）：
//  · uiScheduled —— 把同一帧内的多条 state 合并成一次渲染（原来每条消息都全量重建面板）
//  · switchAnimating —— 切换动画期间暂停重绘（避免动画节点被 innerHTML 换掉）
//  · MAX_SWITCH_DOTS —— 圆点动画的节点上限（原实现按 operation 数量线性开 setTimeout）
let uiScheduled = false;
let switchAnimating = false;
const MAX_SWITCH_DOTS = 8;

function scheduleUI() {
  if (uiScheduled) return;
  uiScheduled = true;
  requestAnimationFrame(() => { uiScheduled = false; updateUI(); });
}

/**
 * 发送命令：命令名与**载荷键名**都由后端生成的类型约束（`wwwroot/js/contract.d.ts`，
 * 由 verify.bat 第 5 步的 `--dump-schema` 产出）。
 *
 * 用法：`send('createOrder', { requirements: reqs, orderType: 'A' })`；无载荷的命令写 `send('startTick')`。
 * 语义：产出的 JSON 与原先手写的 `ws.send(JSON.stringify({type, data}))` **逐字节相同**；
 * 区别只在于——把 `type` 写错、或把载荷键名写错，在 `jsconfig.json(checkJs)` 下会**当场报错**。
 *
 * @template {CommandName} T
 * @param {T} type
 * @param {CommandPayloadMap[T]} [data]
 */
function send(type, data) {
  ws.send(data === undefined ? JSON.stringify({ type }) : JSON.stringify({ type, data }));
}

function connect() {
  ws = new WebSocket(`ws://${window.location.host}/ws`);

  ws.onopen = () => {
    reconnectDelayMs = 1000;
    log('系统连接成功', 'info');
    send('startTick');
  };

  ws.onmessage = handleMessage;

  ws.onclose = () => {
    const seconds = Math.round(reconnectDelayMs / 1000);
    log(`连接断开，${seconds} 秒后重连…`, 'fail');
    setTimeout(connect, reconnectDelayMs);
    reconnectDelayMs = Math.min(reconnectDelayMs * 2, 15000);
  };

  ws.onerror = () => { /* 紧跟着会触发 onclose，这里不重复处理 */ };
}

function handleMessage(event) {
  // `type` 由 contract.d.ts 的 MessageType 联合类型约束 ⇒ case 写错名字会被检查出来；
  // `data` 保持 any（各消息载荷形状不同，逐个标注会淹没噪音，关键字段在渲染函数里按 DTO 用）。
  const { type, data } = /** @type {{ type: MessageType, data: any }} */ (JSON.parse(event.data));
  switch (type) {
    case 'init':
    case 'state':
    case 'tick':
      if (currentState && data && data.orders) {
        data.orders.forEach(function(newOrder) {
          var oldOrder = currentState.orders.find(function(o) { return o.id === newOrder.id; });
          if (oldOrder && oldOrder.feasibilityResult) {
            newOrder.feasibilityResult = oldOrder.feasibilityResult;
            // 确保显示用计数存在 (directSatisfactions数组→directCount数字)
            if (newOrder.feasibilityResult.directCount === undefined) {
              newOrder.feasibilityResult.directCount = (newOrder.feasibilityResult.directSatisfactions ? newOrder.feasibilityResult.directSatisfactions.length : 0) || 0;
            }
            if (newOrder.feasibilityResult.operationCount === undefined) {
              newOrder.feasibilityResult.operationCount = (newOrder.feasibilityResult.operationsNeeded ? newOrder.feasibilityResult.operationsNeeded.length : 0) || 0;
            }
          }
        });
      }
      currentState = data;
      scheduleUI();
      break;
    case 'orderExecuted':
      if (data.success) {
        log('订单执行成功！', 'success');
        if (data.operations && data.operations.length > 0) {
          data.operations.forEach(op => {
            logOp(op);
          });
        }
        if (renderer && data.transferOccurred === true) {
          const gs = currentState.globalState;
          if (gs.currentMaster !== null && gs.currentSlave !== null) {
            renderer.triggerTransfer(gs.currentMaster, gs.currentSlave);
          }
        }
      } else {
        log(`订单执行失败: ${data.reason}`, 'fail');
        if (data.operations && data.operations.length > 0) {
          data.operations.forEach(op => {
            logOp(op);
          });
        }
      }
      break;
    case 'feasibilityResult':
      {
        const orderId = data && data.orderId;
        const result = data && data.result;
        if (orderId != null && currentState && currentState.orders) {
          const idx = currentState.orders.findIndex(o => o.id === orderId);
          if (idx >= 0) {
            currentState.orders[idx].feasibilityResult = result;
            scheduleUI();
          }
        }
        showFeasibilityResult(result);
      }
      break;
    case 'autoCheckResults':
      if (data && data.length > 0) {
        data.forEach(function(item) {
          if (currentState && currentState.orders) {
            const o = currentState.orders.find(x => x.id === item.orderId);
            if (o) o.feasibilityResult = item.result;
          }
        });
      }
      scheduleUI();
      break;
    case 'workshopSwitched':
      animateWorkshopSwitch(data.fromIndex, data.toIndex, data.operations);
      // Log switch operations
      log('工作台切换: ' + data.caseType, 'info');
      if (data.operations && data.operations.length > 0) {
        data.operations.forEach(function(op) {
          log('  ' + op.action + ': ' + op.description, 'info');
        });
      }
      break;
    case 'autoCheckChanged':
      autoCheckEnabled = data.enabled;
      document.getElementById('btn-auto-check').textContent = autoCheckEnabled ? '⏹ 停止自检' : '🔍 自动检测';
      break;
    case 'world':
      // S4：后端下发的世界元数据（形状/液体词表）——前端不再自带副本
      worldMeta = {
        shapes: (data && data.shapes) || [],
        liquids: (data && data.liquids) || []
      };
      break;
    case 'error':
      log(`错误: ${data.message || data}`, 'fail');
      break;
  }
}

function updateUI() {
  // S4b-2（卡顿修复）：切换动画期间不重建 DOM —— 否则动画引用的卡片会被 innerHTML 换掉，
  // 变成"孤儿节点"，回调仍在野节点上跑（表现为明显卡顿/闪断）。动画结束会补一次同步。
  if (switchAnimating) return;
  if (!currentState) return;
  renderGlobalState();
  renderWorkshops();
  renderOrders();
  renderWarehouse();
  renderProducers();
  if (renderer) {
    renderer.setData(currentState.workshops, currentState.warehouse, currentState.producerStatus);
  }
}

function renderGlobalState() {
  const gs = currentState.globalState;
  const md = document.getElementById('master-display');
  const sd = document.getElementById('slave-display');
  const sb = document.getElementById('status-bar');
  md.innerHTML = gs.currentMaster !== null ? `主工作台: <strong class="set">${gs.masterName}</strong>` : `主工作台: <strong class="unset">未选择</strong>`;
  sd.innerHTML = gs.currentSlave !== null ? `从工作台: <strong class="set">${gs.slaveName}</strong>` : `从工作台: <strong class="unset">未选择</strong>`;
  if (gs.currentMaster === null) { sb.textContent = '请选择主工作台（接收仓库流入）'; sb.style.color = '#ffd700'; }
  else if (gs.currentSlave === null) { sb.textContent = '请选择从工作台'; sb.style.color = '#ffd700'; }
  else { sb.textContent = `就绪: ${gs.masterName} → ${gs.slaveName}`; sb.style.color = '#00d9ff'; }
}

function renderWorkshops() {
  const c = document.getElementById('workshops-list');
  const gs = currentState.globalState;
  c.innerHTML = currentState.workshops.map((w, i) => {
    const isM = i === gs.currentMaster;
    const isS = i === gs.currentSlave;
    const rc = isM ? 'master' : isS ? 'slave' : '';
    const rt = isM ? '【主】' : isS ? '【从】' : '';
    const isWineBanned = w.ban.includes('酒') || w.ban.includes('禁酒');
    const isHoneyBanned = w.ban.includes('蜜') || w.ban.includes('禁蜜');
    return `<div class="workshop-card ${rc}" data-index="${i}">
      <div class="workshop-header">
        <span class="workshop-name">${w.name} ${rt}</span>
        <div class="workshop-capacity">
          ${i === editingCapacityIndex && capacityDraft
            ? `<span style="font-size:0.75em;color:#888;">📦 <input type="number" value="${capacityDraft.jars}" oninput="updateCapacityDraft('jars', this.value)" style="width:40px;background:#1a1a2e;border:1px solid #0f3460;color:#fff;padding:1px 3px;border-radius:2px;"> | 🍷 <input type="number" value="${capacityDraft.liquids}" oninput="updateCapacityDraft('liquids', this.value)" style="width:40px;background:#1a1a2e;border:1px solid #0f3460;color:#fff;padding:1px 3px;border-radius:2px;"> <button style="background:#0f3460;color:#fff;border:none;padding:0 6px;border-radius:2px;cursor:pointer;" onclick="event.stopPropagation();editCapacity(${i})">✔</button></span>`
            : `<span style="font-size:0.75em;color:#888;cursor:pointer" onclick="event.stopPropagation();editCapacity(${i})">
            📦 ${w.jars.length}/${w.maxJars||20} | 🍷${(w.liquids && w.liquids['酒'])||0}/${w.maxLiquids||30}
          </span>`}
        </div>
        <div class="role-buttons">
          ${!isM && !isS ? `<button class="btn-set-master" onclick="event.stopPropagation();setMaster(${i})">设为主</button>` : ''}
          ${!isS && !isM ? `<button class="btn-set-slave" onclick="event.stopPropagation();setSlave(${i})">设为从</button>` : ''}
        </div>
      </div>
      <div class="workshop-marks">
        ${w.baseMarks.map(m => `<span class="mark-base">${m}</span>`).join('')}
        ${w.additionalMarks.map(m => `<span class="mark-additional">${m}</span>`).join('')}
      </div>
      ${w.ban.length > 0 ? `<div class="workshop-ban">🚫 ${w.ban.join(', ')}</div>` : ''}
      <div class="workshop-jars">
        ${w.jars.map(j => `<div class="jar-item ${j.liquid ? (j.liquid === '酒' ? 'jar-full-wine' : 'jar-full-honey') : 'jar-empty'} ${j.isMarked ? 'jar-marked' : ''}" title="${j.description}">${j.shape[0]}</div>`).join('')}
      </div>
      <div class="workshop-liquids">
        <span class="${isWineBanned ? 'liquid-banned' : 'liquid-wine'}">🍷 ${(w.liquids && w.liquids['酒']) || 0}</span>
        <span class="${isHoneyBanned ? 'liquid-banned' : 'liquid-honey'}">🍯 ${(w.liquids && w.liquids['蜜']) || 0}</span>
      </div>
    </div>`;
  }).join('');
}

function renderOrders() {
  const c = document.getElementById('orders-list');
  const gs = currentState.globalState;
  c.innerHTML = currentState.orders.map((order, i) => {
    const canExec = gs.currentMaster !== null && (order.type === 'A' || gs.currentSlave !== null);
    let feasibilityHtml = '';
    if (order.feasibilityResult) {
      const fr = order.feasibilityResult;
      if (fr.feasible) {
        feasibilityHtml = `<div class="feasibility-tag pass">✅ 可行 (直接${(fr.directCount != null ? fr.directCount : (fr.directSatisfactions ? fr.directSatisfactions.length : 0)) || 0}个，操作${(fr.operationCount != null ? fr.operationCount : (fr.operationsNeeded ? fr.operationsNeeded.length : 0)) || 0}个)</div>`;
      } else {
        feasibilityHtml = `<div class="feasibility-tag fail">❌ 不可行: ${fr.blockage || fr.reason || "未知原因"}</div>`;
      }
    }
    const statusClass = order.status === '待执行' ? 'pending' : order.status === '成功' ? 'success' : 'failed';
    const feasibleClass = order.feasibilityResult && order.feasibilityResult.feasible ? ' feasible' : '';
    const hasPath = order.feasibilityResult && order.feasibilityResult.solverPath && order.feasibilityResult.solverPath.length > 0;
    return `<div class="order-card ${statusClass}${feasibleClass}">
      <div style="display:flex;justify-content:space-between;align-items:flex-start;">
        <div><span class="order-id">#${order.id}</span> <span class="order-type">${order.type}类</span></div>
        <button onclick="event.stopPropagation();deleteOrder(${i})" style="background:none;border:none;color:#ff4444;cursor:pointer;font-size:1.1em;padding:0 2px;" title="删除订单">✕</button>
      </div>
      <div class="order-reqs">
        ${order.requirements.map(r => r.liquid ? `<span class="req-item req-full">🍶${r.shape}+${r.liquid}</span>` : `<span class="req-item">🏺${r.shape}</span>`).join("")}
      </div>
      ${feasibilityHtml}
      <div style="margin-top:6px;font-size:0.8em;color:#aaa;">状态: ${order.status}</div>
      <div style="display:flex;gap:4px;margin-top:8px;">
        ${order.status === "待执行" ? `<button onclick="event.stopPropagation();quickCheckFeasibility(${i})" style="flex:1;background:#0f3460;border:none;color:#fff;padding:6px;border-radius:4px;font-size:0.8em;cursor:pointer;">🔍 可行性分析</button>` : ""}
        ${order.status === "待执行" && canExec ? `<button onclick="event.stopPropagation();quickExecute(${i})" style="flex:1;background:#e94560;border:none;color:#fff;padding:6px;border-radius:4px;font-size:0.8em;cursor:pointer;">⚡ 执行</button>` : order.status === "待执行" ? `<button disabled style="flex:1;background:#555;border:none;color:#888;padding:6px;border-radius:4px;font-size:0.8em;">先选主从</button>` : ""}
        ${hasPath ? `<button onclick="event.stopPropagation();showPathModal(${i})" style="flex:0.6;background:#533483;border:none;color:#fff;padding:6px;border-radius:4px;font-size:0.8em;cursor:pointer;">📋 路径</button>` : ""}
      </div>
    </div>`;
  }).join('');
}
function renderWarehouse() {
  if (!currentState || !currentState.warehouse) return;
  document.getElementById('warehouse-jars').textContent = currentState.warehouse.jars || 0;
  document.getElementById('warehouse-wine').textContent = (currentState.warehouse.liquids && currentState.warehouse.liquids['酒']) || 0;
  document.getElementById('warehouse-honey').textContent = (currentState.warehouse.liquids && currentState.warehouse.liquids['蜜']) || 0;
}

function renderProducers() {
  const c = document.getElementById('producer-controls');
  if (!currentState.producerStatus || !currentState.producerStatus.producers) {
    c.innerHTML = '<span style="color:#888;font-size:0.8em;">无生产者数据</span>';
    return;
  }
  c.innerHTML = currentState.producerStatus.producers.map(p => {
    return Object.entries(p.rates).map(([shape, rate]) => `
      <div class="producer-row">
        <span style="width:60px;">${p.name}</span>
        <span style="width:40px;">${shape}</span>
        <input type="number" step="0.1" min="0" max="5" value="${rate}" id="rate-${p.name}-${shape}">
        <button onclick="updateRate('${p.name}', '${shape}')">更新</button>
      </div>
    `).join('');
  }).join('');
}

function setMaster(index) { send('setMaster', { index }); }
function setSlave(index) { send('setSlave', { index }); }
function selectOrder(id) { selectedOrderId = id; }

function showCreateOrder() {
  document.getElementById('order-requirements').innerHTML = '';
  addRequirement();
  openModal('modal-order');
}

function addRequirement() {
  const c = document.getElementById('order-requirements');
  if (c.children.length >= 10) { alert('订单需求数量不能超过 10 个'); return; }
  const d = document.createElement('div');
  d.className = 'req-row';
  d.innerHTML = `<select class="req-shape">${worldMeta.shapes.map(s => `<option value="${s}">${s}</option>`).join('')}</select><select class="req-liquid"><option value="">空罐</option>${worldMeta.liquids.map(l => `<option value="${l}">${l}</option>`).join('')}</select><button onclick="this.parentElement.remove()">✕</button>`;
  c.appendChild(d);
}

function submitOrder() {
  const reqs = []; let hasFull = false;
  document.querySelectorAll('.req-row').forEach(row => {
    const shape = row.querySelector('.req-shape').value;
    const liquid = row.querySelector('.req-liquid').value;
    if (liquid) { hasFull = true; reqs.push({ shape, liquid }); }
    else { if (hasFull) { alert('需求列表约定空罐在前（执行顺序为满罐优先）'); return; } reqs.push({ shape }); }
  });
  send('createOrder', { requirements: reqs, orderType: document.getElementById('order-type').value });
  closeModal('modal-order');
}

function showCreateWorkshop() {
  document.getElementById('ws-base-marks').innerHTML = worldMeta.shapes.map(s => `<div class="shape-option" onclick="toggleSelect(this,'base')" data-shape="${s}">${s}</div>`).join('');
  document.getElementById('ws-add-marks').innerHTML = worldMeta.shapes.map(s => `<div class="shape-option" onclick="toggleSelect(this,'add')" data-shape="${s}">${s}</div>`).join('');
  document.getElementById('ws-ban').innerHTML = worldMeta.liquids.map(l => `<div class="liquid-option" onclick="toggleSelect(this,'ban')" data-liquid="${l}">${l}</div>`).join('');
  openModal('modal-workshop');
}

function toggleSelect(el, type) {
  if (type === 'base') {
    const sel = document.querySelectorAll('#ws-base-marks .selected');
    if (el.classList.contains('selected')) el.classList.remove('selected');
    else if (sel.length < 2) el.classList.add('selected');
  } else {
    el.classList.toggle('selected');
  }
}

function submitWorkshop() {
  const name = document.getElementById('ws-name').value || '新工作台';
  const baseMarks = Array.from(document.querySelectorAll('#ws-base-marks .selected')).map(el => el.dataset.shape);
  const additionalMarks = Array.from(document.querySelectorAll('#ws-add-marks .selected')).map(el => el.dataset.shape);
  const ban = Array.from(document.querySelectorAll('#ws-ban .selected')).map(el => el.dataset.liquid);
  if (baseMarks.length === 0) { alert('至少1个基础标记'); return; }
  send('createWorkshop', { name, baseMarks, additionalMarks, ban });
  closeModal('modal-workshop');
}

function showExecuteModal(i) {
  const order = currentState.orders[i];
  selectedOrderId = order.id;
  document.getElementById('execute-order-id').textContent = order.id;
  document.getElementById('execute-order-info').innerHTML = `<p>类型: ${order.type}类</p><p>需求: ${order.requirements.map(r => r.liquid ? `满罐(${r.shape},${r.liquid})` : `空罐(${r.shape})`).join(', ')}</p>`;
  document.getElementById('feasibility-result').innerHTML = '';
  openModal('modal-execute');
}

function checkFeasibility() {
  send('checkFeasibility', { orderId: selectedOrderId });
}

function showFeasibilityResult(result) {
  const c = document.getElementById('feasibility-result');
  if (result.feasible) {
    c.innerHTML = `<div class="feasibility-success">✓ 可行！${result.directSatisfactions?.length > 0 ? `(${result.directSatisfactions.length}个直接满足，${result.operationsNeeded?.length || 0}个需要操作)` : ''}</div>`;
  } else {
    c.innerHTML = `<div class="feasibility-fail">✗ 不可行: ${result.blockage || result.reason || '未知原因'}</div>`;
  }
}

function submitExecute() {
  closeModal('modal-execute');
  send('executeOrder', { orderId: selectedOrderId });
}

function toggleAutoCheck() {
  autoCheckEnabled = !autoCheckEnabled;
  send('setAutoCheck', { enabled: autoCheckEnabled });
  document.getElementById('btn-auto-check').textContent = autoCheckEnabled ? '⏹ 停止自检' : '🔍 自动检测';
  log(autoCheckEnabled ? '自动可行性检测已开启' : '自动可行性检测已关闭', 'info');
}

function editCapacity(index) {
  const wsc = currentState && currentState.workshops[index];
  if (!wsc) return;
  // 已处于编辑态 → 提交
  if (editingCapacityIndex === index && capacityDraft) {
    const nj = parseInt(capacityDraft.jars) || wsc.maxJars || 20;
    const nl = parseInt(capacityDraft.liquids) || wsc.maxLiquids || 30;
    send('updateWorkshopCapacity', { index, maxJars: nj, maxLiquids: nl });
    log('更新 ' + wsc.name + ' 容量: 罐=' + nj + ' 液=' + nl, 'info');
    editingCapacityIndex = null;
    capacityDraft = null;
    return;
  }
  // 进入编辑态：记录草稿并重绘（重绘后的表单会在后续 state 广播中保留）
  editingCapacityIndex = index;
  capacityDraft = { jars: String(wsc.maxJars || 20), liquids: String(wsc.maxLiquids || 30) };
  renderWorkshops();
}

function updateCapacityDraft(key, value) {
  if (capacityDraft) capacityDraft[key] = value;
}


function showPathModal(i) {
  const order = currentState.orders[i];
  if (!order || !order.feasibilityResult) {
    alert('无可用的求解器路径');
    return;
  }
  const fr = order.feasibilityResult;
  const masterPath = fr.masterPath || [];
  const slavePath = fr.solverPath || [];
  if (masterPath.length === 0 && slavePath.length === 0) {
    alert('无可用的求解器路径');
    return;
  }
  let html = '<div style="max-height:70vh;overflow-y:auto;padding:10px;">';
  html += '<h3 style="margin:0 0 12px 0;color:#00d9ff;">订单 #' + order.id + ' 可行性路径</h3>';
  html += '<p style="color:#888;font-size:0.85em;margin:0 0 12px 0;">类型: ' + order.type + '类 | 需求: ' + order.requirements.map(function(r) { return r.liquid ? '满罐(' + r.shape + ',' + r.liquid + ')' : '空罐(' + r.shape + ')'; }).join(', ') + '</p>';

  if ((fr.transferJarIds && fr.transferJarIds.length > 0) || fr.transferLiquids) {
    var jarStr = fr.transferJarIds && fr.transferJarIds.length > 0 ? '#' + fr.transferJarIds.join(', #') : '无';
    var liqStr = fr.transferLiquids
      ? Object.entries(fr.transferLiquids).filter(function(kv) { return kv[1] > 0; }).map(function(kv) { return kv[0] + '×' + kv[1]; }).join(', ')
      : '';
    html += '<div style="background:#102a43;border:1px solid #0f3460;border-radius:6px;padding:8px 10px;margin-bottom:10px;font-size:0.85em;color:#9be8ff;">';
    html += '📦 选择性转移清单：罐子 ' + jarStr + (liqStr ? ' ；液体：' + liqStr : '') + '（其余资源留在主台）</div>';
  }

  html += '<table style="width:100%;border-collapse:collapse;font-size:0.85em;">';
  html += '<thead><tr style="background:#0f3460;color:#fff;"><th style="padding:6px;text-align:left;">阶段</th><th style="padding:6px;text-align:left;">步骤</th><th style="padding:6px;text-align:left;">需求</th><th style="padding:6px;text-align:left;">操作</th><th style="padding:6px;text-align:left;">结果罐子</th></tr></thead><tbody>';
  function rows(path, phaseLabel) {
    path.forEach(function(p, idx) {
    var opText = (typeof p.operation === 'string') ? p.operation : (p.operation ? p.operation.desc || JSON.stringify(p.operation) : '-');
    var jarsAfter = (p.jarsAfter && p.jarsAfter.length > 0) ? p.jarsAfter.join(', ') : '-';
    html += '<tr style="border-bottom:1px solid #1a1a2e;">';
    html += '<td style="padding:6px;color:' + (phaseLabel === '主台' ? '#e94560' : '#00d9ff') + ';">' + phaseLabel + '</td>';
    html += '<td style="padding:6px;color:#00d9ff;">' + (idx+1) + '</td>';
    html += '<td style="padding:6px;">' + (p.requirement || '-') + '</td>';
    html += '<td style="padding:6px;color:#e94560;">' + opText + '</td>';
    html += '<td style="padding:6px;color:#888;font-size:0.8em;">' + jarsAfter + '</td>';
    html += '</tr>';
    });
  }
  rows(masterPath, '主台');
  if (masterPath.length > 0 && slavePath.length > 0) {
    html += '<tr style="background:#16213e;"><td colspan="5" style="padding:6px;color:#ffd700;text-align:center;">▼ 选择性转移 → 从台 ▼</td></tr>';
  }
  rows(slavePath, '从台');
  html += '</tbody></table></div>';

  // Use the existing modal system
  var modal = document.getElementById('modal-path');
  if (!modal) {
    // Create path modal if it doesn't exist
    modal = document.createElement('div');
    modal.id = 'modal-path';
    modal.className = 'modal';
    modal.innerHTML = '<div class="modal-content" style="max-width:760px;"><div id="path-modal-body"></div><button class="modal-close" onclick="closeModal(\'modal-path\')">关闭</button></div>';
    document.body.appendChild(modal);
  }
  document.getElementById('path-modal-body').innerHTML = html;
  openModal('modal-path');
}

function quickExecute(i) {
  selectedOrderId = currentState.orders[i].id;
  closeModal('modal-execute');
  send('executeOrder', { orderId: currentState.orders[i].id });
  log('执行订单 #' + (currentState.orders[i]?.id || i), 'info');
}

function quickCheckFeasibility(i) {
  selectedOrderId = currentState.orders[i].id;
  send('checkFeasibility', { orderId: currentState.orders[i].id });
}

function deleteOrder(i) {
  if (confirm('确定删除订单 #' + (currentState.orders[i]?.id || i) + '?')) {
    send('deleteOrder', { orderId: currentState.orders[i].id });
    log('删除订单订单', 'info');
  }
}

function manualTick() { send('manualTick'); log('手动推进', 'info'); }

let autoTicking = false;
function toggleAuto() {
  autoTicking = !autoTicking;
  send(autoTicking ? 'startTick' : 'stopTick');
  document.getElementById('btn-auto').textContent = autoTicking ? '▶ 自动推进' : '⏸ 暂停推进';
  log(autoTicking ? '自动推进启动' : '自动推进暂停', 'info');
}

function createRandomOrder() { send('createRandomOrder'); log('生成随机订单', 'info'); }
function createRandomWorkshop() { send('createRandomWorkshop'); log('生成随机工作台', 'info'); }
function resetSystem() { if (confirm('确定重置？')) { send('reset'); log('系统重置', 'info'); } }

function updateRate(name, shape) {
  const input = document.getElementById(`rate-${name}-${shape}`);
  if (!input) return;
  send('updateProducerRate', { producerName: name, shape, rate: parseFloat(input.value) });
  log(`更新 ${name} 的 ${shape} 速率: ${input.value}`, 'info');
}

function openModal(id) { document.getElementById(id).classList.add('active'); }
function closeModal(id) { document.getElementById(id).classList.remove('active'); }

function log(message, type = 'info') {
  const c = document.getElementById('event-log');
  const e = document.createElement('div');
  e.className = `log-entry log-${type}`;
  e.textContent = `[${new Date().toLocaleTimeString()}] ${message}`;
  c.appendChild(e);
  c.scrollTop = c.scrollHeight;
}

// Helper: format an operation log entry for display
function logOp(op) {
  if (op.description) {
    log(`  ➤ ${op.description}`, 'info');
  } else if (op.action) {
    var loc = op.phase || op.location || '';
    var tag = loc ? loc + ': ' : '';
    var jarStr = op.jar ? ' - ' + op.jar : '';
    var liqStr = op.liquid ? ' → ' + op.liquid : '';
    log(`  ➤ ${tag}${op.action}${jarStr}${liqStr}`, 'info');
  } else {
    log(`  ➤ ${JSON.stringify(op)}`, 'info');
  }
}

// ============================================================
// Workshop switch animation
// ============================================================
function animateWorkshopSwitch(fromIdx, toIdx, operations) {
  if (!renderer) return;

  var ops = operations || [];

  // S4b-2（卡顿修复）：
  //  ① 动画期间暂停整块重绘（见 updateUI 的守卫），并在动画结束后补一次同步；
  //  ② 圆点数量设上限 —— 原实现按 operation 数量线性开 setTimeout，操作多时成倍放大。
  switchAnimating = true;
  setTimeout(function() { switchAnimating = false; updateUI(); }, Math.min(ops.length * 150 + 600, 1500));

  var oldCard = document.querySelector('.workshop-card[data-index="' + fromIdx + '"]');
  var newCard = document.querySelector('.workshop-card[data-index="' + toIdx + '"]');

  if (oldCard) {
    oldCard.style.transition = 'transform 0.3s, opacity 0.3s';
    oldCard.style.transform = 'scale(0.95)';
    oldCard.style.opacity = '0.6';
    setTimeout(function() {
      oldCard.style.transform = '';
      oldCard.style.opacity = '';
    }, 600);
  }

  if (newCard) {
    newCard.style.transition = 'box-shadow 0.4s, border-color 0.4s';
    newCard.style.boxShadow = '0 0 20px rgba(0,217,255,0.6)';
    newCard.style.borderColor = '#00d9ff';

    var delay = 0;
    ops.slice(0, MAX_SWITCH_DOTS).forEach(function(op, i) {
      setTimeout(function() {
        if (!newCard) return;
        newCard.style.position = 'relative';
        var dot = document.createElement('span');
        dot.textContent = '\u25CF';
        dot.style.cssText = 'position:absolute;color:#00d9ff;font-size:0.6em;pointer-events:none;transition:all 0.8s ease-out;z-index:100;';
        newCard.appendChild(dot);
        dot.style.left = (10 + Math.random() * 60) + '%';
        dot.style.top = '50%';
        dot.style.opacity = '1';
        setTimeout(function() {
          dot.style.top = (10 + Math.random() * 40) + '%';
          dot.style.opacity = '0';
          dot.style.transform = 'scale(2)';
          setTimeout(function() { if (dot.parentNode) dot.remove(); }, 800);
        }, 50);
      }, delay);
      delay += 150;
    });

    setTimeout(function() {
      newCard.style.boxShadow = '';
      newCard.style.borderColor = '';
    }, Math.min(ops.length, MAX_SWITCH_DOTS) * 150 + 1000);
  }

  if (renderer && typeof renderer.triggerTransfer === 'function') {
    renderer.triggerTransfer(fromIdx, toIdx);
  }
}

document.addEventListener('DOMContentLoaded', () => {
  renderer = new CanvasRenderer('main-canvas');
});

// 建立连接（脚本在 body 末尾，DOM 已就绪）；断线由 onclose 自动重连
connect();





