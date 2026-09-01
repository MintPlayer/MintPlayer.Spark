import{n as s,t as r}from"./chunk-C9yOwMO6.js";import{$ as Ot,$n as wu,$t as j_,B as KM,C as Fy,Dn as p$,Dt as Xa,En as p,F as JM,H as LT,Ht as dy,I as JT,It as __,K as Ly,Kn as vg,Kt as g$,N as Ir,Nt as ZT,P as Iy,Pt as _,Qn as wt$1,Rt as bS,Sn as ne$1,St as V_,T as G_,Ut as e_,V as KT,Vt as cn$1,Wn as vc,Xt as hy,Y as Of,Yn as w3,Z as On$1,_n as mg,ar as xi$1,b as Ff,bn as nE,ct as QT,d as Bf,dn as lv,en as jf,et as Ow,hn as mc,in as kb,ir as xf,it as Pi$1,j as IS,l as B_,ln as ky,nr as xT,on as ki$1,pn as m$,pr as zl,q as OT,qt as gc,r as $T,rr as x_,s as Af,tn as jt,tr as xD,tt as Oy,vt as Ty,w as G,y as FT,yn as my,zt as b_}from"./chunk-Btq1RDbg.js";import{a as X,c as b,l as p$1,n as Mt$1,t as Ht}from"./chunk-CwXwit_b.js";import"./chunk-CgYYSK-z.js";import{C as it$1,E as ms,J as _r,M as st$1,O as ps,X as ee,_t as ln$1,bt as xn$1,c as U,dt as Gt,g as bs,gt as jt$1,ht as jn$1,pt as Tn$1,ut as Bn$1,v as fs,vt as qe,y as ge,yt as sn$1}from"./chunk-DU0Rxd-r.js";import{A as Te$1,C as ie$1,D as Fe,E as Be,M as me,N as S,O as K,P as u,S as De,T as B,a as y$1,c as ae,d as w,f as y,g as _t$1,h as Ct$1,j as V,k as Le,l as ne$2,m as $t,p as b$1,s as Q,u as re,w as le,y as re$1}from"./main-26Q3RTJI.js";import{t as Ie}from"./chunk-CeDkLl86.js";import"./chunk-DknKKvu_.js";import{t as w$1}from"./chunk-mbLuEuxx.js";import{t as ft$1}from"./chunk-Wr-sWe_02.js";function Vi(n,o){if(n&1){let e=$T();Xa(0,`button`,13),vc(`click`,function(){mg(e);return vg(ZT(3).rotateBadgeToken())}),mc(1,`i`,14),Ff(2),Af()}if(n&2){let e=ZT(2);kb(2),jf(` `,e.badgeToken?`Rotate`:`Create`,` badge token `)}}function Oi(n,o){n&1&&(Xa(0,`div`,12),Ff(1,`Private repository — create a badge token to make the badge work in your README.`),Af())}function qi(n,o){if(n&1){let e=$T();Xa(0,`div`,5)(1,`div`,6)(2,`strong`,7),Ff(3,`README badge`),Af(),Xa(4,`button`,8),vc(`click`,function(){mg(e);return vg(ZT(2).copyBadge())}),mc(5,`i`,9),Ff(6,` Copy markdown `),Af(),xT(7,Vi,3,1,`button`,10),Af(),Xa(8,`code`,11),Ff(9),Af(),xT(10,Oi,2,0,`div`,12),Af()}if(n&2){let e=ZT(),t=ZT();kb(7),OT(e.isPrivate?7:-1),kb(2),Oy(t.badgeMarkdown()),kb(),OT(e.isPrivate&&!e.badgeToken?10:-1)}}function Hi(n,o){if(n&1&&(Xa(0,`bs-card`,0)(1,`bs-card-header`),mc(2,`i`,1),Ff(3,` Coverage badge`),Af(),Xa(4,`bs-card-body`)(5,`div`,2)(6,`span`,3),Ff(7),Af(),mc(8,`img`,4),Af(),xT(9,qi,11,3,`div`,5),Af()()),n&2){let e=o,t=ZT();kb(7),jf(`Default branch (`,e.defaultBranch??`unknown`,`):`),kb(),hy(`src`,t.badgeUrl(),lv),kb(),OT(e.canManage?9:-1)}}var et=class n{browse=p(u);owner=Ir.required();name=Ir.required();repo=ne$1(null);badgeUrl=On$1(()=>{let o=this.repo();if(!o)return``;let e=`/badge/${o.owner}/${o.name}.svg`;return o.isPrivate&&o.badgeToken?`${e}?token=${o.badgeToken}`:e});badgeMarkdown=On$1(()=>{let o=this.repo();if(!o)return``;let e=o.baseUrl||location.origin,t=`${e}/badge/${o.owner}/${o.name}.svg`;return`[![Coverage](${o.isPrivate&&o.badgeToken?`${t}?token=${o.badgeToken}`:t})](${e}/r/${o.owner}/${o.name})`});constructor(){zl(async()=>{let o=this.owner(),e=this.name();try{this.repo.set(await this.browse.getRepo(o,e))}catch{this.repo.set(null)}})}async copyBadge(){await navigator.clipboard.writeText(this.badgeMarkdown())}async rotateBadgeToken(){let o=this.repo();if(!o)return;let e=await this.browse.rotateBadgeToken(o.owner,o.name);this.repo.set(s(r({},o),{badgeToken:e.badgeToken}))}static ɵfac=function(e){return new(e||n)};static ɵcmp=xi$1({type:n,selectors:[[`app-repo-badge-panel`]],inputs:{owner:[1,`owner`],name:[1,`name`]},decls:1,vars:1,consts:[[1,`mt-3`,`d-block`],[1,`bi`,`bi-patch-check`],[1,`d-flex`,`align-items-center`,`gap-3`],[1,`text-muted`],[`alt`,`coverage badge`,`height`,`20`,3,`src`],[1,`border`,`rounded`,`p-2`,`mt-3`,`bg-light`],[1,`d-flex`,`align-items-center`,`gap-2`,`mb-1`],[1,`small`],[1,`btn`,`btn-sm`,`btn-outline-secondary`,3,`click`],[1,`bi`,`bi-clipboard`],[1,`btn`,`btn-sm`,`btn-outline-warning`],[1,`small`,`d-block`,`text-break`],[1,`small`,`text-muted`,`mt-1`],[1,`btn`,`btn-sm`,`btn-outline-warning`,3,`click`],[1,`bi`,`bi-arrow-repeat`]],template:function(e,t){if(e&1&&xT(0,Hi,10,3,`bs-card`,0),e&2){let i;OT((i=t.repo())?0:-1,i)}},dependencies:[re,ne$2,ae],encapsulation:2})};var _i=(()=>{class n{static{this.counter=1}constructor(){this.bsFor=Ir(void 0),zl(()=>{let e=this.bsFor();if(e!==void 0&&(this.target=e,this.target instanceof HTMLElement)){if(!this.target.id){let t=n.counter++;this.target.id=`for-target-${t}`}this.forValue=this.target.id}})}static{this.ɵfac=function(t){return new(t||n)}}static{this.ɵdir=Ot({type:n,selectors:[[`label`,`bsFor`,``]],hostVars:3,hostBindings:function(t,i){t&2&&(gc(`for`,i.forValue),Ty(`cursor-default`,!0))},inputs:{bsFor:[1,`bsFor`]}})}}return n})();function Zi(n,o){if(n&1){let e=$T();Xa(0,`div`,9)(1,`label`,10),Ff(2,`Project target (%)`),Af(),Xa(3,`input`,13,5),Fy(`ngModelChange`,function(i){mg(e);let a=ZT();return b_(a.projectTarget,i)||(a.projectTarget=i),vg(i)}),Af(),IS(),Af()}if(n&2){let e=e_(4),t=ZT();hy(`sm`,4),kb(),hy(`bsFor`,e),kb(2),Ly(`ngModel`,t.projectTarget),bS()}}function Gi(n,o){n&1&&(Xa(0,`span`,19),Ff(1,`Saved.`),Af())}function Ki(n,o){if(n&1&&(Xa(0,`span`,20),Ff(1),Af()),n&2){let e=ZT(2);kb(),Oy(e.error())}}function Wi(n,o){if(n&1){let e=$T();Xa(0,`bs-card`,6)(1,`bs-card-header`),mc(2,`i`,7),Ff(3,` Coverage gate`),Af(),Xa(4,`bs-card-body`)(5,`bs-grid`)(6,`bs-form`)(7,`div`,8)(8,`div`,9)(9,`label`,10),Ff(10,`Project comparison`),Af(),Xa(11,`bs-select`,11,0),Fy(`ngModelChange`,function(i){let a=mg(e);return b_(a.projectMode,i)||(a.projectMode=i),vg(i)}),Xa(13,`option`,12),Ff(14,`Ratchet against the base commit`),Af(),Xa(15,`option`,12),Ff(16,`Fixed target`),Af()(),IS(),Af(),xT(17,Zi,5,3,`div`,9),Xa(18,`div`,9)(19,`label`,10),Ff(20,`Allowed drop (points)`),Af(),Xa(21,`input`,13,1),Fy(`ngModelChange`,function(i){let a=mg(e);return b_(a.projectThreshold,i)||(a.projectThreshold=i),vg(i)}),Af(),IS(),Af(),Xa(23,`div`,9)(24,`label`,10),Ff(25,`Partial builds judge`),Af(),Xa(26,`bs-select`,11,2),Fy(`ngModelChange`,function(i){let a=mg(e);return b_(a.projectBasis,i)||(a.projectBasis=i),vg(i)}),Xa(28,`option`,12),Ff(29,`Scoped baseline (like-for-like)`),Af(),Xa(30,`option`,12),Ff(31,`Patched projection (whole workspace)`),Af()(),IS(),Af(),Xa(32,`div`,9)(33,`label`,10),Ff(34,`Patch target (%)`),Af(),Xa(35,`input`,14,3),Fy(`ngModelChange`,function(i){let a=mg(e);return b_(a.patchTarget,i)||(a.patchTarget=i),vg(i)}),Af(),IS(),Af(),Xa(37,`div`,9)(38,`label`,10),Ff(39,`Patch tolerance (points)`),Af(),Xa(40,`input`,13,4),Fy(`ngModelChange`,function(i){let a=mg(e);return b_(a.patchThreshold,i)||(a.patchThreshold=i),vg(i)}),Af(),IS(),Af()(),Xa(42,`bs-checkbox`,15),Fy(`ngModelChange`,function(i){let a=mg(e);return b_(a.blocking,i)||(a.blocking=i),vg(i)}),Ff(43,` Blocking — failed checks turn red. Off, the checks post the same numbers but never fail. `),Af(),IS(),Xa(44,`div`,16)(45,`button`,17),vc(`click`,function(){mg(e);return vg(ZT().save())}),mc(46,`i`,18),Ff(47,` Save gate `),Af(),xT(48,Gi,2,0,`span`,19),xT(49,Ki,2,1,`span`,20),Af(),Xa(50,`div`,21),Ff(51,` A `),Xa(52,`code`),Ff(53,`coverage.yml`),Af(),Ff(54,` in the repository overrides these per field, read from the base branch. `),Af()()()()()}if(n&2){let e=o,t=e_(12),i=e_(22),a=e_(27),r=e_(36),d=e_(41),m=ZT();kb(8),hy(`sm`,4),kb(),hy(`bsFor`,t),kb(2),Ly(`ngModel`,e.projectMode),bS(),kb(2),hy(`ngValue`,`auto`),kb(2),hy(`ngValue`,`fixed`),kb(2),OT(e.projectMode===`fixed`?17:-1),kb(),hy(`sm`,4),kb(),hy(`bsFor`,i),kb(2),Ly(`ngModel`,e.projectThreshold),bS(),kb(2),hy(`sm`,4),kb(),hy(`bsFor`,a),kb(2),Ly(`ngModel`,e.projectBasis),bS(),kb(2),hy(`ngValue`,`scoped`),kb(2),hy(`ngValue`,`projection`),kb(2),hy(`sm`,4),kb(),hy(`bsFor`,r),kb(2),Ly(`ngModel`,e.patchTarget),bS(),kb(2),hy(`sm`,4),kb(),hy(`bsFor`,d),kb(2),Ly(`ngModel`,e.patchThreshold),bS(),kb(2),Ly(`ngModel`,e.blocking),bS(),kb(3),hy(`color`,m.colors.primary)(`disabled`,m.saving()),kb(3),OT(m.savedAt()?48:-1),kb(),OT(m.error()?49:-1)}}var tt=class n{browse=p(u);owner=Ir.required();name=Ir.required();colors=wu;canManage=ne$1(!1);gate=ne$1(null);saving=ne$1(!1);savedAt=ne$1(!1);error=ne$1(null);constructor(){zl(async()=>{let o=this.owner(),e=this.name();try{let t=await this.browse.getRepo(o,e);this.canManage.set(t.canManage),t.canManage&&this.gate.set(await this.browse.getGate(o,e))}catch{this.canManage.set(!1)}})}async save(){let o=this.gate();if(o){this.saving.set(!0),this.savedAt.set(!1),this.error.set(null);try{this.gate.set(await this.browse.putGate(this.owner(),this.name(),s(r({},o),{projectTarget:yi(o.projectTarget),patchTarget:yi(o.patchTarget)}))),this.savedAt.set(!0)}catch(e){this.error.set(e?.error?.error??`Saving failed.`)}finally{this.saving.set(!1)}}}static ɵfac=function(e){return new(e||n)};static ɵcmp=xi$1({type:n,selectors:[[`app-repo-gate-panel`]],inputs:{owner:[1,`owner`],name:[1,`name`]},decls:1,vars:1,consts:[[`projectMode`,``],[`projectThreshold`,``],[`projectBasis`,``],[`patchTarget`,``],[`patchThreshold`,``],[`projectTarget`,``],[1,`mt-3`,`d-block`],[1,`bi`,`bi-shield-check`],[`bsRow`,``,1,`g-3`],[3,`sm`],[`bsColFormLabel`,``,1,`mb-1`,3,`bsFor`],[3,`ngModelChange`,`ngModel`],[3,`ngValue`],[`type`,`number`,`min`,`0`,`max`,`100`,`step`,`0.1`,3,`ngModelChange`,`ngModel`],[`type`,`number`,`min`,`0`,`max`,`100`,`step`,`0.1`,`placeholder`,`off`,3,`ngModelChange`,`ngModel`],[`name`,`gateBlocking`,3,`ngModelChange`,`ngModel`],[1,`d-flex`,`align-items-center`,`gap-2`,`mt-3`],[3,`click`,`color`,`disabled`],[1,`bi`,`bi-save`],[1,`small`,`text-success`],[1,`small`,`text-danger`],[1,`small`,`text-muted`,`mt-2`]],template:function(e,t){if(e&1&&xT(0,Wi,55,25,`bs-card`,6),e&2){let i;OT((i=t.canManage()&&t.gate())?0:-1,i)}},dependencies:[Bn$1,jn$1,Tn$1,qe,ln$1,xn$1,Gt,jt$1,sn$1,re,ne$2,ae,ie$1,ps,fs,ms,_i,w,y,bs,b$1,le],encapsulation:2})};function yi(n){return typeof n==`number`&&Number.isFinite(n)?n:null}var Xi=X(`:host {
  display: block;
  width: 100%;
  aspect-ratio: 16/9;
  --mp-trend-chart-grid-color: var(--bs-border-color, #dee2e6);
  --mp-trend-chart-axis-color: var(--bs-secondary-color, #6c757d);
  --mp-trend-chart-goal-color: var(--bs-warning, #ffc107);
  --mp-trend-chart-tooltip-bg: var(--bs-dark, #212529);
  --mp-trend-chart-tooltip-color: var(--bs-light, #f8f9fa);
  --mp-trend-chart-focus-ring-color: var(--bs-primary, #0d6efd);
  --mp-trend-chart-crosshair-color: var(--bs-secondary-color, #6c757d);
}

* {
  box-sizing: border-box;
}

.chart {
  position: relative;
  width: 100%;
  height: 100%;
}

svg {
  display: block;
  width: 100%;
  height: 100%;
}

.grid line {
  stroke: var(--mp-trend-chart-grid-color);
  stroke-width: 1;
}

.axis text {
  fill: var(--mp-trend-chart-axis-color);
  font-size: 22px; /* logical viewBox units */
}

.axis .y-label {
  text-anchor: end;
  dominant-baseline: middle;
}

.axis .x-label {
  text-anchor: middle;
  dominant-baseline: hanging;
}

.series-line {
  fill: none;
  stroke-width: 4;
  stroke-linejoin: round;
  stroke-linecap: round;
}

.series-area {
  opacity: 0.25;
  stroke: none;
}

.goal-line {
  stroke: var(--mp-trend-chart-goal-color);
  stroke-width: 3;
  stroke-dasharray: 10 8;
}

.goal-label {
  fill: var(--mp-trend-chart-goal-color);
  font-size: 22px;
  text-anchor: end;
}

.crosshair {
  stroke: var(--mp-trend-chart-crosshair-color);
  stroke-width: 1;
  stroke-dasharray: 4 4;
  pointer-events: none;
}

.point {
  fill: transparent;
  stroke: none;
  cursor: pointer;
}

.point:focus {
  outline: none;
}

/* Drawn focus/hover marker: the point becomes a visible dot. */
.point:focus-visible,
.point[data-hovered] {
  fill: currentColor;
}

.point:focus-visible {
  stroke: var(--mp-trend-chart-focus-ring-color);
  stroke-width: 4;
}

.chart-tooltip {
  position: absolute;
  display: none;
  max-width: 60%;
  padding: 0.25rem 0.5rem;
  background: var(--mp-trend-chart-tooltip-bg);
  color: var(--mp-trend-chart-tooltip-color);
  border-radius: 0.25rem;
  font-size: 0.8125rem;
  pointer-events: none;
  z-index: 1;
}

.chart-tooltip[data-visible] {
  display: block;
}

.visually-hidden {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip-path: inset(50%);
  white-space: nowrap;
}`);var it=[`#0d6efd`,`#21b577`,`#fd7e14`,`#6f42c1`,`#d63384`,`#20c997`,`#dc3545`,`#6c757d`];var xe=1e3;var Me=562;var A={top:20,right:20,bottom:48,left:72};var Yi=(()=>{class n extends b{constructor(){super(...arguments),this._series=[],this._area=!0,this._stacked=!1,this._inputLabel=null,this._placed=[],this._focusedKey=null,this._restoreFocus=!1,this._hoveredKey=null}static{this.styles=[Xi]}static get observedAttributes(){return[...super.observedAttributes??[],`area`,`stacked`,`y-min`,`y-max`,`goal`,`goal-label`,`locale`,`summary`,`aria-label`,`input-label`]}get series(){return this._series}set series(e){this._series=Array.isArray(e)?e:[],this.requestUpdate()}get area(){return this._area}set area(e){this._area=!!e,this.requestUpdate()}get stacked(){return this._stacked}set stacked(e){this._stacked=!!e,this.requestUpdate()}get yMin(){return this._yMin}set yMin(e){this._yMin=e==null?void 0:Number(e),this.requestUpdate()}get yMax(){return this._yMax}set yMax(e){this._yMax=e==null?void 0:Number(e),this.requestUpdate()}get goal(){return this._goal}set goal(e){this._goal=e==null?void 0:Number(e),this.requestUpdate()}get goalLabel(){return this._goalLabel}set goalLabel(e){this._goalLabel=e||void 0,this.requestUpdate()}get locale(){return this._locale}set locale(e){this._locale=e||void 0,this.requestUpdate()}get summary(){return this._summary}set summary(e){this._summary=e||void 0,this.requestUpdate()}get inputLabel(){return this._inputLabel}set inputLabel(e){this._inputLabel=e??null,this.requestUpdate()}get summaryFormatter(){return this._summaryFormatter}set summaryFormatter(e){this._summaryFormatter=e,this.requestUpdate()}get tooltipFormatter(){return this._tooltipFormatter}set tooltipFormatter(e){this._tooltipFormatter=e}attributeChangedCallback(e,t,i){switch(super.attributeChangedCallback(e,t,i),e){case`area`:this.area=i!==`false`&&i!==null;break;case`stacked`:this.stacked=i!==null&&i!==`false`;break;case`y-min`:this.yMin=i===null?void 0:Number(i);break;case`y-max`:this.yMax=i===null?void 0:Number(i);break;case`goal`:this.goal=i===null?void 0:Number(i);break;case`goal-label`:this.goalLabel=i??void 0;break;case`locale`:this.locale=i??void 0;break;case`summary`:this.summary=i??void 0;break;case`aria-label`:this.requestUpdate();break;case`input-label`:this._inputLabel=i,this.requestUpdate();break}}static toMs(e){return e instanceof Date?e.getTime():Number(e)}numberFormat(){return new Intl.NumberFormat(this._locale||this.closest(`[lang]`)?.getAttribute(`lang`)||void 0)}dateFormat(){return new Intl.DateTimeFormat(this._locale||this.closest(`[lang]`)?.getAttribute(`lang`)||void 0,{dateStyle:`medium`})}place(){let e=new Map,t=this._series.flatMap((g,D)=>g.points.map((u,E)=>({point:u,pi:E})).filter(u=>u.point.y!==null).map(u=>{let E=n.toMs(u.point.x),U=(this._stacked?e.get(E)??0:0)+u.point.y;return this._stacked&&e.set(E,U),{seriesId:g.id,seriesLabel:g.label,color:g.color??it[D%it.length],point:u.point,xMs:E,yPlot:U,px:0,py:0,seriesIndex:D,pointIndex:u.pi}})),i=t.map(g=>g.xMs),a=t.map(g=>g.yPlot),r=i.length?[Math.min(...i),Math.max(...i)]:[0,1],d=Math.min(...a.length?a:[0],this._goal??Infinity,this._stacked?0:Infinity),m=Math.max(...a.length?a:[1],this._goal??-Infinity),c=Fe(Math.min(d,m),Math.max(d,m)),b=[this._yMin??c[0],this._yMax??c[1]],_=Be(r,[A.left,xe-A.right]),C=Be(b,[Me-A.bottom,A.top]);return t.map(g=>(g.px=_(g.xMs),g.py=C(g.yPlot),g)),{placed:t,xd:r,yd:b}}static runsOf(e){return e.reduce((t,i)=>{let a=t[t.length-1];return a?.length&&i.pointIndex===a[a.length-1].pointIndex+1?[...t.slice(0,-1),[...a,i]]:[...t,[i]]},[])}linePath(e){return n.runsOf(e).map(t=>t.map((i,a)=>`${a===0?`M`:`L`} ${i.px} ${i.py}`).join(` `)).join(` `)}areaPath(e,t){return n.runsOf(e).filter(i=>i.length>1).map(i=>`M ${i[0].px} ${t} `+i.map(a=>`L ${a.px} ${a.py}`).join(` `)+` L ${i[i.length-1].px} ${t} Z`).join(` `)}keyOf(e){return`${e.seriesId}\0${e.pointIndex}`}pointName(e){let t=this.numberFormat(),i=typeof e.point.x==`number`&&e.point.x<1e7?t.format(e.point.x):this.dateFormat().format(new Date(e.xMs));return`${e.seriesLabel}, ${i}, ${e.point.y===null?`—`:t.format(e.point.y)}`}onPointerMove(e){if(!this._placed.length)return;let t=this.shadowRoot?.querySelector(`.chart`)?.getBoundingClientRect();if(!t||t.width===0)return;let i=(e.clientX-t.left)/t.width*xe,a=(e.clientY-t.top)/t.height*Me,r=this._placed.reduce((b,_)=>Math.abs(_.px-i)<Math.abs(b.px-i)?_:b),d=this._placed.filter(b=>b.xMs===r.xMs),m=d.reduce((b,_)=>Math.abs(_.py-a)<Math.abs(b.py-a)?_:b),c=this.shadowRoot?.querySelector(`.chart-tooltip`);c&&(c.textContent=this._tooltipFormatter?.(d)??d.map(b=>this.pointName(b)).join(` · `),c.style.left=`${e.clientX-t.left+12}px`,c.style.top=`${e.clientY-t.top+12}px`,c.setAttribute(`data-visible`,``)),this._hoveredKey!==this.keyOf(m)&&(this._hoveredKey=this.keyOf(m),this.requestUpdate(),this.emit(`trend-point-hover`,{seriesId:m.seriesId,point:m.point}))}clearHover(){this.shadowRoot?.querySelector(`.chart-tooltip`)?.removeAttribute(`data-visible`),this._hoveredKey!==null&&(this._hoveredKey=null,this.requestUpdate(),this.emit(`trend-point-hover`,{seriesId:null,point:null}))}onClick(e){let t=e.composedPath()[0]?.closest?.(`[data-key]`)?.getAttribute(`data-key`),i=this._placed.find(a=>this.keyOf(a)===t);i&&this.emit(`trend-point-select`,{seriesId:i.seriesId,point:i.point})}onKeyDown(e){let t=e.composedPath()[0]?.closest?.(`[data-key]`)?.getAttribute(`data-key`),i=this._placed.find(b=>this.keyOf(b)===t);if(!i)return;let a=this._placed.filter(b=>b.seriesId===i.seriesId).sort((b,_)=>b.xMs-_.xMs),r=a.findIndex(b=>this.keyOf(b)===t),d=[...new Set(this._placed.map(b=>b.seriesId))],m=d.indexOf(i.seriesId),c=b=>{b&&(this._focusedKey=this.keyOf(b),this._restoreFocus=!0,this.requestUpdate())};switch(e.key){case`ArrowRight`:c(a[r+1]);break;case`ArrowLeft`:c(a[r-1]);break;case`Home`:c(a[0]);break;case`End`:c(a[a.length-1]);break;case`ArrowUp`:case`ArrowDown`:{let b=d[(m+(e.key===`ArrowDown`?1:d.length-1))%d.length],_=this._placed.filter(C=>C.seriesId===b);if(!_.length)return;c(_.reduce((C,g)=>Math.abs(g.xMs-i.xMs)<Math.abs(C.xMs-i.xMs)?g:C));break}case`Enter`:case` `:this.emit(`trend-point-select`,{seriesId:i.seriesId,point:i.point});break;default:return}e.preventDefault(),e.stopPropagation()}emit(e,t){this.dispatchEvent(new CustomEvent(e,{detail:t,bubbles:!0,composed:!0}))}updated(e){super.updated(e),this._restoreFocus&&this._focusedKey&&(this._restoreFocus=!1,Array.from(this.shadowRoot?.querySelectorAll(`.point`)??[]).find(t=>t.dataset.key===this._focusedKey)?.focus({preventScroll:!0}))}groupLabel(){return this.getAttribute(`aria-label`)??this._inputLabel}render(){let{placed:e,xd:t,yd:i}=this.place();this._placed=e,this._focusedKey&&e.some(u=>this.keyOf(u)===this._focusedKey)||(this._focusedKey=e.length?this.keyOf(e[0]):null);let a=Be(i,[Me-A.bottom,A.top]),r=Be(t,[A.left,xe-A.right]),d=this.numberFormat(),m=Te$1(i[0],i[1]).filter(u=>u>=i[0]&&u<=i[1]),c=Le(t[0],t[1],6,this._locale),b=this._summaryFormatter?.(this._series)??this._summary,_=this.groupLabel(),C=a(Math.max(i[0],Math.min(i[1],this._stacked?0:i[0]))),g=this._series.map((u,E)=>({series:u,color:u.color??it[E%it.length],points:e.filter(U=>U.seriesId===u.id).sort((U,re)=>U.xMs-re.xMs)})),D=e.find(u=>this.keyOf(u)===this._hoveredKey);return Ht`<div
      class="chart"
      @click=${this.onClick}
      @keydown=${this.onKeyDown}
      @pointermove=${this.onPointerMove}
      @pointerleave=${this.clearHover}
    >
      <svg
        viewBox="0 0 ${xe} ${Me}"
        role="group"
        aria-label=${_??p$1}
        aria-describedby=${b?`trend-summary`:p$1}
      >
        <g class="grid" aria-hidden="true">
          ${ge(m,u=>`y-${u}`,u=>Mt$1`<line x1=${A.left} x2=${xe-A.right} y1=${a(u)} y2=${a(u)}></line>`)}
        </g>
        <g class="axis" aria-hidden="true">
          ${ge(m,u=>`yl-${u}`,u=>Mt$1`<text class="y-label" x=${A.left-10} y=${a(u)}>${d.format(u)}</text>`)}
          ${ge(c,u=>`xl-${u.time}`,u=>Mt$1`<text class="x-label" x=${r(u.time)} y=${Me-A.bottom+12}>${u.label}</text>`)}
        </g>
        ${this._goal!==void 0?Mt$1`<g aria-hidden="true">
              <line class="goal-line" x1=${A.left} x2=${xe-A.right} y1=${a(this._goal)} y2=${a(this._goal)}></line>
              ${this._goalLabel?Mt$1`<text class="goal-label" x=${xe-A.right} y=${a(this._goal)-8}>${this._goalLabel}</text>`:p$1}
            </g>`:p$1}
        ${D?Mt$1`<line class="crosshair" x1=${D.px} x2=${D.px} y1=${A.top} y2=${Me-A.bottom}></line>`:p$1}
        ${ge(g,u=>u.series.id,u=>Mt$1`<g aria-hidden="true">
          ${this._area?Mt$1`<path class="series-area" fill=${u.color} d=${this.areaPath(u.points,C)}></path>`:p$1}
          <path class="series-line" stroke=${u.color} d=${this.linePath(u.points)}></path>
        </g>`)}
        ${ge(e,u=>this.keyOf(u),u=>Mt$1`<circle
          class="point"
          data-key=${this.keyOf(u)}
          ?data-hovered=${this.keyOf(u)===this._hoveredKey}
          cx=${u.px}
          cy=${u.py}
          r="9"
          color=${u.color}
          role="button"
          tabindex=${this.keyOf(u)===this._focusedKey?`0`:`-1`}
          aria-label=${this.pointName(u)}
        ></circle>`)}
      </svg>
      ${b?Ht`<div id="trend-summary" class="visually-hidden">${b}</div>`:p$1}
      <div class="chart-tooltip" aria-hidden="true"></div>
    </div>`}}return n})();typeof customElements<`u`&&!customElements.get(`mp-trend-chart`)&&customElements.define(`mp-trend-chart`,Yi);var Ji=[`chart`];var vi=(()=>{class n{constructor(){this.series=Ir([]),this.area=Ir(!0),this.stacked=Ir(!1),this.yMin=Ir(void 0),this.yMax=Ir(void 0),this.goal=Ir(void 0),this.goalLabel=Ir(void 0),this.locale=Ir(void 0),this.summary=Ir(void 0),this.inputLabel=Ir(void 0),this.summaryFormatter=Ir(void 0),this.pointHover=p$(),this.pointSelect=p$(),this.chartRef=m$(`chart`),zl(()=>{let e=this.chartRef()?.nativeElement;e&&(e.series=this.series())}),zl(()=>{let e=this.chartRef()?.nativeElement;e&&(e.area=this.area())}),zl(()=>{let e=this.chartRef()?.nativeElement;e&&(e.stacked=this.stacked())}),zl(()=>{let e=this.chartRef()?.nativeElement;e&&(e.yMin=this.yMin())}),zl(()=>{let e=this.chartRef()?.nativeElement;e&&(e.yMax=this.yMax())}),zl(()=>{let e=this.chartRef()?.nativeElement;e&&(e.goal=this.goal())}),zl(()=>{let e=this.chartRef()?.nativeElement;e&&(e.goalLabel=this.goalLabel())}),zl(()=>{let e=this.chartRef()?.nativeElement;e&&(e.locale=this.locale())}),zl(()=>{let e=this.chartRef()?.nativeElement;e&&(e.summary=this.summary())}),zl(()=>{let e=this.chartRef()?.nativeElement;e&&(e.inputLabel=this.inputLabel()??null)}),zl(()=>{let e=this.chartRef()?.nativeElement;e&&(e.summaryFormatter=this.summaryFormatter())})}onPointHover(e){this.pointHover.emit(e.detail)}onPointSelect(e){this.pointSelect.emit(e.detail)}static{this.ɵfac=function(t){return new(t||n)}}static{this.ɵcmp=xi$1({type:n,selectors:[[`bs-trend-chart`]],viewQuery:function(t,i){t&1&&Iy(i.chartRef,Ji,5),t&2&&JT()},inputs:{series:[1,`series`],area:[1,`area`],stacked:[1,`stacked`],yMin:[1,`yMin`],yMax:[1,`yMax`],goal:[1,`goal`],goalLabel:[1,`goalLabel`],locale:[1,`locale`],summary:[1,`summary`],inputLabel:[1,`inputLabel`],summaryFormatter:[1,`summaryFormatter`]},outputs:{pointHover:`pointHover`,pointSelect:`pointSelect`},decls:2,vars:0,consts:[[`chart`,``],[`bsForwardAria`,``,1,`bs-trend-chart`,3,`trend-point-hover`,`trend-point-select`]],template:function(t,i){t&1&&(Xa(0,`mp-trend-chart`,1,0),vc(`trend-point-hover`,function(r){return i.onPointHover(r)})(`trend-point-select`,function(r){return i.onPointSelect(r)}),Af())},dependencies:[_r],styles:[`[_nghost-%COMP%]{display:block}`]})}}return n})();function Qi(n,o){if(n&1&&(Xa(0,`bs-card`,0)(1,`bs-card-header`),mc(2,`i`,1),Ff(3,` Coverage over time`),Af(),Xa(4,`bs-card-body`)(5,`div`,2),mc(6,`bs-trend-chart`,3),Af()()()),n&2){let e=ZT();kb(6),hy(`series`,e.trendSeries())(`yMin`,0)(`yMax`,100)(`goal`,80)}}var nt=class n{browse=p(u);owner=Ir.required();name=Ir.required();branch=Ir(``);history=ne$1([]);trendSeries=On$1(()=>{let o=this.history();if(o.length<2)return[];let e=o.every(t=>!!t.timestamp);return[{id:`coverage`,label:`Line coverage %`,points:o.map((t,i)=>({x:e?new Date(t.timestamp):i,y:t.percent}))}]});constructor(){zl(async()=>{let o=this.owner(),e=this.name(),t=this.branch();try{this.history.set(await this.browse.getHistory(o,e,t||void 0))}catch{this.history.set([])}})}static ɵfac=function(e){return new(e||n)};static ɵcmp=xi$1({type:n,selectors:[[`app-repo-trend-panel`]],inputs:{owner:[1,`owner`],name:[1,`name`],branch:[1,`branch`]},decls:1,vars:1,consts:[[1,`mt-3`,`d-block`],[1,`bi`,`bi-graph-up`],[2,`max-width`,`640px`],[`goalLabel`,`80% goal`,`inputLabel`,`Coverage over time`,3,`series`,`yMin`,`yMax`,`goal`]],template:function(e,t){e&1&&xT(0,Qi,7,4,`bs-card`,0),e&2&&OT(t.trendSeries().length>0?0:-1)},dependencies:[re,ne$2,ae,vi],encapsulation:2})};var en=(n,o)=>o.key;function tn(n,o){if(n&1&&(xf(0),Ff(1),Of()),n&2){let e=ZT().$implicit;kb(),Oy(e.label)}}function nn(n,o){if(n&1&&(Xa(0,`p`,6),Ff(1),Af(),mc(2,`bs-code-snippet`,8)),n&2){let e=o;kb(),Oy(e.note),kb(),hy(`code`,e.code)(`language`,e.language)}}function on(n,o){if(n&1&&(Xa(0,`bs-tab-page`),dy(1,tn,2,1,`ng-container`,4),Xa(2,`div`,5),xT(3,nn,3,3),Xa(4,`p`,6),Ff(5),Af(),mc(6,`bs-code-snippet`,7),Af()()),n&2){let e,t=o.$implicit;kb(3),OT((e=t.config)?3:-1,e),kb(2),Oy(t.note),kb(),hy(`code`,t.code)}}var ot=class n{baseUrl=Ir();workflowExamples=On$1(()=>{let o=this.baseUrl()||location.origin,e=(i=``)=>`      - name: Upload coverage
        uses: MintPlayer/CodeCoverage/action@master
        with:
          url: ${o}
          use-oidc: true${i}
          finish: true`,t=i=>`name: CI
on:
  push:
    branches: [main]
  pull_request:

permissions:
  contents: read
  id-token: write   # tokenless upload via OIDC

jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
${i}`;return[{key:`dotnet`,label:`.NET`,note:`Coverlet ships with the xunit/mstest templates; --collect produces a Cobertura report the action auto-detects.`,code:t(`      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 9.0.x
      - run: dotnet test --collect:"XPlat Code Coverage"
${e()}`)},{key:`node`,label:`Node.js`,note:`Jest writes coverage/lcov.info when run with --coverage; lcov is auto-detected.`,code:t(`      - uses: actions/setup-node@v4
        with:
          node-version: 22
      - run: npm ci
      - run: npx jest --coverage
${e()}`)},{key:`angular`,label:`Angular`,note:`ng test --code-coverage emits coverage/<project>/lcov.info via karma-coverage.`,code:t(`      - uses: actions/setup-node@v4
        with:
          node-version: 22
      - run: npm ci
      - run: npx ng test --watch=false --code-coverage --browsers=ChromeHeadless
${e()}`)},{key:`react`,label:`React`,note:`Vitest with the v8 provider writes an lcov report; for CRA/jest use "npm test -- --coverage --watchAll=false" instead.`,code:t(`      - uses: actions/setup-node@v4
        with:
          node-version: 22
      - run: npm ci
      - run: npx vitest run --coverage --coverage.reporter=lcov
${e()}`)},{key:`python`,label:`Python`,note:`pytest-cov with --cov-report=xml produces a Cobertura-style coverage.xml.`,code:t(`      - uses: actions/setup-python@v5
        with:
          python-version: "3.13"
      - run: pip install -r requirements.txt pytest pytest-cov
      - run: pytest --cov --cov-report=xml
${e()}`)},{key:`java`,label:`Java`,note:`The JaCoCo Maven plugin writes target/site/jacoco/jacoco.xml during verify.`,code:t(`      - uses: actions/setup-java@v4
        with:
          distribution: temurin
          java-version: "21"
      - run: mvn -B verify
${e(`
          files: '**/jacoco.xml'`)}`)},{key:`nx`,label:`Nx`,note:`Prefer run-many over "nx affected" for the coverage run: unaffected projects emit no report, so an affected upload reads as a coverage drop for everything untouched. The --coverage flag forwards to vitest/jest through every Nx target shape (no "--" separator) \u2014 including non-JS targets, where it breaks the command (a dotnet test target chokes on it: --exclude those). And run it on the plain test target, not atomized test-ci targets \u2014 those run one spec file each into the same directory and overwrite each other's report.`,config:{note:`Per project, emit lcov into a stable workspace-level folder AND declare that folder as the target's outputs \u2014 otherwise a cache-restored test run produces no report to upload. Vitest needs both lines below (lcov is not a vitest default); Jest projects only need "coverageDirectory" (lcov is a Jest default).`,language:`ts`,code:`// libs/my-lib/vitest.config.ts
export default defineConfig({
  test: {
    coverage: {
      provider: 'v8',
      reporter: ['text', 'lcov'],
      reportsDirectory: '../../coverage/libs/my-lib',
    },
  },
});

// libs/my-lib/project.json \u2014 lets Nx restore reports on cache hits
//   "test": {
//     "outputs": ["{workspaceRoot}/coverage/{projectRoot}"],
//     ...
//   }`},code:t(`      - uses: actions/setup-node@v4
        with:
          node-version: 22
          cache: npm
      - run: npm ci
      - run: npx nx run-many -t test --coverage
${e(`
          files: 'coverage/**/lcov.info'`)}`)}]});static ɵfac=function(e){return new(e||n)};static ɵcmp=xi$1({type:n,selectors:[[`app-repo-setup-panel`]],inputs:{baseUrl:[1,`baseUrl`]},decls:22,vars:1,consts:[[1,`mt-3`,`d-block`],[1,`bi`,`bi-rocket-takeoff`],[1,`text-muted`,`small`],[3,`border`],[4,`bsTabPageHeader`],[1,`p-3`],[1,`small`,`text-muted`],[`language`,`yaml`,3,`code`],[1,`mb-3`,3,`code`,`language`]],template:function(e,t){e&1&&(Xa(0,`bs-card`,0)(1,`bs-card-header`),mc(2,`i`,1),Ff(3,` Set up coverage uploads`),Af(),Xa(4,`bs-card-body`)(5,`p`,2),Ff(6,` Add a workflow like this to `),Xa(7,`code`),Ff(8,`.github/workflows/ci.yml`),Af(),Ff(9,`. Public repositories upload tokenless via OIDC (the `),Xa(10,`code`),Ff(11,`id-token: write`),Af(),Ff(12,` permission); for a private repository, create an upload token on the account page, store it as a repository secret and replace the `),Xa(13,`code`),Ff(14,`use-oidc`),Af(),Ff(15,` line with a `),Xa(16,`code`),Ff(17,`token`),Af(),Ff(18,` input. `),Af(),Xa(19,`bs-tab-control`,3),LT(20,on,7,3,`bs-tab-page`,null,en),Af()()()),e&2&&(kb(19),hy(`border`,!0),kb(),FT(t.workflowExamples()))},dependencies:[re,ne$2,ae,$t,_t$1,Ct$1,Ie],encapsulation:2})};var at=class n{http=p(nE);list(o){let e=new cn$1().set(`account`,o);return Ow(this.http.get(`/api/tokens`,{params:e}))}create(o,e,t){return Ow(this.http.post(`/api/tokens`,{accountLogin:o,description:e,scope:t?`Repository`:`Account`,repositoryFullName:t}))}revoke(o){let e=o.split(`/`).pop();return Ow(this.http.delete(`/api/tokens/${encodeURIComponent(e)}`)).then(()=>{})}static ɵfac=function(e){return new(e||n)};static ɵprov=_({token:n,factory:n.ɵfac,providedIn:`root`})};var an=()=>[];var rn=(n,o)=>o.fullName;var sn=(n,o)=>o.id;function ln(n,o){if(n&1){let e=$T();Xa(0,`bs-alert`,3)(1,`div`,13)(2,`span`),Ff(3,`Token created — copy it now, it won't be shown again:`),Af(),Xa(4,`code`,14),Ff(5),Af(),Xa(6,`button`,15),vc(`click`,function(){mg(e);return vg(ZT(2).copyToken())}),mc(7,`i`,16),Ff(8,` Copy `),Af()()()}if(n&2)hy(`type`,ZT(2).successColor),kb(5),Oy(o.tokenValue)}function cn(n,o){if(n&1&&(Xa(0,`option`,10),Ff(1),Af()),n&2){let e=o.$implicit;hy(`ngValue`,e.fullName),kb(),Oy(e.fullName)}}function dn(n,o){n&1&&(Xa(0,`p`,17),Ff(1,`No upload tokens yet. Create one and pass it to the coverage action as `),Xa(2,`code`),Ff(3,`token`),Af(),Ff(4,`.`),Af())}function hn(n,o){if(n&1&&(mc(0,`i`,25),Ff(1)),n&2){let e=ZT().$implicit;kb(),jf(` `,e.repositoryFullName||`repository`,` `)}}function mn(n,o){if(n&1&&(mc(0,`i`,26),Ff(1)),n&2){let e=ZT().$implicit;kb(),jf(` all of `,e.accountLogin,` `)}}function pn(n,o){n&1&&(Xa(0,`bs-badge`,21),Ff(1,`revoked`),Af())}function un(n,o){n&1&&(Xa(0,`bs-badge`,22),Ff(1,`active`),Af())}function bn(n,o){if(n&1){let e=$T();Xa(0,`button`,27),vc(`click`,function(){mg(e);let i=ZT().$implicit;return vg(ZT(4).revokeToken(i))}),mc(1,`i`,28),Ff(2,` Revoke `),Af()}}function gn(n,o){if(n&1&&(Xa(0,`tr`)(1,`td`),Ff(2),Af(),Xa(3,`td`,19),xT(4,hn,2,1)(5,mn,2,1),Af(),Xa(6,`td`,20),Ff(7),j_(8,`date`),Af(),Xa(9,`td`),xT(10,pn,2,0,`bs-badge`,21)(11,un,2,0,`bs-badge`,22),Af(),Xa(12,`td`,23),xT(13,bn,3,0,`button`,24),Af()()),n&2){let e=o.$implicit;kb(2),Oy(e.description||`—`),kb(2),OT(e.scope===`Repository`?4:5),kb(3),Oy(V_(8,5,e.createdAtUtc,`medium`)),kb(3),OT(e.revokedAtUtc?10:11),kb(3),OT(e.revokedAtUtc?-1:13)}}function fn(n,o){if(n&1&&(Xa(0,`bs-table`,18)(1,`thead`)(2,`tr`)(3,`th`),Ff(4,`Description`),Af(),Xa(5,`th`),Ff(6,`Scope`),Af(),Xa(7,`th`),Ff(8,`Created`),Af(),Xa(9,`th`),Ff(10,`Status`),Af(),mc(11,`th`),Af()(),Xa(12,`tbody`),LT(13,gn,14,8,`tr`,null,sn),Af()()),n&2){let e=ZT();hy(`isResponsive`,!0),kb(13),FT(e)}}function _n(n,o){n&1&&xT(0,dn,5,0,`p`,17)(1,fn,15,1,`bs-table`,18),n&2&&OT(o.length===0?0:1)}function yn(n,o){if(n&1){let e=$T();Xa(0,`bs-card`,0)(1,`bs-card-header`),mc(2,`i`,1),Ff(3,` Upload tokens`),Af(),Xa(4,`div`,2),xT(5,ln,9,2,`bs-alert`,3),Xa(6,`bs-form`,4),vc(`submitted`,function(){mg(e);return vg(ZT().createToken())}),Xa(7,`div`,5)(8,`div`)(9,`label`,6),Ff(10,`Description`),Af(),Xa(11,`input`,7),vc(`ngModelChange`,function(i){mg(e);return vg(ZT().newDescription.set(i))}),Af(),IS(),Af(),Xa(12,`div`)(13,`label`,8),Ff(14,`Scope`),Af(),Xa(15,`bs-select`,9),vc(`ngModelChange`,function(i){mg(e);return vg(ZT().newRepoFullName.set(i))}),Xa(16,`option`,10),Ff(17),Af(),LT(18,cn,2,2,`option`,10,rn),Af(),IS(),Af(),Xa(20,`button`,11),mc(21,`i`,12),Ff(22,` Create token `),Af()()(),xT(23,_n,2,1),Af()()}if(n&2){let e,t,i=ZT();kb(5),OT((e=i.createdToken())?5:-1,e),kb(6),hy(`ngModel`,i.newDescription()),bS(),kb(4),hy(`ngModel`,i.newRepoFullName()),bS(),kb(),hy(`ngValue`,``),kb(),jf(`All repositories of `,i.login()),kb(),FT(i.repos()??x_(7,an)),kb(2),hy(`disabled`,i.creating()),kb(3),OT((t=i.tokens())?23:-1,t)}}var rt=class n{browse=p(u);tokensService=p(at);login=Ir.required();repos=ne$1(null);tokens=ne$1(null);canManage=ne$1(!1);newDescription=ne$1(``);newRepoFullName=ne$1(``);createdToken=ne$1(null);creating=ne$1(!1);successColor=wu.success;constructor(){zl(()=>{let o=this.login();this.repos.set(null),this.tokens.set(null),this.canManage.set(!1),this.createdToken.set(null),o&&this.load(o)})}async load(o){this.browse.getAccountRepos(o).then(e=>this.repos.set(e),()=>this.repos.set([])),await this.loadTokens(o)}async loadTokens(o){try{this.tokens.set(await this.tokensService.list(o)),this.canManage.set(!0)}catch{this.tokens.set(null),this.canManage.set(!1)}}async createToken(){this.creating.set(!0);try{let o=await this.tokensService.create(this.login(),this.newDescription()||null,this.newRepoFullName()||null);this.createdToken.set(o),this.newDescription.set(``),this.newRepoFullName.set(``),await this.loadTokens(this.login())}finally{this.creating.set(!1)}}async copyToken(){let o=this.createdToken();o&&await navigator.clipboard.writeText(o.tokenValue)}async revokeToken(o){await this.tokensService.revoke(o.id),await this.loadTokens(this.login())}static ɵfac=function(e){return new(e||n)};static ɵcmp=xi$1({type:n,selectors:[[`app-account-tokens-panel`]],inputs:{login:[1,`login`]},decls:1,vars:1,consts:[[1,`mt-3`,`d-block`],[1,`bi`,`bi-key`],[1,`p-3`],[1,`d-block`,`mb-3`,3,`type`],[3,`submitted`],[1,`d-flex`,`align-items-end`,`gap-2`,`flex-wrap`,`mb-3`],[`for`,`token-description`,1,`form-label`,`d-block`,`small`,`mb-1`],[`id`,`token-description`,`type`,`text`,`placeholder`,`e.g. CI uploads`,3,`ngModelChange`,`ngModel`],[`for`,`token-scope`,1,`form-label`,`d-block`,`small`,`mb-1`],[`id`,`token-scope`,3,`ngModelChange`,`ngModel`],[3,`ngValue`],[`type`,`submit`,1,`btn`,`btn-sm`,`btn-primary`,3,`disabled`],[1,`bi`,`bi-plus-lg`],[1,`d-flex`,`align-items-center`,`gap-2`,`flex-wrap`],[1,`user-select-all`],[1,`btn`,`btn-sm`,`btn-outline-secondary`,3,`click`],[1,`bi`,`bi-clipboard`],[1,`text-muted`,`small`,`mb-0`],[3,`isResponsive`],[1,`small`],[1,`small`,`text-muted`],[1,`text-bg-secondary`],[1,`text-bg-success`],[1,`text-end`],[1,`btn`,`btn-sm`,`btn-outline-danger`],[1,`bi`,`bi-git`],[1,`bi`,`bi-people`],[1,`btn`,`btn-sm`,`btn-outline-danger`,3,`click`],[1,`bi`,`bi-x-lg`]],template:function(e,t){e&1&&xT(0,yn,24,8,`bs-card`,0),e&2&&OT(t.canManage()?0:-1)},dependencies:[Bn$1,jn$1,Tn$1,qe,xn$1,sn$1,Q,B,re,ne$2,w,y,ie$1,De,re$1,JM],encapsulation:2})};var xi=[`*`];var Ci=(()=>{class n{constructor(){this.ariaLabel=Ir(`breadcrumb`)}static{this.ɵfac=function(t){return new(t||n)}}static{this.ɵcmp=xi$1({type:n,selectors:[[`bs-breadcrumb`]],inputs:{ariaLabel:[1,`ariaLabel`]},ngContentSelectors:xi,decls:3,vars:1,consts:[[1,`breadcrumb`]],template:function(t,i){t&1&&(QT(),ki$1(0,`nav`)(1,`ol`,0),KT(2),Pi$1()()),t&2&&gc(`aria-label`,i.ariaLabel())},styles:[`[_nghost-%COMP%]     .breadcrumb{--%NS%bs-breadcrumb-padding-x: 0;--%NS%bs-breadcrumb-padding-y: 0;--%NS%bs-breadcrumb-margin-bottom: 1rem;--%NS%bs-breadcrumb-bg: ;--%NS%bs-breadcrumb-border-radius: ;--%NS%bs-breadcrumb-divider-color: var(--%NS%bs-secondary-color);--%NS%bs-breadcrumb-item-padding-x: .5rem;--%NS%bs-breadcrumb-item-active-color: var(--%NS%bs-secondary-color);display:flex;flex-wrap:wrap;padding:var(--%NS%bs-breadcrumb-padding-y) var(--%NS%bs-breadcrumb-padding-x);margin-bottom:var(--%NS%bs-breadcrumb-margin-bottom);font-size:var(--%NS%bs-breadcrumb-font-size);list-style:none;background-color:var(--%NS%bs-breadcrumb-bg);border-radius:var(--%NS%bs-breadcrumb-border-radius)}[_nghost-%COMP%]     .breadcrumb-item+.breadcrumb-item{padding-left:var(--%NS%bs-breadcrumb-item-padding-x)}[_nghost-%COMP%]     .breadcrumb-item+.breadcrumb-item:before{float:left;padding-right:var(--%NS%bs-breadcrumb-item-padding-x);color:var(--%NS%bs-breadcrumb-divider-color);content:var(--%NS%bs-breadcrumb-divider, "/")}[_nghost-%COMP%]     .breadcrumb-item.active{color:var(--%NS%bs-breadcrumb-item-active-color)}`]})}}return n})();var wi=(()=>{class n{constructor(){this.active=Ir(!1)}static{this.ɵfac=function(t){return new(t||n)}}static{this.ɵcmp=xi$1({type:n,selectors:[[`bs-breadcrumb-item`]],hostAttrs:[`role`,`listitem`],hostVars:5,hostBindings:function(t,i){t&2&&(gc(`aria-current`,i.active()?`page`:null),Ty(`breadcrumb-item`,!0)(`active`,i.active()))},inputs:{active:[1,`active`]},ngContentSelectors:xi,decls:1,vars:0,template:function(t,i){t&1&&(QT(),KT(0))},encapsulation:2})}}return n})();function vt(n){let o=new Map,e=new Map,t=new Map,i=new Map,a=(r,d)=>{o.set(r.id,r),e.set(r.id,d);let m=r.children?.length?r.children.map(_=>a(_,r)).reduce((_,C)=>_+C,0):r.value??0;t.set(r.id,m);let c=(r.children??[]).filter(_=>i.get(_.id)!==void 0),b=c.map(_=>t.get(_.id)??0).reduce((_,C)=>_+C,0);return i.set(r.id,r.colorValue!==void 0?r.colorValue:b>0?c.map(_=>(i.get(_.id)??0)*(t.get(_.id)??0)).reduce((_,C)=>_+C,0)/b:void 0),m};return a(n,null),{root:n,byId:o,parents:e,values:t,colorValues:i}}function _e(n,o){let e=t=>t?[...e(n.parents.get(t.id)),t]:[];return e(o)}function st(n,o){return _e(n,o).length}var Ti=n=>!!(n.children?.length||n.hasChildren);function $i(n,o){return[...o.children??[]].sort((e,t)=>(n.values.get(t.id)??0)-(n.values.get(e.id)??0))}function Te(n,o){return o!==void 0&&n.byId.get(o)||n.root}function Fi(n,o){let e=t=>t.children?.length?1+Math.max(...t.children.map(e)):0;return e(Te(n,o))}function xt(n,o,e={}){let{maxDepth:t=2,minFraction:i=0}=e,a=Te(n,o),r=[],d=(m,c,b,_,C)=>{if(_>t)return;let g=$i(n,m);if(!g.length)return;let D=g.map(u=>n.values.get(u.id)??0).reduce((u,E)=>u+E,0);g.reduce((u,E,U)=>{let re=D>0?(n.values.get(E.id)??0)/D:1/g.length,Q=u+(b-c)*re;return Q-u>=i&&Q>u&&(r.push({node:E,x0:u,x1:Q,depth:_,level:C+1,setsize:g.length,posinset:U+1,hasChildren:Ti(E)}),d(E,u,Q,_+1,C+1)),Q},c)};return d(a,0,1,1,st(n,a)),r}function ki(n,o){let e=n.reduce((d,m)=>d+m,0);if(e===0||o===0)return Infinity;let t=Math.max(...n),i=Math.min(...n),a=e*e,r=o*o;return Math.max(r*t/a,a/(r*i))}function vn(n,o){let e=new Map,t=r({},o),i=n.filter(d=>d.area>0),a=d=>{let m=d.reduce((_,C)=>_+C.area,0),c=t.x1-t.x0,b=t.y1-t.y0;if(!(m<=0||c<=0||b<=0))if(c>=b){let _=m/b;d.reduce((C,g)=>{let D=g.area/_;return e.set(g.id,{x0:t.x0,y0:C,x1:t.x0+_,y1:C+D}),C+D},t.y0),t=s(r({},t),{x0:t.x0+_})}else{let _=m/c;d.reduce((C,g)=>{let D=g.area/_;return e.set(g.id,{x0:C,y0:t.y0,x1:C+D,y1:t.y0+_}),C+D},t.x0),t=s(r({},t),{y0:t.y0+_})}},r$1=[];for(;i.length;){let d=Math.min(t.x1-t.x0,t.y1-t.y0),m=[...r$1,i[0]];!r$1.length||ki(m.map(c=>c.area),d)<=ki(r$1.map(c=>c.area),d)?(r$1=m,i=i.slice(1)):(a(r$1),r$1=[])}return a(r$1),e}function Di(n,o,e={}){let{maxDepth:t=2,minArea:i=0,childPadding:a=0,childHeaderSpace:r$2=0}=e,d=Te(n,o),m=[],c=_=>{let C={x0:_.x0+a,y0:_.y0+a+r$2,x1:_.x1-a,y1:_.y1-a};return C.x1-C.x0>0&&C.y1-C.y0>0?C:null},b=(_,C,g,D)=>{if(g>t)return;let u=g===1?C:c(C);if(!u)return;let E=$i(n,_);if(!E.length)return;let U=(u.x1-u.x0)*(u.y1-u.y0),re=E.map(F=>n.values.get(F.id)??0).reduce((F,Fe)=>F+Fe,0),$e=vn(E.map(F=>({id:F.id,area:re>0?(n.values.get(F.id)??0)/re*U:U/E.length})),u);E.map((F,Fe)=>({kid:F,i:Fe,r:$e.get(F.id)})).filter(F=>!!F.r&&(F.r.x1-F.r.x0)*(F.r.y1-F.r.y0)>=i).map(F=>(m.push(s(r({node:F.kid},F.r),{depth:g,level:D+1,setsize:E.length,posinset:F.i+1,hasChildren:Ti(F.kid)})),b(F.kid,F.r,g+1,D+1),F))};return b(d,{x0:0,y0:0,x1:1,y1:1},1,st(n,d)),m}var xn=2*Math.PI;var Ce=1e-6;var ie=(n,o,e)=>Se(n+o*Math.sin(e));var ne=(n,o,e)=>Se(n-o*Math.cos(e));var Se=n=>Math.round(n*1e3)/1e3;function Cn(n,o,e,t){let i=`M ${ie(n,t,0)} ${ne(o,t,0)} A ${t} ${t} 0 1 1 ${ie(n,t,Math.PI)} ${ne(o,t,Math.PI)} A ${t} ${t} 0 1 1 ${ie(n,t,0)} ${ne(o,t,0)} Z`;if(e<=Ce)return i;return`${i} ${`M ${ie(n,e,0)} ${ne(o,e,0)} A ${e} ${e} 0 1 0 ${ie(n,e,Math.PI)} ${ne(o,e,Math.PI)} A ${e} ${e} 0 1 0 ${ie(n,e,0)} ${ne(o,e,0)} Z`}`}function Ei(n,o,e,t,i,a,r={}){let{padAngle:d=0,ringGap:m=1}=r,c=Math.max(0,e),b=Math.max(c,t-m);if(b-c<=Ce||a-i<=Ce)return``;if(a-i>=xn-Ce)return Cn(n,o,c,b);let _=Math.min((a-i)/2,d)/2,C=_>0?r.padRadius??Math.sqrt(c*c+b*b):0,g=De=>De>Ce?Math.asin(Math.min(1,C*Math.sin(_)/De)):0,D=(i+a)/2,u=(De,St)=>St>De?[De,St]:[D,D+Ce],[E,U]=u(i+g(b),a-g(b)),re=U-E>Math.PI?1:0,Q=`M ${ie(n,b,E)} ${ne(o,b,E)} A ${b} ${b} 0 ${re} 1 ${ie(n,b,U)} ${ne(o,b,U)}`;if(c<=Ce)return`${Q} L ${Se(n)} ${Se(o)} Z`;let[$e,F]=u(i+g(c),a-g(c)),Fe=F-$e>Math.PI?1:0;return`${Q} L ${ie(n,c,F)} ${ne(o,c,F)} A ${c} ${c} 0 ${Fe} 0 ${ie(n,c,$e)} ${ne(o,c,$e)} Z`}function Pi(n,o,e,t=`radial`){let i=(n+o)/2*360%360,a=t===`radial`?i<180?0:180:i>90&&i<270?270:90;return`rotate(${Se(i-90)}) translate(${Se(e)},0) rotate(${a})`}var Li=.6;var wn=1.2;var ft=8;var kn=4;var Mn=`…`;var Ni={visible:!1,text:``,orientation:`tangential`};var Mi=(n,o,e)=>!n.length||o<kn?Ni:o>=n.length?{visible:!0,text:n,orientation:e}:{visible:!0,text:n.slice(0,o-1)+Mn,orientation:e};function Ii(n,o,e,t,i){let a=t-e,r=(e+t)/2;if(a<=0||o<=0||i<=0)return Ni;let d=r*o,m=Li*i,c=wn*i,b=d>=c?Math.floor((a-ft)/m):0,_=2*r*Math.sin(Math.min(o,Math.PI)/2),C=a>=c?Math.floor((Math.min(d,_)-ft)/m):0;return C>=b?Mi(n,C,`tangential`):Mi(n,b,`radial`)}function Ri(n,o,e){return o>=1.4*e&&n-ft>=3*Li*e}function Sn(n,o,e){let t=(n%360+360)%360,i=(1-Math.abs(2*e-1))*o,a=i*(1-Math.abs(t/60%2-1)),r=e-i/2,d=Math.floor(t/60),[m,c,b]=d===0?[i,a,0]:d===1?[a,i,0]:d===2?[0,i,a]:d===3?[0,a,i]:d===4?[a,0,i]:[i,0,a];return[(m+r)*255,(c+r)*255,(b+r)*255]}function _t(n){let o=n.trim().match(/^#([0-9a-f]{3}|[0-9a-f]{6})$/i)?.[1];if(o){let i=o.length===3?[...o].map(a=>a+a).join(``):o;return[0,2,4].map(a=>parseInt(i.slice(a,a+2),16))}let e=n.trim().match(/^rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*(?:,\s*[\d.]+\s*)?\)$/i);if(e)return[Number(e[1]),Number(e[2]),Number(e[3])];let t=n.trim().match(/^hsl\(\s*([\d.]+)\s*,\s*([\d.]+)%\s*,\s*([\d.]+)%\s*\)$/i);if(t)return Sn(Number(t[1]),Number(t[2])/100,Number(t[3])/100);throw new Error(`Unsupported color: "${n}" (use #rrggbb, rgb(r, g, b) or hsl(h, s%, l%))`)}function yt(n){try{return _t(n)}catch{return}}function Si([n,o,e]){let t=n/255,i=o/255,a=e/255,r=Math.max(t,i,a),d=Math.min(t,i,a),m=(r+d)/2;if(r===d)return{h:0,s:0,l:m};let c=r-d,b=m>.5?c/(2-r-d):c/(r+d);return{h:(r===t?(i-a)/c+(i<a?6:0):r===i?(a-t)/c+2:(t-i)/c+4)*60,s:b,l:m}}function Ct(n,o,e,t){let i=Si(_t(e)),a=Si(_t(t)),r=o-n;return d=>{let m=r===0?0:Math.min(1,Math.max(0,(d-n)/r)),c=i.h+(a.h-i.h)*m,b=i.s+(a.s-i.s)*m,_=i.l+(a.l-i.l)*m;return`hsl(${gt(c)}, ${gt(b*100)}%, ${gt(_*100)}%)`}}var gt=n=>Math.round(n*10)/10;function Tn(n){let o=yt(n);if(!o)return;let[e,t,i]=o.map(a=>{let r=a/255;return r<=.03928?r/12.92:Math.pow((r+.055)/1.055,2.4)});return .2126*e+.7152*t+.0722*i}function zi(n,o,e){let t=yt(n),i=yt(o);if(!t||!i)return;let a=Math.min(1,Math.max(0,e)),r=t.map((d,m)=>Math.round(d*a+i[m]*(1-a)));return`rgb(${r[0]}, ${r[1]}, ${r[2]})`}function wt(n){let o=Tn(n);if(o===void 0)return;return(o+.05)/.05>=1.05/(o+.05)?`dark`:`light`}var $n=X(`@charset "UTF-8";
:host {
  /* Flex column so the optional breadcrumb takes its own row; without it the
     chart is the only child and fills the host exactly as before. */
  display: flex;
  flex-direction: column;
  width: 100%;
  aspect-ratio: 1;
  /* Two-level fallbacks: Bootstrap 5.3 reassigns --bs-* per data-bs-theme and
     custom properties inherit through the shadow boundary, so dark mode needs
     no media query here. */
  /* Node labels contrast against the DATA-COLORED surface under them, not the
     page theme \u2014 the element picks one of this pair per node from the fill
     composited over the resolved backdrop. Deliberately NOT --bs-* driven. */
  --mp-hierarchy-chart-label-on-light: #1c1f23;
  --mp-hierarchy-chart-label-on-dark: #f8f9fa;
  --mp-hierarchy-chart-center-color: var(--bs-body-color, #212529);
  --mp-hierarchy-chart-center-bg: var(--bs-body-bg, #ffffff);
  --mp-hierarchy-chart-node-fill: var(--bs-secondary-bg, #e9ecef);
  --mp-hierarchy-chart-gap: var(--bs-body-bg, #ffffff);
  --mp-hierarchy-chart-branch-border: var(--bs-border-color, #dee2e6);
  --mp-hierarchy-chart-tooltip-bg: var(--bs-dark, #212529);
  --mp-hierarchy-chart-tooltip-color: var(--bs-light, #f8f9fa);
  --mp-hierarchy-chart-focus-ring-color: var(--bs-primary, #0d6efd);
}

* {
  box-sizing: border-box;
}

.chart {
  position: relative;
  width: 100%;
  flex: 1 1 auto;
  min-height: 0;
  /* The geometric zoom window: content outside it must clip, not spill. */
  overflow: hidden;
}

.breadcrumb {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  flex: 0 0 auto;
  gap: 0.125rem;
  margin: 0;
  padding: 0 0 0.25rem;
  font-size: 0.875rem;
}

.crumb {
  min-width: 24px;
  min-height: 24px;
  padding: 0.125rem 0.375rem;
  background: transparent;
  border: 0;
  border-radius: 0.25rem;
  color: var(--mp-hierarchy-chart-center-color);
  font: inherit;
  cursor: pointer;
  text-decoration: underline;
}

.crumb-current {
  padding: 0.125rem 0.375rem;
  color: var(--mp-hierarchy-chart-center-color);
  font-weight: 600;
}

.crumb-sep {
  opacity: 0.5;
  user-select: none;
}

/* Only when pinch is enabled: removing pinch-zoom from touch-action is what
   hands two-finger gestures to JS (S4-measured) while one-finger pan stays
   native. Never applied with zoom-gestures="none"/"wheel", so browser page
   pinch-zoom over the chart keeps working there. */
.chart.pinch {
  touch-action: pan-x pan-y;
}

svg {
  display: block;
  width: 100%;
  height: 100%;
}

/* ---------- sunburst ---------- */
.ring {
  stroke: none;
  cursor: pointer;
}

.ring[data-leaf] {
  fill-opacity: 0.6;
  cursor: default;
}

/* font-size comes per-element from the component (device px converted to
   viewBox units against the measured host scale \u2014 labels never scale with
   host size or zoom). */
.arc-label {
  text-anchor: middle;
  dominant-baseline: middle;
  pointer-events: none;
  user-select: none;
}

.arc-label[data-surface=light] {
  fill: var(--mp-hierarchy-chart-label-on-light);
}

.arc-label[data-surface=dark] {
  fill: var(--mp-hierarchy-chart-label-on-dark);
}

/* HTML overlay button in the sunburst hole (a role=tree svg cannot own a button). */
.center-control {
  position: absolute;
  top: 50%;
  left: 50%;
  transform: translate(-50%, -50%);
  border-radius: 50%;
  min-width: 24px;
  min-height: 24px;
  overflow: hidden;
  padding: 0.25rem;
  background: var(--mp-hierarchy-chart-center-bg);
  color: var(--mp-hierarchy-chart-center-color);
  border: 1px solid var(--mp-hierarchy-chart-branch-border);
  font: inherit;
  font-size: 0.875rem;
  cursor: pointer;
}

/* At the root there is nothing to zoom out to, and a disabled button still
   swallows clicks \u2014 which would make the hole a dead zone over the chart. */
.center-control:disabled {
  cursor: default;
  pointer-events: none;
}

/* ---------- icicle / treemap (positioned divs) ---------- */
.cell {
  position: absolute;
  overflow: hidden;
  background: var(--mp-hierarchy-chart-node-fill);
  border: 1px solid var(--mp-hierarchy-chart-gap);
  border-radius: 2px;
  /* font-size is set inline by the component (constant device px). */
  line-height: 1.25;
  padding: 1px 4px;
  cursor: pointer;
  transition: left var(--mp-hierarchy-chart-transition-duration, 300ms) ease-out, top var(--mp-hierarchy-chart-transition-duration, 300ms) ease-out, width var(--mp-hierarchy-chart-transition-duration, 300ms) ease-out, height var(--mp-hierarchy-chart-transition-duration, 300ms) ease-out;
}

.icicle,
.treemap,
.treemap-body {
  position: relative;
  width: 100%;
  height: 100%;
}

.treemap-header {
  height: 1.75rem;
  padding: 0.25rem 0.5rem;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
  color: var(--mp-hierarchy-chart-center-color);
  background: var(--mp-hierarchy-chart-center-bg);
  border: 1px solid var(--mp-hierarchy-chart-branch-border);
  border-radius: 2px;
  font-size: 0.8125rem;
  cursor: pointer;
}

.treemap .treemap-body {
  height: calc(100% - 1.75rem - 2px);
  margin-top: 2px;
}

.chart-tooltip {
  position: absolute;
  display: none;
  max-width: 60%;
  padding: 0.25rem 0.5rem;
  background: var(--mp-hierarchy-chart-tooltip-bg);
  color: var(--mp-hierarchy-chart-tooltip-color);
  border-radius: 0.25rem;
  font-size: 0.8125rem;
  pointer-events: none;
  z-index: 1;
}

.chart-tooltip[data-visible] {
  display: block;
}

/* Transient "how to zoom" overlay shown on an uncaptured plain wheel \u2014 the
   embedded-maps convention. Purely visual: aria-hidden, no pointer events. */
.zoom-hint {
  position: absolute;
  top: 50%;
  left: 50%;
  transform: translate(-50%, -50%);
  max-width: 90%;
  padding: 0.375rem 0.75rem;
  background: var(--mp-hierarchy-chart-tooltip-bg);
  color: var(--mp-hierarchy-chart-tooltip-color);
  border-radius: 0.25rem;
  font-size: 0.875rem;
  text-align: center;
  pointer-events: none;
  user-select: none;
  z-index: 2;
  animation: mp-hierarchy-chart-hint 1.5s ease-out forwards;
}

@keyframes mp-hierarchy-chart-hint {
  0% {
    opacity: 0;
  }
  15% {
    opacity: 1;
  }
  80% {
    opacity: 1;
  }
  100% {
    opacity: 0;
  }
}
.cell[data-leaf] {
  cursor: default;
}

.cell .cell-label {
  display: block;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.cell[data-surface=light] {
  color: var(--mp-hierarchy-chart-label-on-light);
}

.cell[data-surface=dark] {
  color: var(--mp-hierarchy-chart-label-on-dark);
}

/* A treemap branch tile is a frame: children render inside its header inset,
   its own strip shows the label. */
.treemap .cell[data-branch] {
  background: transparent;
  border-color: var(--mp-hierarchy-chart-branch-border);
}

.focus-cell {
  background: var(--mp-hierarchy-chart-center-bg);
  border-color: var(--mp-hierarchy-chart-branch-border);
}

/* Focus indication: a drawn stroke on SVG arcs (outline on SVG is historically
   unreliable), a normal inset outline on HTML cells/buttons. */
.ring:focus {
  outline: none;
}

.ring:focus-visible {
  stroke: var(--mp-hierarchy-chart-focus-ring-color);
  stroke-width: 6px;
  stroke-linejoin: round;
}

.cell:focus-visible {
  outline: 3px solid var(--mp-hierarchy-chart-focus-ring-color);
  outline-offset: -3px;
}

.center-control:focus-visible,
.treemap-header:focus-visible,
.crumb:focus-visible {
  outline: 3px solid var(--mp-hierarchy-chart-focus-ring-color);
  outline-offset: 2px;
}

.ring[data-loading],
.cell[data-loading] {
  animation: mp-hierarchy-chart-pulse 1.2s ease-in-out infinite;
}

.ring[data-load-error],
.cell[data-load-error] {
  opacity: 0.5;
}

@keyframes mp-hierarchy-chart-pulse {
  50% {
    opacity: 0.4;
  }
}
@media (prefers-reduced-motion: reduce) {
  .ring,
  .cell,
  .zoom-hint {
    transition: none !important;
    animation: none !important;
  }
}`);var J=1e3;var kt=2*Math.PI;function Fn(){return typeof matchMedia<`u`&&matchMedia(`(prefers-reduced-motion: reduce)`).matches}var Dn=(()=>{class n extends b{constructor(){super(...arguments),this._layout=`sunburst`,this._maxDepth=void 0,this._minAngle=.2,this._minSize=4,this._showLabels=!0,this._labelFontSize=12,this._backdrop=`#ffffff`,this._hostScale=.42,this._colorMin=0,this._colorMax=100,this._colorStart=`#fe0000`,this._colorEnd=`#21b577`,this._inputLabel=null,this._transitionDuration=300,this._loadingIds=new Set,this._failedIds=new Set,this._loadedIds=new Set,this._loadingLabel=`Loading`,this._fill=Ct(this._colorMin,this._colorMax,this._colorStart,this._colorEnd),this._prevSpans=new Map,this._tween=1,this._tweenFrame=0,this._hoveredId=null,this._focusedId=null,this._restoreFocus=!1,this._rendered=[],this._typeahead=``,this._typeaheadTimer=0,this._zoomOutLabel=`Zoom out one level`,this._metricUnitLabel=`%`,this._valueUnitLabel=``,this.liveAnnouncer=new ee(this,{omitRole:!0}),this._showBreadcrumb=!1,this._breadcrumbLabel=`Chart path`,this._gestures=new Set([`wheel`,`pinch`]),this._hintVisible=!1,this._hintTimer=0,this._wheelListener=e=>this.onWheel(e),this._viewZoom=1,this._viewX=0,this._viewY=0,this._pinchPointers=new Map,this._dragPointer=null,this._dragLast={x:0,y:0},this._dragTotal=0,this._dragMoved=!1,this._dismissedForId=null}static{this.styles=[$n]}static get observedAttributes(){return[...super.observedAttributes??[],`layout`,`root-id`,`max-depth`,`min-angle`,`min-size`,`show-labels`,`label-font-size`,`backdrop`,`color-min`,`color-max`,`color-start`,`color-end`,`transition-duration`,`locale`,`zoom-gestures`,`zoom-hint-label`,`show-breadcrumb`,`breadcrumb-label`,`zoom-out-label`,`metric-unit-label`,`value-unit-label`,`loading-label`,`aria-label`,`input-label`]}get data(){return this._data}set data(e){this._data=e,this._index=e?vt(e):void 0,this.resetZoom()}get layout(){return this._layout}set layout(e){this._layout=e===`icicle`||e===`treemap`?e:`sunburst`,this.resetZoom()}get rootId(){return this._rootId}set rootId(e){let t=e||void 0;t!==this._rootId&&(this.beginTween(),this._rootId=t,t===void 0?this.removeAttribute(`root-id`):this.setAttribute(`root-id`,t),this.requestUpdate())}get maxDepth(){return this._maxDepth??(this._loadChildren?2:`auto`)}set maxDepth(e){this._maxDepth=e===`auto`?`auto`:Math.max(1,Math.floor(Number(e)||2)),this.requestUpdate()}get renderedDepth(){let e=this.maxDepth;return e!==`auto`?e:this._index?Math.max(1,Fi(this._index,this._rootId)):1}get minAngle(){return this._minAngle}set minAngle(e){this._minAngle=Math.max(0,Number(e)||0),this.requestUpdate()}get minSize(){return this._minSize}set minSize(e){this._minSize=Math.max(0,Number(e)||0),this.requestUpdate()}get showLabels(){return this._showLabels}set showLabels(e){this._showLabels=!!e,this.requestUpdate()}get labelFontSize(){return this._labelFontSize}set labelFontSize(e){this._labelFontSize=Math.max(1,Number(e)||12),this.requestUpdate()}get backdrop(){return this._backdropOverride}set backdrop(e){this._backdropOverride=e||void 0,this.requestUpdate()}get colorMin(){return this._colorMin}set colorMin(e){this._colorMin=Number(e)||0,this.rebuildScale()}get colorMax(){return this._colorMax}set colorMax(e){this._colorMax=Number(e)||0,this.rebuildScale()}get colorStart(){return this._colorStart}set colorStart(e){this._colorStart=e||`#fe0000`,this.rebuildScale()}get colorEnd(){return this._colorEnd}set colorEnd(e){this._colorEnd=e||`#21b577`,this.rebuildScale()}get inputLabel(){return this._inputLabel}set inputLabel(e){this._inputLabel=e??null,this.requestUpdate()}get transitionDuration(){return this._transitionDuration}set transitionDuration(e){this._transitionDuration=Math.max(0,Number(e)||0),this.style.setProperty(`--mp-hierarchy-chart-transition-duration`,`${this._transitionDuration}ms`)}get locale(){return this._locale}set locale(e){this._locale=e||void 0,this.requestUpdate()}get tooltipFormatter(){return this._tooltipFormatter}set tooltipFormatter(e){this._tooltipFormatter=e}get labelFormatter(){return this._labelFormatter}set labelFormatter(e){this._labelFormatter=e,this.requestUpdate()}get loadChildren(){return this._loadChildren}set loadChildren(e){this._loadChildren=e,this.requestUpdate()}get loadingLabel(){return this._loadingLabel}set loadingLabel(e){this._loadingLabel=e??`Loading`}rebuildScale(){this._fill=Ct(this._colorMin,this._colorMax,this._colorStart,this._colorEnd),this.requestUpdate()}attributeChangedCallback(e,t,i){switch(super.attributeChangedCallback(e,t,i),e){case`layout`:this.layout=i??`sunburst`;break;case`root-id`:this.rootId=i??void 0;break;case`max-depth`:i===null?(this._maxDepth=void 0,this.requestUpdate()):this.maxDepth=i===`auto`?`auto`:Number(i);break;case`min-angle`:this.minAngle=Number(i??.2);break;case`min-size`:this.minSize=Number(i??4);break;case`show-labels`:this.showLabels=i!==`false`&&i!==null;break;case`label-font-size`:this.labelFontSize=Number(i??12);break;case`backdrop`:this.backdrop=i??void 0;break;case`color-min`:this.colorMin=Number(i??0);break;case`color-max`:this.colorMax=Number(i??100);break;case`color-start`:this.colorStart=i??`#fe0000`;break;case`color-end`:this.colorEnd=i??`#21b577`;break;case`transition-duration`:this.transitionDuration=Number(i??300);break;case`locale`:this.locale=i??void 0;break;case`zoom-gestures`:this.zoomGestures=i??`wheel pinch`;break;case`zoom-hint-label`:this._zoomHintLabel=i??void 0;break;case`show-breadcrumb`:this.showBreadcrumb=i!==`false`&&i!==null;break;case`breadcrumb-label`:this._breadcrumbLabel=i??`Chart path`,this.requestUpdate();break;case`zoom-out-label`:this._zoomOutLabel=i??`Zoom out one level`,this.requestUpdate();break;case`loading-label`:this._loadingLabel=i??`Loading`;break;case`metric-unit-label`:this._metricUnitLabel=i??`%`,this.requestUpdate();break;case`value-unit-label`:this._valueUnitLabel=i??``,this.requestUpdate();break;case`aria-label`:this.requestUpdate();break;case`input-label`:this.inputLabel=i;break}}get showBreadcrumb(){return this._showBreadcrumb}set showBreadcrumb(e){this._showBreadcrumb=!!e,this.requestUpdate()}get breadcrumbLabel(){return this._breadcrumbLabel}set breadcrumbLabel(e){this._breadcrumbLabel=e||`Chart path`,this.requestUpdate()}get zoomOutLabel(){return this._zoomOutLabel}set zoomOutLabel(e){this._zoomOutLabel=e||`Zoom out one level`,this.requestUpdate()}get metricUnitLabel(){return this._metricUnitLabel}set metricUnitLabel(e){this._metricUnitLabel=e??`%`,this.requestUpdate()}get valueUnitLabel(){return this._valueUnitLabel}set valueUnitLabel(e){this._valueUnitLabel=e??``,this.requestUpdate()}get focusedRoot(){return this._index?Te(this._index,this._rootId):void 0}connectedCallback(){super.connectedCallback(),this.addEventListener(`wheel`,this._wheelListener,{passive:!1}),typeof ResizeObserver<`u`&&(this._resizeObserver=new ResizeObserver(e=>{let t=e[e.length-1]?.contentRect.width;if(!t)return;let i=t/J;Math.abs(i-this._hostScale)>.001&&(this._hostScale=i,this.requestUpdate())}),this._resizeObserver.observe(this))}disconnectedCallback(){this.removeEventListener(`wheel`,this._wheelListener),clearTimeout(this._hintTimer),this._resizeObserver?.disconnect(),this._resizeObserver=void 0,super.disconnectedCallback()}willUpdate(e){super.willUpdate(e),this._tween>=1&&this.resolveBackdrop()}resolveBackdrop(){if(this._backdropOverride){this._backdrop=this._backdropOverride;return}if(typeof getComputedStyle!=`function`)return;let e=this;for(;e;){let t=getComputedStyle(e).backgroundColor;if(En(t)){this._backdrop=t;return}let i=e.getRootNode();e=e.parentElement??(i instanceof ShadowRoot?i.host:null)}}surfaceToneOf(e,t){let i=this.fillOf(e),a=i?zi(i,this._backdrop,t):void 0;return this.toneOfSurface(a)}toneOfSurface(e){return((e!==void 0?wt(e):void 0)??wt(this._backdrop)??`dark`)===`dark`?`light`:`dark`}get zoomGestures(){return this._gestures.size?[...this._gestures].join(` `):`none`}set zoomGestures(e){let t=(e??``).toLowerCase().split(/\s+/);this._gestures=new Set(t.filter(i=>i===`wheel`||i===`pinch`))}get zoomHintLabel(){return this._zoomHintLabel!==void 0?this._zoomHintLabel:typeof navigator<`u`&&/Mac|iPhone|iPad/.test(navigator.platform??``)?`Use ⌘ + scroll to zoom the chart`:`Use Ctrl + scroll to zoom the chart`}set zoomHintLabel(e){this._zoomHintLabel=e||void 0}static{this.MAX_ZOOM=32}get zoomLevel(){return this._viewZoom}setZoomLevel(e,t=.5,i=.5){let a=Math.min(n.MAX_ZOOM,Math.max(1,Number(e)||1)),r=this._viewX+t/this._viewZoom,d=this._viewY+i/this._viewZoom;this._viewZoom=a,this._viewX=lt(r-t/a,a),this._viewY=lt(d-i/a,a),this.requestUpdate()}resetZoom(){this._viewZoom=1,this._viewX=0,this._viewY=0,this.requestUpdate()}panBy(e,t){this._viewZoom<=1||(this._viewX=lt(this._viewX-e/this._viewZoom,this._viewZoom),this._viewY=lt(this._viewY-t/this._viewZoom,this._viewZoom),this.requestUpdate())}viewRect(e,t,i,a){let r=this._viewZoom;return{x0:(e-this._viewX)*r,y0:(t-this._viewY)*r,x1:(i-this._viewX)*r,y1:(a-this._viewY)*r}}chartAnchor(e){let t=this.shadowRoot?.querySelector(`.chart`)?.getBoundingClientRect();return!t||!t.width||!t.height?{x:.5,y:.5}:{x:Math.min(1,Math.max(0,(e.clientX-t.left)/t.width)),y:Math.min(1,Math.max(0,(e.clientY-t.top)/t.height))}}get effectiveScale(){return this._hostScale*this._viewZoom}onWheel(e){if(!this._gestures.has(`wheel`)||!this._index)return;if(!e.ctrlKey&&!e.metaKey){this.showZoomHint();return}e.preventDefault();let t=e.deltaMode===1?16:e.deltaMode===2?this.clientHeight||400:1,i=Math.max(-100,Math.min(100,e.deltaY*t)),a=this.chartAnchor(e);this.setZoomLevel(this._viewZoom*Math.exp(-i*.005),a.x,a.y)}pinchDistance(){let[e,t]=[...this._pinchPointers.values()];return Math.hypot(t.x-e.x,t.y-e.y)}pinchMidpoint(){let[e,t]=[...this._pinchPointers.values()];return{x:(e.x+t.x)/2,y:(e.y+t.y)/2}}onPointerDown(e){if(e.pointerType===`touch`){if(!this._gestures.has(`pinch`))return;this._pinchPointers.set(e.pointerId,{x:e.clientX,y:e.clientY});return}this._viewZoom>1&&e.button===0&&(this._dragPointer=e.pointerId,this._dragLast={x:e.clientX,y:e.clientY},this._dragTotal=0,this._dragMoved=!1)}trackViewGesture(e){if(e.pointerType===`touch`&&this._pinchPointers.has(e.pointerId)){if(this._pinchPointers.size!==2)return this._pinchPointers.set(e.pointerId,{x:e.clientX,y:e.clientY}),!1;let t=this.pinchDistance(),i=this.pinchMidpoint();if(this._pinchPointers.set(e.pointerId,{x:e.clientX,y:e.clientY}),t>0){let a=this.pinchMidpoint(),r=this.chartAnchor({clientX:a.x,clientY:a.y});this.setZoomLevel(this._viewZoom*(this.pinchDistance()/t),r.x,r.y);let d=this.shadowRoot?.querySelector(`.chart`)?.getBoundingClientRect();d?.width&&d.height&&this.panBy((a.x-i.x)/d.width,(a.y-i.y)/d.height)}return!0}if(this._dragPointer===e.pointerId){let t=e.clientX-this._dragLast.x,i=e.clientY-this._dragLast.y;this._dragLast={x:e.clientX,y:e.clientY},this._dragTotal+=Math.abs(t)+Math.abs(i),this._dragTotal>3&&(this._dragMoved=!0);let a=this.shadowRoot?.querySelector(`.chart`)?.getBoundingClientRect();return a?.width&&a.height&&this.panBy(t/a.width,i/a.height),!0}return!1}endViewGesture(e){this._pinchPointers.delete(e.pointerId),this._dragPointer===e.pointerId&&(this._dragPointer=null)}showZoomHint(){this._hintVisible=!0,clearTimeout(this._hintTimer),this._hintTimer=setTimeout(()=>{this._hintVisible=!1,this.requestUpdate()},1500),this.requestUpdate()}zoomTo(e){let t=this._index;if(!t)return;let i=Te(t,e);i!==this.focusedRoot&&(this.resetZoom(),this.rootId=i===t.root?void 0:i.id,this.liveAnnouncer.announce(this.labelText(i)),this.emit(`hierarchy-zoom`,{node:i,path:_e(t,i)}))}focusNode(e){this._rendered.some(t=>t.id===e)&&(this._focusedId=e,this._restoreFocus=!0,this.requestUpdate())}zoomOut(){let e=this._index,t=this.focusedRoot;!e||!t||t===e.root||this.zoomTo(e.parents.get(t.id)?.id)}beginTween(){if(this._transitionDuration<=0||Fn()||this._layout!==`sunburst`||!this._prevSpans.size)return;cancelAnimationFrame(this._tweenFrame);let e=performance.now(),t=i=>{this._tween=Math.min(1,(i-e)/this._transitionDuration),this.requestUpdate(),this._tween<1&&(this._tweenFrame=requestAnimationFrame(t))};this._tween=0,this._tweenFrame=requestAnimationFrame(t)}tweenedSpan(e){if(this._tween>=1)return e;let t=1-(1-this._tween)*(1-this._tween),i=this._prevSpans.get(e.node.id)??{x0:e.x0,x1:e.x0};return{x0:i.x0+(e.x0-i.x0)*t,x1:i.x1+(e.x1-i.x1)*t}}nodeFromEvent(e){let t=e.composedPath()[0]?.closest?.(`[data-id]`)?.getAttribute(`data-id`);return t?this._index?.byId.get(t):void 0}onClick(e){if(this._dragMoved){this._dragMoved=!1;return}let t=this._index,i=this.nodeFromEvent(e);if(!(!t||!i)){if(i===this.focusedRoot){this.zoomOut();return}if(this._failedIds.delete(i.id),this._loadedIds.delete(i.id),i.children?.length||i.hasChildren&&this._loadChildren){this.zoomTo(i.id);return}this.emit(`hierarchy-node-select`,{node:i,path:_e(t,i)})}}get tooltipEl(){return this.shadowRoot?.querySelector(`.chart-tooltip`)??null}isTooltipVisible(){return this.tooltipEl?.hasAttribute(`data-visible`)??!1}showTooltip(e,t,i){let a=this.tooltipEl,r=this.shadowRoot?.querySelector(`.chart`);if(!a||!r)return;a.textContent=this.tooltipText(e),a.setAttribute(`data-visible`,``);let d=a.offsetWidth,m=a.offsetHeight;a.style.left=`${Math.max(0,Math.min(t+12,r.clientWidth-d))}px`,a.style.top=`${Math.max(0,Math.min(i+12,r.clientHeight-m))}px`}hideTooltip(){this.tooltipEl?.removeAttribute(`data-visible`)}onPointerMove(e){let t=this._index;if(!t||this.trackViewGesture(e))return;let i=this.nodeFromEvent(e);if(!i||i===this.focusedRoot){this.clearHover();return}if(i.id!==this._dismissedForId){this._dismissedForId=null;let a=this.shadowRoot?.querySelector(`.chart`)?.getBoundingClientRect();a&&this.showTooltip(i,e.clientX-a.left,e.clientY-a.top)}this._hoveredId!==i.id&&(this._hoveredId=i.id,this.emit(`hierarchy-node-hover`,{node:i,path:_e(t,i)}))}clearHover(){this.hideTooltip(),this._dismissedForId=null,this._hoveredId!==null&&(this._hoveredId=null,this.emit(`hierarchy-node-hover`,{node:null,path:[]}))}onFocusIn(e){let t=e.composedPath()[0]?.closest?.(`[role="treeitem"][data-id]`),i=t?.getAttribute(`data-id`),a=i?this._index?.byId.get(i):void 0;if(!a||a===this.focusedRoot||a.id===this._dismissedForId)return;this._dismissedForId=null;let r=this.shadowRoot?.querySelector(`.chart`)?.getBoundingClientRect(),d=t?.getBoundingClientRect();!r||!d||this.showTooltip(a,d.left-r.left+d.width/2,d.top-r.top+d.height/2)}onFocusOut(){this.hideTooltip()}tooltipText(e){let t=this._tooltipFormatter?.(e);if(t!==void 0)return t;let i=new Intl.NumberFormat(this._locale||this.closest(`[lang]`)?.getAttribute(`lang`)||void 0),a=this._index?.colorValues.get(e.id),r=this._index?.values.get(e.id);return[e.name,a!==void 0?`${i.format(Math.round(a*10)/10)}%`:void 0,r!==void 0&&r>0?i.format(r):void 0].filter(d=>d!==void 0).join(` — `)}labelText(e){return this._labelFormatter?.(e)??e.name}accessibleName(e){let t=new Intl.NumberFormat(this._locale||this.closest(`[lang]`)?.getAttribute(`lang`)||void 0),i=this._index?.colorValues.get(e.id),a=this._index?.values.get(e.id);return[this.labelText(e),i!==void 0?`${t.format(Math.round(i*10)/10)}${this._metricUnitLabel}`:void 0,a!==void 0&&a>0?`${t.format(a)}${this._valueUnitLabel?` ${this._valueUnitLabel}`:``}`:void 0].filter(r=>r!==void 0).join(`, `)}emit(e,t){this.dispatchEvent(new CustomEvent(e,{detail:t,bubbles:!0,composed:!0}))}onKeyDown(e){let t=this._index,i=e.composedPath()[0]?.closest?.(`[role="treeitem"][data-id]`)?.getAttribute(`data-id`);if(!t||!i)return;let a=c=>{c!==void 0&&(this._focusedId=c,this._restoreFocus=!0,this.requestUpdate())},r=this._rendered.find(c=>c.id===i),d=this._rendered.filter(c=>c.parentId===r?.parentId),m=d.findIndex(c=>c.id===i);switch(e.key){case`ArrowRight`:a(d[(m+1)%d.length]?.id);break;case`ArrowLeft`:a(d[(m-1+d.length)%d.length]?.id);break;case`ArrowDown`:a(this._rendered.find(c=>c.parentId===i)?.id);break;case`ArrowUp`:a(this._rendered.some(c=>c.id===r?.parentId)?r?.parentId??void 0:void 0);break;case`Home`:a(d[0]?.id);break;case`End`:a(d[d.length-1]?.id);break;case`Enter`:{let c=t.byId.get(i);if(!c)return;this._failedIds.delete(c.id),this._loadedIds.delete(c.id),c===this.focusedRoot?this.zoomOut():c.children?.length||c.hasChildren&&this._loadChildren?this.zoomTo(c.id):this.emit(`hierarchy-node-select`,{node:c,path:_e(t,c)});break}case`Escape`:if(this.isTooltipVisible()){this.hideTooltip(),this._dismissedForId=i;break}if(this._viewZoom>1){this.resetZoom();break}if(this.focusedRoot===this._index?.root)return;this.zoomOut();break;case`Backspace`:if(this.focusedRoot===this._index?.root)return;this.zoomOut();break;case`+`:case`=`:this.zoomKeyboard(i,this._viewZoom*1.5);break;case`-`:case`_`:this.zoomKeyboard(i,this._viewZoom/1.5);break;case`0`:this.resetZoom();break;default:this.handleTypeahead(e,i);return}e.preventDefault(),e.stopPropagation()}zoomKeyboard(e,t){let i=this.shadowRoot?.querySelector(`[role="treeitem"][data-id="${Ai(e)}"]`),a=this.shadowRoot?.querySelector(`.chart`)?.getBoundingClientRect(),r=i?.getBoundingClientRect(),d=a?.width&&r?.width?this.chartAnchor({clientX:r.left+r.width/2,clientY:r.top+r.height/2}):{x:.5,y:.5};this.setZoomLevel(t,d.x,d.y)}handleTypeahead(e,t){if(e.key.length!==1||e.ctrlKey||e.metaKey||e.altKey)return;clearTimeout(this._typeaheadTimer),this._typeahead+=e.key.toLowerCase(),this._typeaheadTimer=setTimeout(()=>this._typeahead=``,500);let i=this._index;if(!i)return;let a=this._rendered,r=a.findIndex(m=>m.id===t),d=[...a.slice(r+1),...a.slice(0,r+1)].find(m=>{let c=i.byId.get(m.id);return c?this.labelText(c).toLowerCase().startsWith(this._typeahead):!1});d&&(this._focusedId=d.id,this._restoreFocus=!0,this.requestUpdate(),e.preventDefault())}resolveTabFocus(){if(!(this._focusedId&&this._rendered.some(e=>e.id===this._focusedId))){let e=this._rendered[0]?.id,t=!!this.shadowRoot?.activeElement;this._focusedId=e??null,t&&e&&(this._restoreFocus=!0)}return this._focusedId??void 0}updated(e){super.updated(e),this._restoreFocus&&this._focusedId&&(this._restoreFocus=!1,this.shadowRoot?.querySelector(`[role="treeitem"][data-id="${Ai(this._focusedId)}"]`)?.focus({preventScroll:!0})),this.loadLazyCandidates()}isLazy(e){return!!e&&!!e.hasChildren&&!e.children?.length&&!this._loadedIds.has(e.id)}loadLazyCandidates(){let e=this._loadChildren,t=this._index;if(!e||!t)return;let i=this.focusedRoot,a=this.maxDepth===`auto`,r=this.renderedDepth,d=[...this.isLazy(i)?[i]:[],...this._rendered.filter(m=>a||m.depth<r).map(m=>t.byId.get(m.id)).filter(m=>this.isLazy(m))].filter(m=>!this._loadingIds.has(m.id)&&!this._failedIds.has(m.id));d.map(m=>(this._loadingIds.add(m.id),m===i&&this.liveAnnouncer.announce(`${this._loadingLabel} ${this.labelText(m)}`),e(m).then(c=>{m.children=c,this._loadingIds.delete(m.id),this._loadedIds.add(m.id),this._data&&(this._index=vt(this._data)),this.requestUpdate()},c=>{this._loadingIds.delete(m.id),this._failedIds.add(m.id),this.emit(`hierarchy-node-load-error`,{node:m,error:c}),this.requestUpdate()}),m)),d.length&&this.requestUpdate()}fillOf(e){if(e.color)return e.color;let t=this._index?.colorValues.get(e.id);return t!==void 0?this._fill(t):void 0}treeLabel(){return this.getAttribute(`aria-label`)??this._inputLabel}renderBreadcrumb(e,t){let i=_e(e,t);return Ht`<nav class="breadcrumb" aria-label=${this._breadcrumbLabel}>
      ${i.map((a,r)=>r===i.length-1?Ht`<span class="crumb-current" aria-current="location">${this.labelText(a)}</span>`:Ht`<button
              type="button"
              class="crumb"
              @click=${()=>this.zoomTo(a===e.root?void 0:a.id)}
            >${this.labelText(a)}</button><span class="crumb-sep" aria-hidden="true">/</span>`)}
    </nav>`}render(){let e=this._index,t=this.focusedRoot;return!e||!t?Ht`<div class="chart"></div>`:Ht`${this._showBreadcrumb?this.renderBreadcrumb(e,t):p$1}<div
      class="chart ${this._gestures.has(`pinch`)?`pinch`:``}"
      @click=${this.onClick}
      @keydown=${this.onKeyDown}
      @pointerdown=${this.onPointerDown}
      @pointermove=${this.onPointerMove}
      @pointerup=${this.endViewGesture}
      @pointercancel=${this.endViewGesture}
      @pointerleave=${this.clearHover}
      @focusin=${this.onFocusIn}
      @focusout=${this.onFocusOut}
    >
      ${this._layout===`sunburst`?this.renderSunburst(e):this._layout===`icicle`?this.renderIcicle(e,t):this.renderTreemap(e,t)}
      <div class="chart-tooltip" aria-hidden="true"></div>
      ${this._hintVisible&&this._gestures.has(`wheel`)?Ht`<div class="zoom-hint" aria-hidden="true">${this.zoomHintLabel}</div>`:p$1}
      ${this.liveAnnouncer.template()}
    </div>`}captureRendered(e,t){this._rendered=e.map(i=>({id:i.node.id,parentId:this._index?.parents.get(i.node.id)?.id??t,depth:i.depth})),this._tabFocusId=this.resolveTabFocus()}renderSunburst(e){let t=this.focusedRoot,i=this.renderedDepth,a=xt(e,this._rootId,{maxDepth:i,minFraction:this._minAngle/360/this._viewZoom}),r$3=J/2/(i+1),d=this.treeLabel(),m=new Map(a.map(g=>[g.node.id,this.tweenedSpan(g)]));this._prevSpans=new Map([...m.entries()].map(([g,D])=>[g,s(r({},D),{depth:0})])),this.captureRendered(a,t?.id??``);let c=t===e.root,b=100/(i+1)*.9,_=this._viewZoom,C=this.viewRect(.5,.5,.5,.5);return Ht`<svg
      viewBox="${this._viewX*J} ${this._viewY*J} ${J/_} ${J/_}"
      role="tree"
      aria-label=${d??p$1}
    >
      <!-- role=none: this group only centres the geometry, and an unroled node
           between role=tree and its treeitems is an aria-required-parent risk. -->
      <g role="none" transform="translate(${J/2},${J/2})">
        ${ge(a,g=>g.node.id,g=>this.renderArc(g,m.get(g.node.id)??g,r$3))}
        ${this._showLabels&&this._tween>=1?ge(a.map(g=>({n:g,fit:this.arcLabelFit(g,r$3)})).filter(({fit:g})=>g.visible),({n:g})=>`label-${g.node.id}`,({n:g,fit:D})=>Mt$1`<text
                class="arc-label"
                aria-hidden="true"
                font-size=${Math.round(this._labelFontSize/this.effectiveScale*100)/100}
                data-surface=${this.surfaceToneOf(g.node,g.hasChildren?1:.6)}
                transform=${Pi(g.x0,g.x1,(g.depth+.5)*r$3,D.orientation)}
              >${D.text}</text>`):p$1}
      </g>
    </svg>
    <button
      class="center-control"
      aria-label=${this._zoomOutLabel}
      title=${c?p$1:this._zoomOutLabel}
      ?disabled=${c}
      style=${U({left:`${C.x0*100}%`,top:`${C.y0*100}%`,width:`${b*_}%`,height:`${b*_}%`})}
      @click=${this.zoomOut}
    >${t?this.labelText(t):``}</button>`}renderArc(e,t,i){let a=Ei(0,0,e.depth*i,(e.depth+1)*i,t.x0*kt,t.x1*kt,{padAngle:.005}),r=this.fillOf(e.node);return Mt$1`<path
      class="ring"
      data-id=${e.node.id}
      ?data-leaf=${!e.hasChildren}
      ?data-loading=${this._loadingIds.has(e.node.id)}
      ?data-load-error=${this._failedIds.has(e.node.id)}
      d=${a}
      fill=${r??`var(--mp-hierarchy-chart-node-fill)`}
      role="treeitem"
      tabindex=${e.node.id===this._tabFocusId?`0`:`-1`}
      aria-label=${this.accessibleName(e.node)}
      aria-level=${e.level}
      aria-setsize=${e.setsize}
      aria-posinset=${e.posinset}
      aria-expanded=${e.hasChildren?String(e.depth<this.renderedDepth&&!!e.node.children?.length):p$1}
      aria-busy=${this._loadingIds.has(e.node.id)?`true`:p$1}
    ></path>`}arcLabelFit(e,t){return Ii(this.labelText(e.node),(e.x1-e.x0)*kt,e.depth*t*this.effectiveScale,(e.depth+1)*t*this.effectiveScale,this._labelFontSize)}renderIcicle(e,t){let i=this.renderedDepth,a=xt(e,this._rootId,{maxDepth:i,minFraction:this._minSize/J/this._viewZoom}),r$4=i+1,d=this.treeLabel();this.captureRendered(a,t.id);let m=this.viewRect(0,0,1/r$4,1);return Ht`<div class="icicle" role="tree" aria-label=${d??p$1}>
      <div
        class="cell focus-cell"
        data-id=${t.id}
        data-surface=${this.toneOfSurface(void 0)}
        role="treeitem"
        tabindex="-1"
        aria-label=${this.accessibleName(t)}
        aria-level=${st(e,t)}
        aria-setsize="1"
        aria-posinset="1"
        aria-expanded="true"
        title=${t===e.root?p$1:this._zoomOutLabel}
        style=${U(s(r({},this.cellGeometry(m)),{fontSize:`${this._labelFontSize}px`}))}
      >${this._showLabels&&this.cellLabelFits(m)?Ht`<span class="cell-label">${this.labelText(t)}</span>`:p$1}</div>
      ${ge(a,c=>c.node.id,c=>this.renderCell(c,this.viewRect(c.depth/r$4,c.x0,(c.depth+1)/r$4,c.x1),c.hasChildren&&c.depth<this.renderedDepth&&!!c.node.children?.length))}
    </div>`}renderTreemap(e,t){let i=this.renderedDepth,a=Di(e,this._rootId,{maxDepth:i,minArea:(this._minSize/J)**2/(this._viewZoom*this._viewZoom),childPadding:.004,childHeaderSpace:.028}),r=this.treeLabel(),d=_e(e,t).map(m=>this.labelText(m)).join(` / `);return this.captureRendered(a,t.id),Ht`<div class="treemap">
      <button
        class="treemap-header"
        aria-label=${this._zoomOutLabel}
        title=${d}
        ?disabled=${t===e.root}
        @click=${this.zoomOut}
      >${d}</button>
      <div class="treemap-body" role="tree" aria-label=${r??p$1}>
        ${ge(a,m=>m.node.id,m=>this.renderCell(m,this.viewRect(m.x0,m.y0,m.x1,m.y1),m.hasChildren&&m.depth<this.renderedDepth&&!!m.node.children?.length))}
      </div>
    </div>`}cellGeometry(e){return{left:`${e.x0*100}%`,top:`${e.y0*100}%`,width:`${(e.x1-e.x0)*100}%`,height:`${(e.y1-e.y0)*100}%`}}cellLabelFits(e){let t=J*this._hostScale;return Ri((e.x1-e.x0)*t,(e.y1-e.y0)*t,this._labelFontSize)}renderCell(e,t,i){let a=this._layout===`treemap`&&i,r$5=a?void 0:this.fillOf(e.node),d=this._showLabels&&this.cellLabelFits(t);return Ht`<div
      class="cell"
      data-id=${e.node.id}
      ?data-leaf=${!e.hasChildren}
      ?data-branch=${a}
      ?data-loading=${this._loadingIds.has(e.node.id)}
      ?data-load-error=${this._failedIds.has(e.node.id)}
      data-surface=${r$5?this.surfaceToneOf(e.node,1):this.toneOfSurface(void 0)}
      role="treeitem"
      tabindex=${e.node.id===this._tabFocusId?`0`:`-1`}
      aria-label=${this.accessibleName(e.node)}
      aria-level=${e.level}
      aria-setsize=${e.setsize}
      aria-posinset=${e.posinset}
      aria-expanded=${e.hasChildren?String(i):p$1}
      aria-busy=${this._loadingIds.has(e.node.id)?`true`:p$1}
      style=${U(r(s(r({},this.cellGeometry(t)),{fontSize:`${this._labelFontSize}px`}),r$5?{background:r$5}:{}))}
    >${d?Ht`<span class="cell-label">${this.labelText(e.node)}</span>`:p$1}</div>`}}return n})();function lt(n,o){return Math.min(1-1/o,Math.max(0,n))}function En(n){if(!n||n===`transparent`)return!1;let o=n.match(/^rgba\([^)]+,\s*([\d.]+)\s*\)$/i)?.[1];return o!==void 0?Number(o)>=1:/^(rgb\(|#|hsl\()/i.test(n)}function Ai(n){return typeof CSS<`u`&&typeof CSS.escape==`function`?CSS.escape(n):n.replace(/["\\]/g,`\\$&`)}typeof customElements<`u`&&!customElements.get(`mp-hierarchy-chart`)&&customElements.define(`mp-hierarchy-chart`,Dn);var Pn=[`chart`];var Bi=(()=>{class n{constructor(){this.data=Ir(void 0),this.layout=Ir(`sunburst`),this.rootId=g$(void 0),this.maxDepth=Ir(void 0),this.minAngle=Ir(.2),this.minSize=Ir(4),this.showLabels=Ir(!0),this.labelFontSize=Ir(12),this.backdrop=Ir(void 0),this.zoomGestures=Ir(`wheel pinch`),this.zoomHintLabel=Ir(void 0),this.showBreadcrumb=Ir(!1),this.breadcrumbLabel=Ir(void 0),this.colorMin=Ir(0),this.colorMax=Ir(100),this.colorStart=Ir(`#fe0000`),this.colorEnd=Ir(`#21b577`),this.transitionDuration=Ir(300),this.locale=Ir(void 0),this.inputLabel=Ir(void 0),this.zoomOutLabel=Ir(void 0),this.metricUnitLabel=Ir(void 0),this.valueUnitLabel=Ir(void 0),this.loadingLabel=Ir(void 0),this.tooltipFormatter=Ir(void 0),this.labelFormatter=Ir(void 0),this.loadChildren=Ir(void 0),this.zoom=p$(),this.nodeSelect=p$(),this.nodeHover=p$(),this.nodeLoadError=p$(),this.chartRef=m$(`chart`),zl(()=>{let e=this.chartRef()?.nativeElement;e&&(e.data=this.data())}),zl(()=>{let e=this.chartRef()?.nativeElement;e&&(e.layout=this.layout())}),zl(()=>{let e=this.chartRef()?.nativeElement;e&&(e.rootId=this.rootId())}),zl(()=>{let e=this.chartRef()?.nativeElement,t=this.maxDepth();e&&t!==void 0&&(e.maxDepth=t)}),zl(()=>{let e=this.chartRef()?.nativeElement;e&&(e.minAngle=this.minAngle())}),zl(()=>{let e=this.chartRef()?.nativeElement;e&&(e.minSize=this.minSize())}),zl(()=>{let e=this.chartRef()?.nativeElement;e&&(e.showLabels=this.showLabels())}),zl(()=>{let e=this.chartRef()?.nativeElement;e&&(e.labelFontSize=this.labelFontSize())}),zl(()=>{let e=this.chartRef()?.nativeElement;e&&(e.backdrop=this.backdrop())}),zl(()=>{let e=this.chartRef()?.nativeElement;e&&(e.zoomGestures=this.zoomGestures())}),zl(()=>{let e=this.chartRef()?.nativeElement,t=this.zoomHintLabel();e&&t!==void 0&&(e.zoomHintLabel=t)}),zl(()=>{let e=this.chartRef()?.nativeElement;e&&(e.showBreadcrumb=this.showBreadcrumb())}),zl(()=>{let e=this.chartRef()?.nativeElement,t=this.breadcrumbLabel();e&&t!==void 0&&(e.breadcrumbLabel=t)}),zl(()=>{let e=this.chartRef()?.nativeElement;e&&(e.colorMin=this.colorMin())}),zl(()=>{let e=this.chartRef()?.nativeElement;e&&(e.colorMax=this.colorMax())}),zl(()=>{let e=this.chartRef()?.nativeElement;e&&(e.colorStart=this.colorStart())}),zl(()=>{let e=this.chartRef()?.nativeElement;e&&(e.colorEnd=this.colorEnd())}),zl(()=>{let e=this.chartRef()?.nativeElement;e&&(e.transitionDuration=this.transitionDuration())}),zl(()=>{let e=this.chartRef()?.nativeElement;e&&(e.locale=this.locale())}),zl(()=>{let e=this.chartRef()?.nativeElement;e&&(e.inputLabel=this.inputLabel()??null)}),zl(()=>{let e=this.chartRef()?.nativeElement,t=this.zoomOutLabel();e&&t!==void 0&&(e.zoomOutLabel=t)}),zl(()=>{let e=this.chartRef()?.nativeElement,t=this.metricUnitLabel();e&&t!==void 0&&(e.metricUnitLabel=t)}),zl(()=>{let e=this.chartRef()?.nativeElement,t=this.valueUnitLabel();e&&t!==void 0&&(e.valueUnitLabel=t)}),zl(()=>{let e=this.chartRef()?.nativeElement,t=this.loadingLabel();e&&t!==void 0&&(e.loadingLabel=t)}),zl(()=>{let e=this.chartRef()?.nativeElement;e&&(e.tooltipFormatter=this.tooltipFormatter())}),zl(()=>{let e=this.chartRef()?.nativeElement;e&&(e.labelFormatter=this.labelFormatter())}),zl(()=>{let e=this.chartRef()?.nativeElement;e&&(e.loadChildren=this.loadChildren())})}onZoom(e){let t=e.detail;this.rootId.set(e.target.rootId),this.zoom.emit(t)}onNodeSelect(e){this.nodeSelect.emit(e.detail)}onNodeHover(e){this.nodeHover.emit(e.detail)}onNodeLoadError(e){this.nodeLoadError.emit(e.detail)}static{this.ɵfac=function(t){return new(t||n)}}static{this.ɵcmp=xi$1({type:n,selectors:[[`bs-hierarchy-chart`]],viewQuery:function(t,i){t&1&&Iy(i.chartRef,Pn,5),t&2&&JT()},inputs:{data:[1,`data`],layout:[1,`layout`],rootId:[1,`rootId`],maxDepth:[1,`maxDepth`],minAngle:[1,`minAngle`],minSize:[1,`minSize`],showLabels:[1,`showLabels`],labelFontSize:[1,`labelFontSize`],backdrop:[1,`backdrop`],zoomGestures:[1,`zoomGestures`],zoomHintLabel:[1,`zoomHintLabel`],showBreadcrumb:[1,`showBreadcrumb`],breadcrumbLabel:[1,`breadcrumbLabel`],colorMin:[1,`colorMin`],colorMax:[1,`colorMax`],colorStart:[1,`colorStart`],colorEnd:[1,`colorEnd`],transitionDuration:[1,`transitionDuration`],locale:[1,`locale`],inputLabel:[1,`inputLabel`],zoomOutLabel:[1,`zoomOutLabel`],metricUnitLabel:[1,`metricUnitLabel`],valueUnitLabel:[1,`valueUnitLabel`],loadingLabel:[1,`loadingLabel`],tooltipFormatter:[1,`tooltipFormatter`],labelFormatter:[1,`labelFormatter`],loadChildren:[1,`loadChildren`]},outputs:{rootId:`rootIdChange`,zoom:`zoom`,nodeSelect:`nodeSelect`,nodeHover:`nodeHover`,nodeLoadError:`nodeLoadError`},decls:2,vars:0,consts:[[`chart`,``],[`bsForwardAria`,``,1,`bs-hierarchy-chart`,3,`hierarchy-zoom`,`hierarchy-node-select`,`hierarchy-node-hover`,`hierarchy-node-load-error`]],template:function(t,i){t&1&&(Xa(0,`mp-hierarchy-chart`,1,0),vc(`hierarchy-zoom`,function(r){return i.onZoom(r)})(`hierarchy-node-select`,function(r){return i.onNodeSelect(r)})(`hierarchy-node-hover`,function(r){return i.onNodeHover(r)})(`hierarchy-node-load-error`,function(r){return i.onNodeLoadError(r)}),Af())},dependencies:[_r],styles:[`[_nghost-%COMP%]{display:block}`]})}}return n})();var Ln=(n,o)=>({linesCovered:n,linesCoverable:o,branchesCovered:0,branchesTotal:0,filesCount:0});var Nn=(n,o)=>o.flag;var Ui=(n,o)=>o.path;function In(n,o){if(n&1){let e=$T();Xa(0,`button`,5),vc(`click`,function(){let i=mg(e).$implicit;return vg(ZT(2).selectFlag(i.flag))}),Ff(1),Xa(2,`span`,7),Ff(3),Af()()}if(n&2){let e=o.$implicit,t=ZT(2);Ty(`btn-primary`,t.selectedFlag()===e.flag)(`btn-outline-secondary`,t.selectedFlag()!==e.flag),gc(`aria-label`,__(``,e.flag,` coverage `,e.rate))(`aria-pressed`,t.selectedFlag()===e.flag),kb(),jf(` `,e.flag,` `),kb(2),Oy(e.rate)}}function Rn(n,o){if(n&1){let e=$T();Xa(0,`div`,2)(1,`span`,4),Ff(2,`Flags:`),Af(),Xa(3,`button`,5),vc(`click`,function(){mg(e);return vg(ZT().selectFlag(null))}),Ff(4,`All`),Af(),LT(5,In,4,10,`button`,6,Nn),Af()}if(n&2){let e=ZT();kb(3),Ty(`btn-primary`,!e.selectedFlag())(`btn-outline-secondary`,!!e.selectedFlag()),gc(`aria-pressed`,!e.selectedFlag()),kb(2),FT(e.flagEntries())}}function zn(n,o){n&1&&my(0)}function An(n,o){if(n&1){let e=$T();Xa(0,`div`,3)(1,`div`,8)(2,`bs-hierarchy-chart`,9),vc(`rootIdChange`,function(i){mg(e);return vg(ZT().chartRootId.set(i))})(`zoom`,function(i){mg(e);return vg(ZT().onChartZoom(i))})(`nodeSelect`,function(i){mg(e);return vg(ZT().onChartSelect(i))}),Af()(),Xa(3,`div`,10),dy(4,zn,1,0,`ng-container`,11),Af()()}if(n&2){let e=ZT(),t=e_(7);kb(2),hy(`data`,o)(`rootId`,e.chartRootId())(`maxDepth`,`auto`)(`colorMin`,60)(`colorMax`,80),kb(2),hy(`ngTemplateOutlet`,t)}}function Bn(n,o){n&1&&my(0)}function Un(n,o){if(n&1&&dy(0,Bn,1,0,`ng-container`,11),n&2){ZT();hy(`ngTemplateOutlet`,e_(7))}}function Vn(n,o){if(n&1){let e=$T();Xa(0,`bs-breadcrumb-item`)(1,`a`,13),vc(`click`,function(){let i=mg(e).$implicit;return vg(ZT(2).openFolder(i.path))}),Ff(2),Af()()}if(n&2){let e=o.$implicit;kb(2),Oy(e.name)}}function On(n,o){if(n&1&&(Xa(0,`span`,17),Ff(1),Af()),n&2){let e=ZT(2);kb(),ky(`Showing `,e.unmatchedFiles.length,` of `,e.unmatchedTotal,` unmatched paths.`)}}function qn(n,o){if(n&1&&(Xa(0,`bs-alert`,14),Ff(1),Xa(2,`code`),Ff(3,`rootDir`),Af(),Ff(4,`/checkout. `),xT(5,On,2,2,`span`,17),Af()),n&2){let e=ZT();hy(`type`,ZT(2).warningColor),kb(),ky(` `,e.unmatchedTotal,` report path(s) couldn't be matched to the repository tree (e.g. `,e.unmatchedFiles[0],`). Check the action's `),kb(4),OT(e.unmatchedFiles.length<e.unmatchedTotal?5:-1)}}function Hn(n,o){n&1&&(Xa(0,`p`,15),Ff(1,`No coverage data in this folder.`),Af())}function jn(n,o){if(n&1){let e=$T();Xa(0,`a`,23),vc(`click`,function(){mg(e);let i=ZT().$implicit;return vg(ZT(4).openFile(i.path))}),Ff(1),Af()}if(n&2){let e=ZT().$implicit;kb(),Oy(e.name)}}function Zn(n,o){if(n&1){let e=$T();Xa(0,`a`,23),vc(`click`,function(){mg(e);let i=ZT().$implicit;return vg(ZT(4).openFolder(i.path))}),Ff(1),Af()}if(n&2){let e=ZT().$implicit;kb(),Oy(e.name)}}function Gn(n,o){if(n&1&&(Xa(0,`tr`)(1,`td`),mc(2,`i`,19),xT(3,jn,2,1,`a`,20)(4,Zn,2,1,`a`,20),Af(),Xa(5,`td`),mc(6,`app-coverage-bar`,21),Af(),Xa(7,`td`,22),Ff(8),Af()()),n&2){let e=o.$implicit;kb(2),Ty(`bi-folder-fill`,!e.isFile)(`bi-file-earmark-code`,e.isFile)(`text-warning`,!e.isFile),kb(),OT(e.isFile?3:4),kb(3),hy(`summary`,Bf(10,Ln,e.linesCovered,e.linesCoverable)),kb(2),ky(``,e.linesCovered,`/`,e.linesCoverable)}}function Kn(n,o){if(n&1&&(Xa(0,`bs-table`,16)(1,`thead`)(2,`tr`)(3,`th`),Ff(4,`Name`),Af(),Xa(5,`th`),Ff(6,`Coverage`),Af(),Xa(7,`th`,18),Ff(8,`Lines`),Af()()(),Xa(9,`tbody`),LT(10,Gn,9,13,`tr`,null,Ui),Af()()),n&2){let e=ZT();hy(`isResponsive`,!0),kb(10),FT(e.entries)}}function Wn(n,o){if(n&1&&(xT(0,qn,6,4,`bs-alert`,14),xT(1,Hn,2,0,`p`,15)(2,Kn,12,1,`bs-table`,16)),n&2){let e=o;OT(e.unmatchedFiles.length>0?0:-1),kb(),OT(e.entries.length===0?1:2)}}function Xn(n,o){n&1&&mc(0,`bs-spinner`)}function Yn(n,o){if(n&1){let e=$T();Xa(0,`bs-breadcrumb`,12)(1,`bs-breadcrumb-item`)(2,`a`,13),vc(`click`,function(){mg(e);return vg(ZT().openFolder(``))}),Ff(3,`root`),Af()(),LT(4,Vn,3,1,`bs-breadcrumb-item`,null,Ui),Af(),xT(6,Wn,3,2)(7,Xn,1,0,`bs-spinner`)}if(n&2){let e,t=ZT();kb(4),FT(t.pathSegments()),kb(2),OT((e=t.tree())?6:7,e)}}var ct=class n{router=p(wt$1);route=p(jt);browse=p(u);owner=Ir.required();name=Ir.required();sha=Ir.required();tree=ne$1(null);currentPath=ne$1(``);hierarchy=ne$1(null);chartRootId=ne$1(`/`);warningColor=wu.warning;flagTotals=ne$1(null);selectedFlag=ne$1(null);flagEntries=On$1(()=>{let o=this.flagTotals();return o?Object.entries(o).map(([e,t])=>({flag:e,rate:t.linesCoverable>0?`${(t.linesCovered/t.linesCoverable*100).toFixed(1)}%`:`—`})):[]});pathSegments=On$1(()=>{let o=this.currentPath();if(!o)return[];let e=[],t=``;for(let i of o.split(`/`))t=t?`${t}/${i}`:i,e.push({name:i,path:t});return e});treeToken=0;metaToken=0;initialized=!1;constructor(){this.route.queryParamMap.pipe(w$1()).subscribe(o=>{let e=o.get(`flag`);e!==this.selectedFlag()&&(this.selectedFlag.set(e),this.initialized&&this.openFolder(this.currentPath()))}),zl(()=>{let o=this.owner(),e=this.name(),t=this.sha();!o||!e||!t||G(()=>this.reload(o,e,t))})}reload(o,e,t){this.tree.set(null),this.hierarchy.set(null),this.currentPath.set(``),this.chartRootId.set(`/`),this.selectedFlag.set(this.route.snapshot.queryParamMap.get(`flag`)),this.flagTotals.set(null),this.initialized=!0,this.openFolder(``),this.loadCommitMeta(o,e,t)}async loadCommitMeta(o,e,t){let i=++this.metaToken;try{let a=await this.browse.getHierarchy(o,e,t);if(i!==this.metaToken)return;this.hierarchy.set(a)}catch{if(i!==this.metaToken)return;this.hierarchy.set(null)}try{let a=(await this.browse.getCommit(o,e,t)).flagTotals??null;if(i!==this.metaToken)return;this.flagTotals.set(a)}catch{if(i!==this.metaToken)return;this.flagTotals.set(null)}}async openFolder(o){let e=++this.treeToken;this.currentPath.set(o),this.chartRootId.set(o||`/`),this.tree.set(null);try{let t=await this.browse.getTree(this.owner(),this.name(),this.sha(),o||void 0,this.selectedFlag()??void 0);if(e!==this.treeToken)return;this.tree.set(t)}catch{if(e!==this.treeToken)return;this.tree.set({buildId:``,entries:[],unmatchedFiles:[],unmatchedTotal:0})}}selectFlag(o){o!==this.selectedFlag()&&this.router.navigate([],{relativeTo:this.route,queryParams:{flag:o},queryParamsHandling:`merge`,replaceUrl:!0})}openFile(o){this.router.navigate([`/r`,this.owner(),this.name(),`c`,this.sha(),`f`],{queryParams:{path:o}})}onChartZoom(o){let e=o.node.id===`/`?``:o.node.id;e!==this.currentPath()&&this.openFolder(e)}onChartSelect(o){this.openFile(o.node.id)}static ɵfac=function(e){return new(e||n)};static ɵcmp=xi$1({type:n,selectors:[[`app-commit-files-panel`]],inputs:{owner:[1,`owner`],name:[1,`name`],sha:[1,`sha`]},decls:8,vars:2,consts:[[`folderList`,``],[1,`mt-3`,`d-block`],[`role`,`group`,`aria-label`,`Filter by flag`,1,`d-flex`,`flex-wrap`,`gap-2`,`align-items-center`,`px-3`,`pt-3`],[1,`row`,`g-3`,`mb-2`],[`aria-hidden`,`true`,1,`small`,`text-muted`],[`type`,`button`,1,`btn`,`btn-sm`,3,`click`],[`type`,`button`,1,`btn`,`btn-sm`,3,`btn-primary`,`btn-outline-secondary`],[1,`opacity-75`],[1,`col-12`,`col-lg-5`],[`layout`,`sunburst`,`inputLabel`,`Coverage by folder`,`valueUnitLabel`,`lines`,2,`max-width`,`420px`,`margin-inline`,`auto`,3,`rootIdChange`,`zoom`,`nodeSelect`,`data`,`rootId`,`maxDepth`,`colorMin`,`colorMax`],[1,`col-12`,`col-lg-7`],[4,`ngTemplateOutlet`],[1,`mb-2`],[`href`,`javascript:void(0)`,3,`click`],[1,`d-block`,`mb-2`,3,`type`],[1,`text-muted`,`mb-0`],[3,`isResponsive`],[1,`d-block`,`small`],[1,`text-end`],[1,`bi`],[`href`,`javascript:void(0)`,1,`ms-1`],[3,`summary`],[1,`text-end`,`small`,`text-muted`],[`href`,`javascript:void(0)`,1,`ms-1`,3,`click`]],template:function(e,t){if(e&1&&(Xa(0,`bs-card`,1)(1,`bs-card-header`),Ff(2,`Files`),Af(),xT(3,Rn,7,5,`div`,2),xT(4,An,5,6,`div`,3)(5,Un,1,1,`ng-container`),Af(),dy(6,Yn,8,1,`ng-template`,null,0,G_)),e&2){let i;kb(3),OT(t.flagEntries().length>0?3:-1),kb(),OT((i=t.hierarchy())?4:5,i)}},dependencies:[xD,KM,re,ne$2,w3,Q,Ci,wi,Bi,re$1,K],encapsulation:2})};function Jn(n,o){if(n&1&&mc(0,`app-commit-files-panel`,0),n&2){let e=o;hy(`owner`,e.owner)(`name`,e.name)(`sha`,e.sha)}}var dt=class n{spark=p(st$1);po=Ir.required();target=ne$1(null);constructor(){zl(async()=>{let o=this.po(),e=y$1(o,`Sha`)?.value,t=y$1(o,`Repository`)?.value;if(typeof e!=`string`||!e||typeof t!=`string`||!t){this.target.set(null);return}try{let a=y$1(await this.spark.get(`Repository`,t),`FullName`)?.value,[r,d]=typeof a==`string`?a.split(`/`):[];this.target.set(r&&d?{owner:r,name:d,sha:e}:null)}catch{this.target.set(null)}})}static ɵfac=function(e){return new(e||n)};static ɵcmp=xi$1({type:n,selectors:[[`app-commit-files-extras`]],inputs:{po:[1,`po`]},decls:1,vars:1,consts:[[3,`owner`,`name`,`sha`]],template:function(e,t){if(e&1&&xT(0,Jn,1,3,`app-commit-files-panel`,0),e&2){let i;OT((i=t.target())?0:-1,i)}},dependencies:[ct],encapsulation:2})};var ht=class n{http=p(nE);getMyAccounts(){return Ow(this.http.get(`/api/me/accounts`))}resync(){return Ow(this.http.post(`/api/me/accounts/resync`,{}))}static ɵfac=function(e){return new(e||n)};static ɵprov=_({token:n,factory:n.ɵfac,providedIn:`root`})};function Qn(n,o){n&1&&(Xa(0,`p`,7),Ff(1),Af()),n&2&&(kb(),Oy(o))}function eo(n,o){if(n&1){let e=$T();Xa(0,`bs-alert`,0)(1,`div`,2)(2,`span`,3),mc(3,`i`,4),Ff(4),j_(5,`t`),Af(),Xa(6,`button`,5),vc(`click`,function(){mg(e);return vg(ZT().reconnect())}),mc(7,`i`,6),Ff(8),j_(9,`t`),Af()(),xT(10,Qn,2,1,`p`,7),Af()}if(n&2){let e,t=ZT();hy(`type`,t.warningColor)(`announce`,!0),kb(4),jf(` `,B_(5,6,`app.reauthBanner`),` `),kb(2),hy(`disabled`,t.reconnecting()),kb(2),jf(` `,B_(9,8,`app.reconnectGitHub`),` `),kb(2),OT((e=t.reconnectError())?10:-1,e)}}function to(n,o){if(n&1&&(Xa(0,`p`,1),Ff(1),j_(2,`t`),Xa(3,`a`,8),Ff(4),j_(5,`t`),Af(),Ff(6),j_(7,`t`),Af()),n&2){let e=ZT();kb(),jf(` `,B_(2,4,`app.installAppHintBefore`),` `),kb(2),hy(`href`,e.gitHubAppUrl(),lv),kb(),Oy(B_(5,6,`app.installAppHintLink`)),kb(2),jf(` `,B_(7,8,`app.installAppHintAfter`),` `)}}var mt=class n{accountsService=p(ht);gitHubLogin=p(V);authService=p(S);gitHubAppUrl=ne$1(`https://github.com/apps/coverageproduction`);reauthRequired=ne$1(!1);reconnecting=ne$1(!1);reconnectError=ne$1(null);warningColor=wu.warning;constructor(){zl(()=>{this.authService.user()?.isAuthenticated?this.load():this.reauthRequired.set(!1)})}async load(){try{let o=await this.accountsService.getMyAccounts();this.gitHubAppUrl.set(o.gitHubAppUrl),this.reauthRequired.set(o.gitHubReauthRequired??!1)}catch{this.reauthRequired.set(!1)}}async reconnect(){this.reconnectError.set(null),this.reconnecting.set(!0);try{let o=await this.gitHubLogin.login(me);if(o.success){await this.accountsService.resync(),await this.load();return}if(o.error===`popup_closed`)return;this.reconnectError.set(o.message??null)}finally{this.reconnecting.set(!1)}}static ɵfac=function(e){return new(e||n)};static ɵcmp=xi$1({type:n,selectors:[[`app-home-extras`]],decls:2,vars:2,consts:[[1,`d-block`,`mt-3`,3,`type`,`announce`],[1,`text-muted`,`small`,`mt-3`,`mb-0`],[1,`d-flex`,`align-items-center`,`gap-2`,`flex-wrap`],[1,`me-auto`],[1,`bi`,`bi-exclamation-triangle`],[1,`btn`,`btn-sm`,`btn-warning`,`text-nowrap`,3,`click`,`disabled`],[1,`bi`,`bi-github`],[1,`small`,`mb-0`,`mt-2`],[`target`,`_blank`,`rel`,`noopener`,3,`href`]],template:function(e,t){e&1&&(xT(0,eo,11,10,`bs-alert`,0),xT(1,to,8,10,`p`,1)),e&2&&(OT(t.reauthRequired()?0:-1),kb(),OT(t.authService.user()?.isAuthenticated?1:-1))},dependencies:[Q,it$1],encapsulation:2})};function io(n,o){if(n&1&&mc(0,`app-repo-badge-panel`,3)(1,`app-repo-gate-panel`,3)(2,`app-repo-trend-panel`,3)(3,`app-repo-setup-panel`),n&2){let e=o;hy(`owner`,e.owner)(`name`,e.name),kb(),hy(`owner`,e.owner)(`name`,e.name),kb(),hy(`owner`,e.owner)(`name`,e.name)}}function no(n,o){if(n&1&&xT(0,io,4,6),n&2){let e,t=ZT().$implicit;OT((e=ZT().repoOf(t))?0:-1,e)}}function oo(n,o){if(n&1&&mc(0,`app-commit-files-extras`,2),n&2){let e=ZT().$implicit;hy(`po`,e)}}function ao(n,o){n&1&&mc(0,`app-home-extras`)}function ro(n,o){n&1&&mc(0,`app-account-tokens-panel`,4),n&2&&hy(`login`,o)}function so(n,o){if(n&1&&xT(0,ro,1,1,`app-account-tokens-panel`,4),n&2){let e,t=ZT().$implicit;OT((e=ZT().loginOf(t))?0:-1,e)}}function lo(n,o){if(n&1&&xT(0,no,1,1)(1,oo,1,1,`app-commit-files-extras`,2)(2,ao,1,0,`app-home-extras`)(3,so,1,1),n&2){let e=o.entityType;OT(e.name===`Repository`?0:e.name===`Commit`?1:e.name===`Home`?2:e.name===`Account`?3:-1)}}var Mt=class n{repoOf(o){let e=y$1(o,`FullName`)?.value;if(typeof e!=`string`)return null;let[t,i]=e.split(`/`);return t&&i?{owner:t,name:i}:null}loginOf(o){let e=y$1(o,`Login`)?.value;return typeof e==`string`&&e?e:null}static ɵfac=function(e){return new(e||n)};static ɵcmp=xi$1({type:n,selectors:[[`app-po-detail-page`]],decls:3,vars:1,consts:[[`extras`,``],[3,`extraContentTemplate`],[3,`po`],[3,`owner`,`name`],[3,`login`]],template:function(e,t){if(e&1&&(mc(0,`spark-po-detail`,1),dy(1,lo,4,1,`ng-template`,null,0,G_)),e&2)hy(`extraContentTemplate`,e_(2))},dependencies:[ft$1,et,tt,nt,ot,rt,dt,mt],encapsulation:2})};export{Mt as default};