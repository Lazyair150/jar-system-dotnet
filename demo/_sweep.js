'use strict';
global.window = global;
require('./puzzle_data.js'); require('./puzzle_engine.js');
require('./battle_data.js'); require('./battle_engine.js');
var seed=1; Math.random=function(){seed=(seed*1103515245+12345)&0x7fffffff;return seed/0x7fffffff;};
var ENG=window.ENG,BT=window.BATTLE,BENCHES=window.BENCHES,PRODS=window.PRODS;

function auto(B,P){
  var G=B.G;
  var live=G.orders.filter(function(o){return !o.done;});
  if(!live.length)return;
  var needShapes={},needLiquid={};
  live.forEach(function(o){o.reqs.forEach(function(r){needShapes[r.shape]=true;if(r.liquid)needLiquid[r.liquid]=true;});});
  var hasB=live.some(function(o){return o.cls==='B';});
  if(hasB&&(!B.subBase||B.masterBase.id===B.subBase.id)){
    BT.setBench(B,'sub',BENCHES.filter(function(b){return b.id!==B.masterBase.id;})[0]);
  }
  var bestDef=null,bestScore=-1e9;
  BENCHES.forEach(function(b){
    var d=BT.effectiveDef(b,B.constraints);
    var pool=G.master.jars.map(function(j){return {shape:j.shape,liquid:j.liquid,ref:j,home:d};});
    var liq={};window.LIQUIDS.forEach(function(L){liq[L]=G.master.liquid[L]||0;});
    var score=0;
    live.forEach(function(o){
      var p=pool.slice();
      if(o.cls==='B'&&G.sub){G.sub.jars.forEach(function(j){p.push({shape:j.shape,liquid:j.liquid,ref:j,home:d});});
        window.LIQUIDS.forEach(function(L){liq[L]+=G.sub.liquid[L]||0;});}
      var h=ENG.grow(o,p,d,liq).height;
      score+=(h/o.total)*10+(h>0?P.perHit*o.value:0);
    });
    Object.keys(needShapes).forEach(function(s){if(d.base.indexOf(s)>=0)score+=P.baseBonus;});
    if(b.id===B.masterBase.id)score+=P.sticky;
    if(score>bestScore){bestScore=score;bestDef=b;}
  });
  if(bestDef)BT.setBench(B,'master',bestDef);
  var mBase=B.masterBase.base;
  var jarC=PRODS.filter(function(p){return p.kind==='jar'&&needShapes[p.shape]&&mBase.indexOf(p.shape)>=0;})
    .sort(function(a,b){return b.amount-a.amount;});
  var liqC=PRODS.filter(function(p){return p.kind==='liquid'&&needLiquid[p.liquid]&&(B.masterBase.ban||[]).indexOf(p.liquid)<0;});
  var slots=[];
  for(var i=0;i<jarC.length&&slots.length<P.jarLines;i++)slots.push(jarC[i]);
  for(var j=0;j<liqC.length&&slots.length<3;j++)slots.push(liqC[j]);
  while(slots.length<3&&jarC.length)slots.push(jarC[0]);
  for(var k=0;k<3;k++)G.lines[k]=slots[k]?{prod:slots[k],target:'master'}:null;
}

function run(eid,P){
  var B=BT.newBattle(eid),t=0;
  while(B.status==='playing'&&t<60){auto(B,P);BT.battleTurn(B);t++;}
  return B;
}
var cfgs=[];
[1,2,3].forEach(function(jl){[0,8,20].forEach(function(st){[0,1].forEach(function(ph){
  cfgs.push({jarLines:jl,sticky:st,perHit:ph,baseBonus:ph?3:0});
});});});
console.log('jarLines sticky perHit | 锈蚀 | 贪食');
cfgs.forEach(function(P){
  var out=[];
  ['rust','glut'].forEach(function(id){
    var w=0,hp=0;
    for(var s=0;s<5;s++){seed=1000+s*97;var B=run(id,P);if(B.status==='win')w++;hp+=Math.max(0,B.hp);}
    out.push(w+'/5 敌剩'+(hp/5).toFixed(1));
  });
  console.log('   jl='+P.jarLines+' st='+P.sticky+' ph='+P.perHit+'  | '+out.join(' | '));
});
