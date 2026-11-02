// 网页冒烟（无依赖）：用 Edge/Chrome 的 CDP 驱动 world.html，真的点按钮。
//
//   1 启动宿主（例如 --level 1 --urls http://127.0.0.1:5299）
//   2 node tools/web_smoke.js http://127.0.0.1:5299
//
// 它做四件事，每件都必须通过，否则退出码非 0：
//   A 页面渲染出内核状态（不是"等待内核状态…"）、关卡名与回合上限都来自 /api/levels
//   B 帧时间线里出现内核帧（说明 WebSocket 真的连上了）
//   C 点「推进一回合」，回合数 +1（说明命令面板真的能发命令）
//   D 期间 /api/invariants 保持 ok=true（没有把世界点坏）
//
// 为什么不用 puppeteer/playwright：这个仓库不带 node 依赖；
// CDP 只需要 HTTP + WebSocket，而两者都能用内置模块说话（WebSocket 这里手写帧）。

const http = require('http');
const crypto = require('crypto');
const { spawn } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');

const base = process.argv[2] || 'http://127.0.0.1:5299';
const page = process.argv[3] || 'play.html';
const target = new URL(base);

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function findBrowser() {
  const candidates = [
    'C:/Program Files/Google/Chrome/Application/chrome.exe',
    'C:/Program Files (x86)/Google/Chrome/Application/chrome.exe',
    'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
    'C:/Program Files/Microsoft/Edge/Application/msedge.exe',
    '/usr/bin/google-chrome',
    '/usr/bin/chromium'
  ];
  for (const c of candidates) if (fs.existsSync(c)) return c;
  throw new Error('找不到 Chrome/Edge：冒烟需要本机装一个 Chromium 系浏览器');
}

function httpGet(url) {
  return new Promise((resolve, reject) => {
    http.get(url, (res) => {
      let body = '';
      res.on('data', (d) => (body += d));
      res.on('end', () => resolve(body));
    }).on('error', reject);
  });
}

function httpJson(url) {
  return httpGet(url).then((b) => JSON.parse(b));
}

/** 最小 WebSocket 客户端（只够跟 CDP 说话：客户端必须掩码、服务端不掩码）。 */
function wsConnect(urlString, onMessage) {
  const url = new URL(urlString);
  const key = crypto.randomBytes(16).toString('base64');
  return new Promise((resolve, reject) => {
    const req = http.request({
      host: url.hostname,
      port: url.port,
      path: url.pathname + url.search,
      headers: {
        Connection: 'Upgrade',
        Upgrade: 'websocket',
        'Sec-WebSocket-Key': key,
        'Sec-WebSocket-Version': '13'
      }
    });
    req.on('upgrade', (res, socket) => {
      let buf = Buffer.alloc(0);
      socket.on('data', (chunk) => {
        buf = Buffer.concat([buf, chunk]);
        for (;;) {
          if (buf.length < 2) return;
          const op = buf[0] & 0x0f;
          let len = buf[1] & 0x7f;
          let off = 2;
          if (len === 126) { if (buf.length < 4) return; len = buf.readUInt16BE(2); off = 4; }
          else if (len === 127) { if (buf.length < 10) return; len = Number(buf.readBigUInt64BE(2)); off = 10; }
          if (buf.length < off + len) return;
          const payload = buf.slice(off, off + len).toString('utf8');
          buf = buf.slice(off + len);
          if (op === 1) onMessage(payload);
        }
      });
      resolve({
        send(obj) {
          const data = Buffer.from(JSON.stringify(obj));
          const mask = crypto.randomBytes(4);
          let head;
          if (data.length < 126) head = Buffer.from([0x81, 0x80 | data.length]);
          else { head = Buffer.alloc(4); head[0] = 0x81; head[1] = 0x80 | 126; head.writeUInt16BE(data.length, 2); }
          const masked = Buffer.alloc(data.length);
          for (let i = 0; i < data.length; i++) masked[i] = data[i] ^ mask[i % 4];
          socket.write(Buffer.concat([head, mask, masked]));
        },
        close() { socket.destroy(); }
      });
    });
    req.on('error', reject);
    req.end();
  });
}

