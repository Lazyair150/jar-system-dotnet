var ws = new WebSocket(`ws://${window.location.host}`);
let currentState = null;
let selectedOrderIndex = null;
let autoCheckEnabled = false;

ws.onopen = () => {
  log('系统连接成功', 'info');
  ws.send(JSON.stringify({ type: 'startTick' }));
};

ws.onmessage = (event) => {
  const { type, data } = JSON.parse(event.data);
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
      updateUI();
      break;
    case 'orderExecuted':
      if (data.success) {
        log('订单执行成功！', 'success');
        if (data.operations && data.operations.length > 0) {
          data.operations.forEach(op => {
            if (op.description) {
              log(`  ➤ ${op.description}`, 'info');
            } else if (op.action) {
              log(`  ➤ ${op.location}: ${op.action}${op.jar ? ' - ' + op.jar : ''}`, 'info');
            }
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
            if (op.description) {
              log(`  ➤ ${op.description}`, 'info');
            } else if (op.action) {
              log(`  ➤ ${op.location}: ${op.action}${op.jar ? ' - ' + op.jar : ''}`, 'info');
            }
          });
        }
      }
      break;
    case 'feasibilityResult':
      if (selectedOrderIndex !== null && currentState && currentState.orders && currentState.orders[selectedOrderIndex]) {
        currentState.orders[selectedOrderIndex].feasibilityResult = data;
        updateUI();
      }
      showFeasibilityResult(data);
      break;
    case 'autoCheckResults':
      if (data && data.length > 0) {
        data.forEach(function(item) {
          if (currentState && currentState.orders && currentState.orders[item.index]) {
            currentState.orders[item.index].feasibilityResult = item.result;
          }
        });
      }
      updateUI();
      break;
    case 'autoCheckChanged':
      autoCheckEnabled = data.enabled;
      document.getElementById('btn-auto-check').textContent = autoCheckEnabled ? '⏹ 停止自检' : '🔍 自动检测';
      break;
    case 'error':
      log(`错误: ${data.message || data}`, 'fail');
      break;
  }
};

