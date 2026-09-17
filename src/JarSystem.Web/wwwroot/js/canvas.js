class CanvasRenderer {
  constructor(canvasId) {
    this.canvas = document.getElementById(canvasId);
    this.ctx = this.canvas.getContext('2d');
    this.workshops = [];
    this.producerStatus = null;
    this.particles = [];
    this.dirty = true;   // S4b-2：脏标记（数据/尺寸变化才重绘）
    // 表现层修复（2026-09-16）：区分**显示尺寸**与**背板尺寸**。
    //   · 显示尺寸由 CSS 决定（`#main-canvas` 是 `flex: 1`）——我们**只读**它，不再自己写 CSS 尺寸；
    //   · 背板尺寸 = 显示尺寸 × devicePixelRatio，绘制坐标系再按 dpr 缩放回 CSS 像素。
    // 修前把背板直接设成 `父宽-30`，浏览器还要把位图**拉伸**到 flex 算出的显示尺寸 ⇒ 网格与文字发虚。
    this.dpr = 1;
    this.cssW = 1;
    this.cssH = 1;
    this.syncSize();
    window.addEventListener('resize', () => this.resize());
    this.animate();
  }

  /** 把背板对齐到"显示尺寸 × dpr"；尺寸没变时**什么都不做**（否则每次都会清空画布）。 */
  syncSize() {
    // 上限 3：再高的比例（4K@200% 以上）会让背板像素数按比例平方增长，不值得。
    const dpr = Math.max(1, Math.min(window.devicePixelRatio || 1, 3));
    const cssW = Math.max(1, this.canvas.clientWidth);
    const cssH = Math.max(1, this.canvas.clientHeight);
    if (cssW === this.cssW && cssH === this.cssH && dpr === this.dpr) return false;
    this.dpr = dpr;
    this.cssW = cssW;
    this.cssH = cssH;
    this.canvas.width = Math.round(cssW * dpr);
    this.canvas.height = Math.round(cssH * dpr);
    this.dirty = true;
    return true;
  }

  resize() { this.syncSize(); }

  setData(workshops, warehouse, producerStatus) {
    this.workshops = workshops;
    this.warehouse = warehouse;
    this.producerStatus = producerStatus;
    // 布局也可能随数据变化（换行/滚动条）⇒ 顺便对齐背板。**不放在 animate 里**：
    // 读 clientWidth 会强制同步布局，每帧读一次正是 S4b-2 要避免的开销。
    this.syncSize();
    this.dirty = true;   // 数据变了才需要重绘
  }

  // S4b-2（卡顿修复）：原来无条件每帧重绘（网格/仓库/生产者/网络/粒子）。
  // 这里只有"数据变了"或"还有粒子在飞"时才画 —— 稳态下完全不占绘制时间。
  animate() {
    if (this.dirty || this.particles.length > 0) {
      this.render();
      this.dirty = false;
    }
    requestAnimationFrame(() => this.animate());
  }

  render() {
    const ctx = this.ctx, w = this.cssW, h = this.cssH;
    // 绘制坐标系缩放回 CSS 像素 ⇒ 下面所有坐标与字号与修前**逐字相同**，只是背板更密（不糊）。
    ctx.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
    ctx.fillStyle = '#0a0a1a';
    ctx.fillRect(0, 0, w, h);

    ctx.strokeStyle = '#1a1a2e';
    for (let x = 0; x < w; x += 40) { ctx.beginPath(); ctx.moveTo(x,0); ctx.lineTo(x,h); ctx.stroke(); }
    for (let y = 0; y < h; y += 40) { ctx.beginPath(); ctx.moveTo(0,y); ctx.lineTo(w,y); ctx.stroke(); }

    ctx.fillStyle = '#e94560';
    ctx.font = 'bold 14px sans-serif';
    ctx.fillText('🏭 生产监控中心', 15, 25);

    this.drawWarehouse(w - 180, 10);
    if (this.producerStatus) this.drawProducers(15, 40);
    if (this.workshops.length > 0) this.drawNetwork(w, h);
    this.drawParticles();
  }

  drawWarehouse(x, y) {
    const ctx = this.ctx;
    ctx.fillStyle = '#16213e';
    ctx.fillRect(x, y, 160, 70);
    ctx.strokeStyle = '#e94560';
    ctx.strokeRect(x, y, 160, 70);
    ctx.fillStyle = '#fff'; ctx.font = 'bold 11px sans-serif';
    ctx.fillText('📦 中央仓库', x + 10, y + 18);
    ctx.font = '10px sans-serif'; ctx.fillStyle = '#aaa';
    ctx.fillText(`罐子: ${this.warehouse?.jars || 0}`, x + 10, y + 35);
    ctx.fillStyle = '#ff6b6b';
    ctx.fillText(`酒: ${this.warehouse?.liquids?.酒 || 0}`, x + 10, y + 50);
    ctx.fillStyle = '#ffd700';
    ctx.fillText(`蜜: ${this.warehouse?.liquids?.蜜 || 0}`, x + 80, y + 50);
  }

  drawProducers(x, y) {
    const ctx = this.ctx;
    if (!this.producerStatus || !this.producerStatus.generators) return;
    const gens = this.producerStatus.generators.filter(g => g.rate > 0);
    ctx.font = '10px monospace';
    let ly = y;
    gens.forEach(g => {
      const barW = 50;
      const fill = Math.min(g.accumulated, 1) * barW;
      ctx.fillStyle = '#0f3460';
      ctx.fillRect(x + 70, ly - 8, barW, 10);
      ctx.fillStyle = g.accumulated >= 1 ? '#00d9ff' : '#e94560';
      ctx.fillRect(x + 70, ly - 8, fill, 10);
      ctx.fillStyle = '#aaa';
      ctx.fillText(`${g.shape}:${g.rate.toFixed(1)}/t`, x, ly);
      ly += 14;
    });
  }

  drawNetwork(w, h) {
    const ctx = this.ctx;
    const cx = w / 2, cy = h / 2 + 30, r = Math.min(w, h) * 0.32;

    ctx.strokeStyle = '#0f3460'; ctx.lineWidth = 2;
    for (let i = 0; i < this.workshops.length; i++) {
      const a = (i / this.workshops.length) * Math.PI * 2 - Math.PI / 2;
      ctx.beginPath(); ctx.moveTo(cx, cy); ctx.lineTo(cx + Math.cos(a) * r, cy + Math.sin(a) * r); ctx.stroke();
    }

    for (let i = 0; i < this.workshops.length; i++) {
      const ws = this.workshops[i];
      const a = (i / this.workshops.length) * Math.PI * 2 - Math.PI / 2;
      const x = cx + Math.cos(a) * r, y = cy + Math.sin(a) * r;
      this.drawNode(ctx, ws, x, y);
    }

    ctx.beginPath(); ctx.arc(cx, cy, 28, 0, Math.PI * 2);
    ctx.fillStyle = '#16213e'; ctx.fill();
    ctx.strokeStyle = '#e94560'; ctx.lineWidth = 3; ctx.stroke();
    ctx.fillStyle = '#e94560'; ctx.font = 'bold 12px sans-serif'; ctx.textAlign = 'center';
    ctx.fillText('仓库', cx, cy - 2); ctx.fillText('中心', cx, cy + 14); ctx.textAlign = 'left';
  }

  drawNode(ctx, ws, x, y) {
    const rad = 48;
    ctx.beginPath(); ctx.arc(x, y, rad, 0, Math.PI * 2);
    ctx.fillStyle = '#16213e'; ctx.fill();
    let sc = '#0f3460', lw = 2;
    if (ws.isMaster) { sc = '#e94560'; lw = 4; }
    else if (ws.isSlave) { sc = '#00d9ff'; lw = 3; }
    ctx.strokeStyle = sc; ctx.lineWidth = lw; ctx.stroke();

    ctx.fillStyle = '#fff'; ctx.font = 'bold 10px sans-serif'; ctx.textAlign = 'center';
    ctx.fillText(ws.name, x, y - 28);
    if (ws.isMaster) { ctx.fillStyle = '#e94560'; ctx.fillText('【主】', x, y - 16); }
    else if (ws.isSlave) { ctx.fillStyle = '#00d9ff'; ctx.fillText('【从】', x, y - 16); }

    ctx.font = '9px sans-serif'; ctx.fillStyle = '#e94560';
    ctx.fillText(ws.baseMarks.join(','), x, y - 4);
    ctx.fillStyle = '#aaa';
    ctx.fillText(`罐:${ws.jars.length}`, x, y + 8);
    ctx.fillStyle = '#ff6b6b';
    ctx.fillText(`酒:${ws.liquids?.酒 || 0}`, x - 16, y + 22);
    ctx.fillStyle = '#ffd700';
    ctx.fillText(`蜜:${ws.liquids?.蜜 || 0}`, x + 16, y + 22);
    if (ws.ban && ws.ban.length > 0) {
      ctx.fillStyle = '#ff4444'; ctx.font = 'bold 9px sans-serif';
      ctx.fillText(`🚫${ws.ban.join(',')}`, x, y + 36);
    }
    ctx.textAlign = 'left';
  }

  addParticle(x, y, tx, ty) {
    this.particles.push({ x, y, tx, ty, p: 0, s: 0.02 + Math.random() * 0.02 });
    this.dirty = true;
  }

  drawParticles() {
    const ctx = this.ctx;
    for (let i = this.particles.length - 1; i >= 0; i--) {
      const p = this.particles[i];
      p.p += p.s;
      if (p.p >= 1) { this.particles.splice(i, 1); continue; }
      const x = p.x + (p.tx - p.x) * p.p, y = p.y + (p.ty - p.y) * p.p;
      ctx.beginPath(); ctx.arc(x, y, 4, 0, Math.PI * 2);
      ctx.fillStyle = '#4ecdc4'; ctx.fill();
    }
  }

  triggerTransfer(fromIdx, toIdx) {
    // 必须用 **CSS 像素**的布局尺寸（与 render 同一坐标系），不能用背板像素：
    // 否则粒子会按 dpr 倍偏移。
    const w = this.cssW, h = this.cssH;
    const cx = w / 2, cy = h / 2 + 30, r = Math.min(w, h) * 0.32;
    const fa = (fromIdx / this.workshops.length) * Math.PI * 2 - Math.PI / 2;
    const ta = (toIdx / this.workshops.length) * Math.PI * 2 - Math.PI / 2;
    const fx = cx + Math.cos(fa) * r, fy = cy + Math.sin(fa) * r;
    const tx = cx + Math.cos(ta) * r, ty = cy + Math.sin(ta) * r;
    for (let i = 0; i < 5; i++) setTimeout(() => this.addParticle(fx, fy, tx, ty), i * 100);
  }
}

let renderer;
