/* 产线与流入的只读表现组件。
   它只把 StateDto.producerStatus/warehouse 转成 HTML，不连接、不发命令、不推导世界规则。
   这样“生产实体是什么、页面怎么表达”与 play.js 的交互编排有了明确边界。 */
(function (global) {
  'use strict';

  var RATES = [0, 0.2, 0.4, 0.6];

  function esc(value) {
    return String(value === null || value === undefined ? '' : value)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/"/g, '&quot;');
  }

  function isPreset(rate) {
    return RATES.some(function (preset) { return Math.abs(rate - preset) < 1e-9; });
  }

  function rateButtons(producer, shape, rate) {
    var buttons = RATES.map(function (preset) {
      var on = Math.abs(rate - preset) < 1e-9;
      return '<button type="button" data-act="rate" data-p="' + esc(producer) +
        '" data-s="' + esc(shape) + '" data-r="' + preset + '" class="' +
        (on ? 'on' : '') + '">' + preset + '</button>';
    }).join('');
    if (!isPreset(rate)) {
      buttons = '<span class="ratecur" title="当前速率来自世界配置，不在快捷档位内">当前 ' +
        rate.toFixed(2) + '</span>' + buttons;
    }
    return buttons;
  }

  function renderShapeSources(generator, producers) {
    var sources = producers.filter(function (producer) {
      return Object.prototype.hasOwnProperty.call(producer.rates || {}, generator.shape);
    });
    if (!sources.length) return '<div class="empty">没有生产源，这条线只是零速率占位</div>';

    return sources.map(function (producer) {
      var rate = Number(producer.rates[generator.shape] || 0);
      return '<div class="lsrc"><span class="lsrc-name">' + esc(producer.name) + '</span>' +
        '<span class="lsrc-rate">' + rate.toFixed(2) + ' / 回合</span>' +
        '<div class="rates">' + rateButtons(producer.name, generator.shape, rate) + '</div></div>';
    }).join('');
  }

  function renderShapeCard(generator, producers) {
    var rate = Number(generator.rate || 0);
    var accumulated = Number(generator.accumulated || 0);
    return '<div class="linecard shapecard">' +
      '<div class="lh"><b>' + esc(generator.shape) + '</b>' +
      '<span class="ltotal">合计 ' + rate.toFixed(2) + ' / 回合</span></div>' +
      '<div class="lmeta">累加 ' + accumulated.toFixed(2) + ' · 生产源 ' +
      Number(generator.producers || 0) + ' 个</div>' +
      renderShapeSources(generator, producers) +
      '</div>';
  }

  function renderLiquidCard(liquid, rateValue, accumulatedValue, warehouseStock, flowLimit) {
    var rate = Number(rateValue || 0);
    var accumulated = Number(accumulatedValue || 0);
    var buttons = RATES.map(function (preset) {
      var on = Math.abs(rate - preset) < 1e-9;
      return '<button type="button" data-act="lrate" data-l="' + esc(liquid) +
        '" data-r="' + preset + '" class="' + (on ? 'on' : '') + '">' +
        (preset === 0 ? '停' : preset.toFixed(1)) + '</button>';
    }).join('');
    if (!isPreset(rate)) {
      buttons = '<span class="ratecur" title="当前速率来自世界配置，不在快捷档位内">当前 ' +
        rate.toFixed(2) + '</span>' + buttons;
    }

    return '<div class="linecard liqcard"><div class="lh"><b>' + esc(liquid) + '</b>' +
      '<span class="ltotal">自产 ' + rate.toFixed(2) + ' / 回合</span></div>' +
      '<div class="lmeta">累加 ' + accumulated.toFixed(2) + ' · 仓库库存 ' +
      Number(warehouseStock || 0) + '</div>' +
      '<div class="flowmeta">每次推进最多流入 ' + Number(flowLimit || 0) +
      ' 份（不要求本回合有新产出）</div>' +
      '<div class="rates">' + buttons + '</div></div>';
  }

  function render(producerStatus, warehouse, state) {
    var ps = producerStatus || {};
    var wh = warehouse || {};
    var producers = ps.producers || [];
    var generators = ps.generators || [];
    var slots = ps.slots || { used: 0, limit: 0 };
    var flow = ps.flow || { jars: 0, liquidPerLiquid: 0 };
    var activeLines = generators.filter(function (g) { return Number(g.rate || 0) > 0; }).length;
    var configuredGenerators = generators.filter(function (g) { return Number(g.producers || 0) > 0; });
    var idleGenerators = generators.filter(function (g) { return Number(g.producers || 0) === 0; });

    var shapeCards = configuredGenerators.map(function (generator) {
      return renderShapeCard(generator, producers);
    }).join('');
    var idleSummary = idleGenerators.length
      ? '<div class="idlelines">另有 ' + idleGenerators.length + ' 条形状线没有生产源，不参与每 tick 计算：' +
        idleGenerators.map(function (g) { return esc(g.shape); }).join('、') + '</div>'
      : '';

    var liquidRates = ps.liquidRates || {};
    var liquidAcc = ps.liquidAccumulated || {};
    var warehouseLiquids = wh.liquids || {};
    var liquidCards = Object.keys(liquidRates).map(function (liquid) {
      return renderLiquidCard(liquid, liquidRates[liquid], liquidAcc[liquid],
        warehouseLiquids[liquid], flow.liquidPerLiquid);
    }).join('');

    var slotLimit = Number(slots.limit || 0);
    var flowGuide = '<div class="flowguide" id="flowGuide" role="note">' +
      '<div class="fgtitle">生产 ≠ 流入</div>' +
      '<div><b>生产</b>：形状线把罐子送进中央仓库；液体由世界按自产速率补进仓库。</div>' +
      '<div><b>流入</b>：每次推进从仓库向主台搬，最多 ' + Number(flow.jars || 0) +
      ' 只罐子、每种液体最多 ' + Number(flow.liquidPerLiquid || 0) + ' 份。</div>' +
      '<div class="fgnote">液体自产为 0 仍可能进入主台：搬的是仓库已有库存。' +
      (state && state.phase === 'running' ? ' 执行中仍按世界 tick 推进。' : '') + '</div></div>';

    return {
      html: (shapeCards || '<div class="empty">这个世界没有可生产的形状线</div>') + idleSummary +
        flowGuide +
        (liquidCards ? '<div class="linesep">液体 · 世界自产</div><div class="lines">' +
          liquidCards + '</div>' : ''),
      hint: '形状线 ' + activeLines + ' / ' + generators.length + ' 条有功 · 产能槽 ' +
        Number(slots.used || 0) + ' / ' + (slotLimit > 0 ? slotLimit : '不限') + ' · 流入另计'
    };
  }

  global.JarProductionView = { render: render };
})(window);