function updateUI() {
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
    const rc = isM ? 'role-master' : isS ? 'role-slave' : '';
    const rt = isM ? '【主】' : isS ? '【从】' : '';
    const isWineBanned = w.ban.includes('酒') || w.ban.includes('禁酒');
    const isHoneyBanned = w.ban.includes('蜜') || w.ban.includes('禁蜜');
    return `<div class="workshop-card ${rc}" data-index="${i}">
      <div class="workshop-header">
        <span class="workshop-name">${w.name} ${rt}</span>
        <div class="workshop-capacity">
          <span style="font-size:0.75em;color:#888;cursor:pointer" onclick="event.stopPropagation();editCapacity(${i})">
            📦 ${w.jars.length}/${w.maxJars||20} | 🍷${(w.liquids && w.liquids['酒'])||0}/${w.maxLiquids||30}
          </span>
        </div>
        <div class="role-buttons">
          ${!isM ? `<button class="btn-set-master" onclick="event.stopPropagation();setMaster(${i})">设为主</button>` : ''}
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
  const canExec = gs.currentMaster !== null && gs.currentSlave !== null;
  c.innerHTML = currentState.orders.map((order, i) => {
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

function setMaster(index) { ws.send(JSON.stringify({ type: 'setMaster', data: { index } })); }
function setSlave(index) { ws.send(JSON.stringify({ type: 'setSlave', data: { index } })); }
function selectOrder(index) { selectedOrderIndex = index; }

function showCreateOrder() {
  document.getElementById('order-requirements').innerHTML = '';
  addRequirement();
  openModal('modal-order');
}

function addRequirement() {
  const c = document.getElementById('order-requirements');
  const d = document.createElement('div');
  d.className = 'req-row';
  d.innerHTML = `<select class="req-shape">${SHAPES.map(s => `<option value="${s}">${s}</option>`).join('')}</select><select class="req-liquid"><option value="">空罐</option><option value="酒">酒</option><option value="蜜">蜜</option></select><button onclick="this.parentElement.remove()">✕</button>`;
  c.appendChild(d);
}

function submitOrder() {
  const reqs = []; let hasFull = false;
  document.querySelectorAll('.req-row').forEach(row => {
    const shape = row.querySelector('.req-shape').value;
    const liquid = row.querySelector('.req-liquid').value;
    if (liquid) { hasFull = true; reqs.push({ shape, liquid }); }
    else { if (hasFull) { alert('空罐必须在满罐之前！'); return; } reqs.push({ shape }); }
  });
  ws.send(JSON.stringify({ type: 'createOrder', data: { requirements: reqs, orderType: document.getElementById('order-type').value } }));
  closeModal('modal-order');
}

function showCreateWorkshop() {
  document.getElementById('ws-base-marks').innerHTML = SHAPES.map(s => `<div class="shape-option" onclick="toggleSelect(this,'base')" data-shape="${s}">${s}</div>`).join('');
  document.getElementById('ws-add-marks').innerHTML = SHAPES.map(s => `<div class="shape-option" onclick="toggleSelect(this,'add')" data-shape="${s}">${s}</div>`).join('');
  document.getElementById('ws-ban').innerHTML = LIQUIDS.map(l => `<div class="liquid-option" onclick="toggleSelect(this,'ban')" data-liquid="${l}">${l}</div>`).join('');
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
  ws.send(JSON.stringify({ type: 'createWorkshop', data: { name, baseMarks, additionalMarks, ban } }));
  closeModal('modal-workshop');
}

function showExecuteModal(i) {
  selectedOrderIndex = i;
  const order = currentState.orders[i];
  document.getElementById('execute-order-id').textContent = order.id;
  document.getElementById('execute-order-info').innerHTML = `<p>类型: ${order.type}类</p><p>需求: ${order.requirements.map(r => r.liquid ? `满罐(${r.shape},${r.liquid})` : `空罐(${r.shape})`).join(', ')}</p>`;
  document.getElementById('feasibility-result').innerHTML = '';
  openModal('modal-execute');
}

function checkFeasibility() {
  ws.send(JSON.stringify({ type: 'checkFeasibility', data: { orderIndex: selectedOrderIndex } }));
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
  ws.send(JSON.stringify({ type: 'executeOrder', data: { orderIndex: selectedOrderIndex } }));
}

function toggleAutoCheck() {
  autoCheckEnabled = !autoCheckEnabled;
  ws.send(JSON.stringify({ type: 'setAutoCheck', data: { enabled: autoCheckEnabled } }));
  document.getElementById('btn-auto-check').textContent = autoCheckEnabled ? '⏹ 停止自检' : '🔍 自动检测';
  log(autoCheckEnabled ? '自动可行性检测已开启' : '自动可行性检测已关闭', 'info');
}

function editCapacity(index) {
  const wsc = currentState && currentState.workshops[index];
  if (!wsc) return;
  const card = document.querySelector(`.workshop-card[data-index="${index}"]`);
  if (!card) return;
  const capDiv = card.querySelector('.workshop-capacity');
  if (!capDiv) return;
  const inputs = capDiv.querySelectorAll('input');
  if (inputs.length > 0) {
    const nj = parseInt(inputs[0].value) || wsc.maxJars || 20;
    const nl = parseInt(inputs[1].value) || wsc.maxLiquids || 30;
    ws.send(JSON.stringify({ type: 'updateWorkshopCapacity', data: { index, maxJars: nj, maxLiquids: nl } }));
    log('更新 ' + wsc.name + ' 容量: 罐=' + nj + ' 液=' + nl, 'info');
    capDiv.innerHTML = `<span style="font-size:0.75em;color:#888;cursor:pointer;" onclick="event.stopPropagation();editCapacity(${index})">📦 ${wsc.jars.length}/${wsc.maxJars||20} | 🍷${(wsc.liquids && wsc.liquids['酒'])||0}/${wsc.maxLiquids||30}</span>`;
  } else {
    capDiv.innerHTML = `<span style="font-size:0.75em;color:#888;">📦 <input type="number" value="${wsc.maxJars||20}" style="width:40px;background:#1a1a2e;border:1px solid #0f3460;color:#fff;padding:1px 3px;border-radius:2px;"> | 🍷 <input type="number" value="${wsc.maxLiquids||30}" style="width:40px;background:#1a1a2e;border:1px solid #0f3460;color:#fff;padding:1px 3px;border-radius:2px;"> <button style="background:#0f3460;color:#fff;border:none;padding:0 6px;border-radius:2px;cursor:pointer;" onclick="event.stopPropagation();editCapacity(${index})">✔</button></span>`;
  }
}


function showPathModal(i) {
  const order = currentState.orders[i];
  if (!order || !order.feasibilityResult || !order.feasibilityResult.solverPath) {
    alert('无可用的求解器路径');
    return;
  }
  const paths = order.feasibilityResult.solverPath;
  let msg = '订单 #' + order.id + ' 求解器路径:\n';
  paths.forEach(function(p, idx) {
    msg += '\n' + (idx+1) + '. ' + (p.operation || p.desc || JSON.stringify(p));
  });
  alert(msg);
}

function quickExecute(i) {
  selectedOrderIndex = i;
  closeModal('modal-execute');
  ws.send(JSON.stringify({ type: 'executeOrder', data: { orderIndex: i } }));
  log('执行订单 #' + (currentState.orders[i]?.id || i), 'info');
}

function quickCheckFeasibility(i) {
  selectedOrderIndex = i;
  ws.send(JSON.stringify({ type: 'checkFeasibility', data: { orderIndex: i } }));
}

function deleteOrder(i) {
  if (confirm('确定删除订单 #' + (currentState.orders[i]?.id || i) + '?')) {
    ws.send(JSON.stringify({ type: 'deleteOrder', data: { index: i } }));
    log('删除订单订单', 'info');
  }
}

function manualTick() { ws.send(JSON.stringify({ type: 'manualTick' })); log('手动推进', 'info'); }

let autoTicking = false;
function toggleAuto() {
  autoTicking = !autoTicking;
  ws.send(JSON.stringify({ type: autoTicking ? 'startTick' : 'stopTick' }));
  document.getElementById('btn-auto').textContent = autoTicking ? '▶ 自动推进' : '⏸ 暂停推进';
  log(autoTicking ? '自动推进启动' : '自动推进暂停', 'info');
}

function createRandomOrder() { ws.send(JSON.stringify({ type: 'createRandomOrder' })); log('生成随机订单', 'info'); }
function createRandomWorkshop() { ws.send(JSON.stringify({ type: 'createRandomWorkshop' })); log('生成随机工作台', 'info'); }
function resetSystem() { if (confirm('确定重置？')) { ws.send(JSON.stringify({ type: 'reset' })); log('系统重置', 'info'); } }

function updateRate(name, shape) {
  const input = document.getElementById(`rate-${name}-${shape}`);
  if (!input) return;
  ws.send(JSON.stringify({ type: 'updateProducerRate', data: { producerName: name, shape, rate: parseFloat(input.value) } }));
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

document.addEventListener('DOMContentLoaded', () => {
  renderer = new CanvasRenderer('main-canvas');
});