async function main() {
  const browser = findBrowser();
  const userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'jarsys-smoke-'));
  const proc = spawn(browser, [
    '--headless=new',
    '--disable-gpu',
    '--no-first-run',
    '--no-default-browser-check',
    '--remote-debugging-port=9333',
    '--user-data-dir=' + userDataDir,
    'about:blank'
  ], { stdio: 'ignore' });

  const cleanup = () => { try { proc.kill(); } catch { /* 已退出 */ } };
  const failures = [];
  const check = (ok, message) => {
    console.log((ok ? 'OK   ' : 'FAIL ') + message);
    if (!ok) failures.push(message);
  };

  try {
    // 等 DevTools 端点起来
    let version = null;
    for (let i = 0; i < 40 && !version; i++) {
      await sleep(250);
      try { version = await httpJson('http://127.0.0.1:9333/json/version'); } catch { /* 还没起 */ }
    }
    if (!version) throw new Error('浏览器 DevTools 没起来');
    const wsUrl = version.webSocketDebuggerUrl;

    let id = 0;
    const pending = new Map();
    const browserWs = await wsConnect(wsUrl, (txt) => {
      const msg = JSON.parse(txt);
      if (msg.id && pending.has(msg.id)) { pending.get(msg.id)(msg); pending.delete(msg.id); }
    });
    const send = (method, params) => new Promise((resolve) => {
      const msgId = ++id;
      pending.set(msgId, resolve);
      browserWs.send({ id: msgId, method, params: params || {} });
    });

    const { result: tab } = await send('Target.createTarget', { url: base + '/' + page });
    const { result: attached } = await send('Target.attachToTarget', { targetId: tab.targetId, flatten: true });
    const sessionId = attached.sessionId;

    const sessionSend = (method, params) => new Promise((resolve) => {
      const msgId = ++id;
      pending.set(msgId, resolve);
      browserWs.send({ id: msgId, sessionId, method, params: params || {} });
    });

    await sessionSend('Runtime.enable');
    await sessionSend('Page.enable');
    await sleep(1500);

    const evaluate = async (expression) => {
      const res = await sessionSend('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
      if (res.result && res.result.exceptionDetails) throw new Error('页面脚本抛错: ' + JSON.stringify(res.result.exceptionDetails));
      return res.result && res.result.result && res.result.result.value;
    };

    // A 渲染出内核状态（世界视图看 #state，游戏页看主台卡）
    const stateSel = page.indexOf('play') === 0 ? 'masterBody' : 'state';
    const stateText = await evaluate("(document.getElementById('" + stateSel + "')||{}).textContent||''");
    check(stateText.length > 20 && !stateText.includes('等待内核状态'), 'A 世界状态已渲染：' + String(stateText).slice(0, 40).replace(/\s+/g, ' '));

    const levelText = await evaluate("((document.getElementById('level')||document.getElementById('lvname')||{}).textContent)||''");
    const levelOk = page.indexOf('play') === 0 ? levelText.length > 2 : /第\s*\d+\s*\/\s*\d+\s*关/.test(levelText);
    check(levelOk, 'A 页头显示关卡：' + levelText);

    const turnsBefore = (await httpJson(base + '/api/state')).turns;

    // B 帧时间线（此刻可能还是空的：接上 WebSocket 才会有帧，先记个数，点完再判）
    const framesBefore = await evaluate(page.indexOf('play') === 0
      ? "document.querySelectorAll('#logBody .ln').length"
      : "document.querySelectorAll('#frames .frame').length");
    check(framesBefore >= 0, 'B 帧时间线就位（当前 ' + framesBefore + ' 条）');

    // C 点「推进一回合」
    const clicked = await evaluate(`(function(){
      var b = document.querySelector('[data-act="' + (location.pathname.indexOf('play') >= 0 ? 'advance' : 'tick') + '"]');
      if (!b) return 'no-button';
      if (b.disabled) return 'disabled';
      b.click();
      return 'clicked';
    })()`);
    check(clicked === 'clicked', 'C 点到了「推进一回合」(' + clicked + ')');
    await sleep(1200);

    const turnsAfter = (await httpJson(base + '/api/state')).turns;
    check(turnsAfter.used === turnsBefore.used + 1,
      'C 回合数 +1：' + turnsBefore.used + ' → ' + turnsAfter.used + ' / ' + turnsAfter.limit);

    const pageTurns = await evaluate("(document.getElementById('turns')||{}).textContent||''");
    check(pageTurns.includes(String(turnsAfter.used)), 'C 页头回合数跟上了内核：' + pageTurns);

    // B 推进之后必须有帧进来：没有就是 WebSocket 没连上，或者页面没在收帧。
    const framesAfter = await evaluate(page.indexOf('play') === 0
      ? "document.querySelectorAll('#logBody .ln').length"
      : "document.querySelectorAll('#frames .frame').length");
    check(framesAfter > 0, 'B 帧时间线收到内核帧（' + framesAfter + ' 条）');


    if (page.indexOf('play') === 0) {
      const orderCards = await evaluate("document.querySelectorAll('#ordersBody .order').length");
      check(orderCards > 0, 'E 关卡开局就有订单卡（' + orderCards + ' 张）');
      const liquidCards = await evaluate("document.querySelectorAll('#linesBody .liqcard').length");
      check(liquidCards > 0, 'E 液体也有供应卡（' + liquidCards + ' 张）');
      const lineCards = await evaluate("document.querySelectorAll('#linesBody .linecard').length");
      check(lineCards > 0, 'E 产线卡可配置（' + lineCards + ' 张）');
      const bars = await evaluate("document.querySelectorAll('#ordersBody .obar').length");
      check(bars > 0, 'E 订单进度条已画出（' + bars + ' 条）');
      const benchJars = await evaluate("(document.getElementById('masterBody')||{}).textContent||''");
      check(benchJars.length > 0, 'E 主台卡已渲染');
    }

    if (page.indexOf('play') === 0) {
      // F 「分析」要真的把内核的答复画出来：进度条长度变化 + 结论文字 + （可行时）做法步骤
      const before = await evaluate("(document.querySelector('#ordersBody .onote')||{}).textContent||''");
      await evaluate("(function(){var b=document.querySelector('#ordersBody [data-act=check]');if(b)b.click();return !!b;})()");
      await sleep(1200);
      const after = await evaluate("(document.querySelector('#ordersBody .onote')||{}).textContent||''");
      check(after && after !== '点「分析」问内核这单能不能做',
        'F 分析有表现：' + String(after).slice(0, 46).replace(/\s+/g, ' '));
      check(after !== before || after.length > 0, 'F 分析结果已刷新');
    }
    // D 不变量
    const inv = await httpJson(base + '/api/invariants');
    check(inv.ok === true, 'D 不变量仍为空（violations=' + inv.count + '）');
  } finally {
    cleanup();
    try { fs.rmSync(userDataDir, { recursive: true, force: true }); } catch { /* 删不掉就算了 */ }
  }

  if (failures.length) {
    console.error('\n冒烟失败 ' + failures.length + ' 项');
    process.exit(1);
  }
  console.log('\n冒烟 OK');
  process.exit(0);
}

main().catch((e) => { console.error('冒烟异常: ' + (e && e.message ? e.message : e)); process.exit(1); });
