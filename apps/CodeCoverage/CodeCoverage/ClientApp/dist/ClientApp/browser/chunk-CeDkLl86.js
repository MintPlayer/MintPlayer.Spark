import{a as v,i as u,n as s$1,t as r}from"./chunk-C9yOwMO6.js";import{Dn as p$,En as p$1,I as JT,N as Ir,P as Iy,_ as Dy,ar as xi,it as Pi,on as ki,pn as m$,qn as vy,qt as gc,vn as mt}from"./chunk-Btq1RDbg.js";import{a as X$1,c as b,i as V$1,l as p$2,t as Ht,u as st}from"./chunk-CwXwit_b.js";import{t as f}from"./chunk-CgYYSK-z.js";var zt=u((Ft,Le)=>{"use strict";function ye(e){return e instanceof Map?e.clear=e.delete=e.set=function(){throw new Error(`map is read-only`)}:e instanceof Set&&(e.add=e.clear=e.delete=function(){throw new Error(`set is read-only`)}),Object.freeze(e),Object.getOwnPropertyNames(e).forEach(t=>{let i=e[t],u=typeof i;(u===`object`||u===`function`)&&!Object.isFrozen(i)&&ye(i)}),e}var X=class{constructor(t){t.data===void 0&&(t.data={}),this.data=t.data,this.isMatchIgnored=!1}ignoreMatch(){this.isMatchIgnored=!0}};function Re(e){return e.replace(/&/g,`&amp;`).replace(/</g,`&lt;`).replace(/>/g,`&gt;`).replace(/"/g,`&quot;`).replace(/'/g,`&#x27;`)}function B(e,...t){let i=Object.create(null);for(let u in e)i[u]=e[u];return t.forEach(function(u){for(let b in u)i[b]=u[b]}),i}var tt=`</span>`,be=e=>!!e.scope,nt=(e,{prefix:t})=>{if(e.startsWith(`language:`))return e.replace(`language:`,`language-`);if(e.includes(`.`)){let i=e.split(`.`);return[`${t}${i.shift()}`,...i.map((u,b)=>`${u}${`_`.repeat(b+1)}`)].join(` `)}return`${t}${e}`},ie=class{constructor(t,i){this.buffer=``,this.classPrefix=i.classPrefix,t.walk(this)}addText(t){this.buffer+=Re(t)}openNode(t){if(!be(t))return;let i=nt(t.scope,{prefix:this.classPrefix});this.span(i)}closeNode(t){be(t)&&(this.buffer+=tt)}value(){return this.buffer}span(t){this.buffer+=`<span class="${t}">`}},_e=(e={})=>{let t={children:[]};return Object.assign(t,e),t},se=class e{constructor(){this.rootNode=_e(),this.stack=[this.rootNode]}get top(){return this.stack[this.stack.length-1]}get root(){return this.rootNode}add(t){this.top.children.push(t)}openNode(t){let i=_e({scope:t});this.add(i),this.stack.push(i)}closeNode(){if(this.stack.length>1)return this.stack.pop()}closeAllNodes(){for(;this.closeNode(););}toJSON(){return JSON.stringify(this.rootNode,null,4)}walk(t){return this.constructor._walk(t,this.rootNode)}static _walk(t,i){return typeof i==`string`?t.addText(i):i.children&&(t.openNode(i),i.children.forEach(u=>this._walk(t,u)),t.closeNode(i)),t}static _collapse(t){typeof t!=`string`&&t.children&&(t.children.every(i=>typeof i==`string`)?t.children=[t.children.join(``)]:t.children.forEach(i=>{e._collapse(i)}))}},re=class extends se{constructor(t){super(),this.options=t}addText(t){t!==``&&this.add(t)}startScope(t){this.openNode(t)}endScope(){this.closeNode()}__addSublanguage(t,i){let u=t.root;i&&(u.scope=`language:${i}`),this.add(u)}toHTML(){return new ie(this,this.options).value()}finalize(){return this.closeAllNodes(),!0}};function H(e){return e?typeof e==`string`?e:e.source:null}function Se(e){return C(`(?=`,e,`)`)}function it(e){return C(`(?:`,e,`)*`)}function st(e){return C(`(?:`,e,`)?`)}function C(...e){return e.map(i=>H(i)).join(``)}function rt(e){let t=e[e.length-1];return typeof t==`object`&&t.constructor===Object?(e.splice(e.length-1,1),t):{}}function Z(...e){return`(`+(rt(e).capture?``:`?:`)+e.map(u=>H(u)).join(`|`)+`)`}function Ne(e){return new RegExp(e.toString()+`|`).exec(``).length-1}function ct(e,t){let i=e&&e.exec(t);return i&&i.index===0}var ot=new RegExp(Z(/\[(?:[^\\\]]|\\.)*\]/,/\(\?<(?![=!])[^>]+>/,/\(\?'[^']+'/,/\(\??/,/\\([1-9][0-9]*)/,/\\./));function oe(e,{joinWith:t}){let i=0;return e.map(u=>{i+=1;let b=i,_=H(u),c=``;for(;_.length>0;){let r=ot.exec(_);if(!r){c+=_;break}c+=_.substring(0,r.index),_=_.substring(r.index+r[0].length),r[0][0]===`\\`&&r[1]?c+=`\\`+String(Number(r[1])+b):(c+=r[0],(r[0]===`(`||/^\(\?[<']/.test(r[0]))&&i++)}return c}).map(u=>`(${u})`).join(t)}var at=/\b\B/,Ae=`[a-zA-Z]\\w*`,ae=`[a-zA-Z_]\\w*`,ke=`\\b\\d+(\\.\\d+)?`,Ie=`(-?)(\\b0[xX][a-fA-F0-9]+|(\\b\\d+(\\.\\d*)?|\\.\\d+)([eE][-+]?\\d+)?)`,Te=`\\b(0b[01]+)`,lt=`!|!=|!==|%|%=|&|&&|&=|\\*|\\*=|\\+|\\+=|,|-|-=|/=|/|:|;|<<|<<=|<=|<|===|==|=|>>>=|>>=|>=|>>>|>>|>|\\?|\\[|\\{|\\(|\\^|\\^=|\\||\\|=|\\|\\||~`,ut=(e={})=>{let t=/^#![ ]*\//;return e.binary&&(e.begin=C(t,/.*\b/,e.binary,/\b.*/)),B({scope:`meta`,begin:t,end:/$/,relevance:0,"on:begin":(i,u)=>{i.index!==0&&u.ignoreMatch()}},e)},U={begin:`\\\\[\\s\\S]`,relevance:0},ft={scope:`string`,begin:`'`,end:`'`,illegal:`\\n`,contains:[U]},gt={scope:`string`,begin:`"`,end:`"`,illegal:`\\n`,contains:[U]},ht={begin:/\b(a|an|the|are|I'm|isn't|don't|doesn't|won't|but|just|should|pretty|simply|enough|gonna|going|wtf|so|such|will|you|your|they|like|more)\b/},J=function(e,t,i={}){let u=B({scope:`comment`,begin:e,end:t,contains:[]},i);u.contains.push({scope:`doctag`,begin:`[ ]*(?=(TODO|FIXME|NOTE|BUG|OPTIMIZE|HACK|XXX):)`,end:/(TODO|FIXME|NOTE|BUG|OPTIMIZE|HACK|XXX):/,excludeBegin:!0,relevance:0});let b=Z(`I`,`a`,`is`,`so`,`us`,`to`,`at`,`if`,`in`,`it`,`on`,/[A-Za-z]+['](d|ve|re|ll|t|s|n)/,/[A-Za-z]+[-][a-z]+/,/[A-Za-z][a-z]{2,}/);return u.contains.push({begin:C(/[ ]+/,`(`,b,/[.]?[:]?([.][ ]|[ ])/,`){3}`)}),u},pt=J(`//`,`$`),dt=J(`/\\*`,`\\*/`),Et=J(`#`,`$`),bt={scope:`number`,begin:ke,relevance:0},_t={scope:`number`,begin:Ie,relevance:0},xt={scope:`number`,begin:Te,relevance:0},Mt={scope:`regexp`,begin:/\/(?=[^/\n]*\/)/,end:/\/[gimuy]*/,contains:[U,{begin:/\[/,end:/\]/,relevance:0,contains:[U]}]},wt={scope:`title`,begin:Ae,relevance:0},Ot={scope:`title`,begin:ae,relevance:0},yt={begin:`\\.\\s*`+ae,relevance:0},Rt=function(e){return Object.assign(e,{"on:begin":(t,i)=>{i.data._beginMatch=t[1]},"on:end":(t,i)=>{i.data._beginMatch!==t[1]&&i.ignoreMatch()}})},F=Object.freeze({__proto__:null,APOS_STRING_MODE:ft,BACKSLASH_ESCAPE:U,BINARY_NUMBER_MODE:xt,BINARY_NUMBER_RE:Te,COMMENT:J,C_BLOCK_COMMENT_MODE:dt,C_LINE_COMMENT_MODE:pt,C_NUMBER_MODE:_t,C_NUMBER_RE:Ie,END_SAME_AS_BEGIN:Rt,HASH_COMMENT_MODE:Et,IDENT_RE:Ae,MATCH_NOTHING_RE:at,METHOD_GUARD:yt,NUMBER_MODE:bt,NUMBER_RE:ke,PHRASAL_WORDS_MODE:ht,QUOTE_STRING_MODE:gt,REGEXP_MODE:Mt,RE_STARTERS_RE:lt,SHEBANG:ut,TITLE_MODE:wt,UNDERSCORE_IDENT_RE:ae,UNDERSCORE_TITLE_MODE:Ot});function St(e,t){e.input[e.index-1]===`.`&&t.ignoreMatch()}function Nt(e,t){e.className!==void 0&&(e.scope=e.className,delete e.className)}function At(e,t){t&&e.beginKeywords&&(e.begin=`\\b(`+e.beginKeywords.split(` `).join(`|`)+`)(?!\\.)(?=\\b|\\s)`,e.__beforeBegin=St,e.keywords=e.keywords||e.beginKeywords,delete e.beginKeywords,e.relevance===void 0&&(e.relevance=0))}function kt(e,t){Array.isArray(e.illegal)&&(e.illegal=Z(...e.illegal))}function It(e,t){if(e.match){if(e.begin||e.end)throw new Error(`begin & end are not supported with match`);e.begin=e.match,delete e.match}}function Tt(e,t){e.relevance===void 0&&(e.relevance=1)}var Bt=(e,t)=>{if(!e.beforeMatch)return;if(e.starts)throw new Error(`beforeMatch cannot be used with starts`);let i=Object.assign({},e);Object.keys(e).forEach(u=>{delete e[u]}),e.keywords=i.keywords,e.begin=C(i.beforeMatch,Se(i.begin)),e.starts={relevance:0,contains:[Object.assign(i,{endsParent:!0})]},e.relevance=0,delete i.beforeMatch},Dt=[`of`,`and`,`for`,`in`,`not`,`or`,`if`,`then`,`parent`,`list`,`value`],vt=`keyword`;function Be(e,t,i=vt){let u=Object.create(null);return typeof e==`string`?b(i,e.split(` `)):Array.isArray(e)?b(i,e):Object.keys(e).forEach(function(_){Object.assign(u,Be(e[_],t,_))}),u;function b(_,c){t&&(c=c.map(r=>r.toLowerCase())),c.forEach(function(r){let l=r.split(`|`);u[l[0]]=[_,Ct(l[0],l[1])]})}}function Ct(e,t){return t?Number(t):Lt(e)?0:1}function Lt(e){return Dt.includes(e.toLowerCase())}var xe={},v=e=>{console.error(e)},Me=(e,...t)=>{console.log(`WARN: ${e}`,...t)},L=(e,t)=>{xe[`${e}/${t}`]||(console.log(`Deprecated as of ${e}. ${t}`),xe[`${e}/${t}`]=!0)},Y=new Error;function De(e,t,{key:i}){let u=0,b=e[i],_={},c={};for(let r=1;r<=t.length;r++)c[r+u]=b[r],_[r+u]=!0,u+=Ne(t[r-1]);e[i]=c,e[i]._emit=_,e[i]._multi=!0}function Pt(e){if(Array.isArray(e.begin)){if(e.skip||e.excludeBegin||e.returnBegin)throw v(`skip, excludeBegin, returnBegin not compatible with beginScope: {}`),Y;if(typeof e.beginScope!=`object`||e.beginScope===null)throw v(`beginScope must be object`),Y;De(e,e.begin,{key:`beginScope`}),e.begin=oe(e.begin,{joinWith:``})}}function jt(e){if(Array.isArray(e.end)){if(e.skip||e.excludeEnd||e.returnEnd)throw v(`skip, excludeEnd, returnEnd not compatible with endScope: {}`),Y;if(typeof e.endScope!=`object`||e.endScope===null)throw v(`endScope must be object`),Y;De(e,e.end,{key:`endScope`}),e.end=oe(e.end,{joinWith:``})}}function Ht(e){e.scope&&typeof e.scope==`object`&&e.scope!==null&&(e.beginScope=e.scope,delete e.scope)}function Ut(e){Ht(e),typeof e.beginScope==`string`&&(e.beginScope={_wrap:e.beginScope}),typeof e.endScope==`string`&&(e.endScope={_wrap:e.endScope}),Pt(e),jt(e)}function $t(e){function t(c,r){return new RegExp(H(c),`m`+(e.case_insensitive?`i`:``)+(e.unicodeRegex?`u`:``)+(r?`g`:``))}class i{constructor(){this.matchIndexes={},this.regexes=[],this.matchAt=1,this.position=0}addRule(r,l){l.position=this.position++,this.matchIndexes[this.matchAt]=l,this.regexes.push([l,r]),this.matchAt+=Ne(r)+1}compile(){this.regexes.length===0&&(this.exec=()=>null);let r=this.regexes.map(l=>l[1]);this.matcherRe=t(oe(r,{joinWith:`|`}),!0),this.lastIndex=0}exec(r){this.matcherRe.lastIndex=this.lastIndex;let l=this.matcherRe.exec(r);if(!l)return null;let w=l.findIndex((j,V)=>V>0&&j!==void 0),x=this.matchIndexes[w];return l.splice(0,w),Object.assign(l,x)}}class u{constructor(){this.rules=[],this.multiRegexes=[],this.count=0,this.lastIndex=0,this.regexIndex=0}getMatcher(r){if(this.multiRegexes[r])return this.multiRegexes[r];let l=new i;return this.rules.slice(r).forEach(([w,x])=>l.addRule(w,x)),l.compile(),this.multiRegexes[r]=l,l}resumingScanAtSamePosition(){return this.regexIndex!==0}considerAll(){this.regexIndex=0}addRule(r,l){this.rules.push([r,l]),l.type===`begin`&&this.count++}exec(r){let l=this.getMatcher(this.regexIndex);l.lastIndex=this.lastIndex;let w=l.exec(r);if(this.resumingScanAtSamePosition()&&!(w&&w.index===this.lastIndex)){let x=this.getMatcher(0);x.lastIndex=this.lastIndex+1,w=x.exec(r)}return w&&(this.regexIndex+=w.position+1,this.regexIndex===this.count&&this.considerAll()),w}}function b(c){let r=new u;return c.contains.forEach(l=>r.addRule(l.begin,{rule:l,type:`begin`})),c.terminatorEnd&&r.addRule(c.terminatorEnd,{type:`end`}),c.illegal&&r.addRule(c.illegal,{type:`illegal`}),r}function _(c,r){let l=c;if(c.isCompiled)return l;[Nt,It,Ut,Bt].forEach(x=>x(c,r)),e.compilerExtensions.forEach(x=>x(c,r)),c.__beforeBegin=null,[At,kt,Tt].forEach(x=>x(c,r)),c.isCompiled=!0;let w=null;return typeof c.keywords==`object`&&c.keywords.$pattern&&(c.keywords=Object.assign({},c.keywords),w=c.keywords.$pattern,delete c.keywords.$pattern),w=w||/\w+/,c.keywords&&(c.keywords=Be(c.keywords,e.case_insensitive)),l.keywordPatternRe=t(w,!0),r&&(c.begin||(c.begin=/\B|\b/),l.beginRe=t(l.begin),!c.end&&!c.endsWithParent&&(c.end=/\B|\b/),c.end&&(l.endRe=t(l.end)),l.terminatorEnd=H(l.end)||``,c.endsWithParent&&r.terminatorEnd&&(l.terminatorEnd+=(c.end?`|`:``)+r.terminatorEnd)),c.illegal&&(l.illegalRe=t(c.illegal)),c.contains||(c.contains=[]),c.contains=[].concat(...c.contains.map(function(x){return Gt(x===`self`?c:x)})),c.contains.forEach(function(x){_(x,l)}),c.starts&&_(c.starts,r),l.matcher=b(l),l}if(e.compilerExtensions||(e.compilerExtensions=[]),e.contains&&e.contains.includes(`self`))throw new Error("ERR: contains `self` is not supported at the top-level of a language.  See documentation.");return e.classNameAliases=B(e.classNameAliases||{}),_(e)}function ve(e){return e?e.endsWithParent||ve(e.starts):!1}function Gt(e){return e.variants&&!e.cachedVariants&&(e.cachedVariants=e.variants.map(function(t){return B(e,{variants:null},t)})),e.cachedVariants?e.cachedVariants:ve(e)?B(e,{starts:e.starts?B(e.starts):null}):Object.isFrozen(e)?B(e):e}var Wt=`11.12.0`,ce=class extends Error{constructor(t,i){super(t),this.name=`HTMLInjectionError`,this.html=i}},ne=Re,we=B,Oe=Symbol(`nomatch`),Kt=7,Ce=function(e){let t=Object.create(null),i=Object.create(null),u=[],b=!0,_=`Could not find the language '{}', did you forget to load/include a language module?`,c={disableAutodetect:!0,name:`Plain text`,contains:[]},r={ignoreUnescapedHTML:!1,throwUnescapedHTML:!1,noHighlightRe:/^(no-?highlight)$/i,languageDetectRe:/\blang(?:uage)?-([\w-]+)\b/i,classPrefix:`hljs-`,cssSelector:`pre code`,languages:null,__emitter:re};function l(n){return r.noHighlightRe.test(n)}function w(n){let a=n.className+` `;a+=n.parentNode?n.parentNode.className:``;let h=r.languageDetectRe.exec(a);if(h){let d=I(h[1]);return d||(Me(_.replace(`{}`,h[1])),Me(`Falling back to no-highlight mode for this block.`,n)),d?h[1]:`no-highlight`}return a.split(/\s+/).find(d=>l(d)||I(d))}function x(n,a,h){let d=``,M=``;typeof a==`object`?(d=n,h=a.ignoreIllegals,M=a.language):(L(`10.7.0`,`highlight(lang, code, ...args) has been deprecated.`),L(`10.7.0`,`Please use highlight(code, options) instead.
https://github.com/highlightjs/highlight.js/issues/2277`),M=n,d=a),h===void 0&&(h=!0);let S={code:d,language:M};G(`before:highlight`,S);let T=S.result?S.result:j(S.language,S.code,h);return T.code=S.code,G(`after:highlight`,T),T}function j(n,a,h,d){let M=Object.create(null);function S(s,o){return s.keywords[o]}function T(){if(!f.keywords){O.addText(E);return}let s=0;f.keywordPatternRe.lastIndex=0;let o=f.keywordPatternRe.exec(E),g=``;for(;o;){g+=E.substring(s,o.index);let p=A.case_insensitive?o[0].toLowerCase():o[0],y=S(f,p);if(y){let[k,Qe]=y;if(O.addText(g),g=``,M[p]=(M[p]||0)+1,M[p]<=Kt&&(z+=Qe),k.startsWith(`_`))g+=o[0];else{let me=A.classNameAliases[k]||k;N(o[0],me)}}else g+=o[0];s=f.keywordPatternRe.lastIndex,o=f.keywordPatternRe.exec(E)}g+=E.substring(s),O.addText(g)}function W(){if(E===``)return;let s=null;if(typeof f.subLanguage==`string`){if(!t[f.subLanguage]){O.addText(E);return}s=j(f.subLanguage,E,!0,Ee[f.subLanguage]),Ee[f.subLanguage]=s._top}else s=q(E,f.subLanguage.length?f.subLanguage:null);f.relevance>0&&(z+=s.relevance),O.__addSublanguage(s._emitter,s.language)}function R(){f.subLanguage!=null?W():T(),E=``}function N(s,o){s!==``&&(O.startScope(o),O.addText(s),O.endScope())}function ge(s,o){let g=1,p=o.length-1;for(;g<=p;){if(!s._emit[g]){g++;continue}let y=A.classNameAliases[s[g]]||s[g],k=o[g];y?N(k,y):(E=k,T(),E=``),g++}}function he(s,o){return s.scope&&typeof s.scope==`string`&&O.openNode(A.classNameAliases[s.scope]||s.scope),s.beginScope&&(s.beginScope._wrap?(N(E,A.classNameAliases[s.beginScope._wrap]||s.beginScope._wrap),E=``):s.beginScope._multi&&(ge(s.beginScope,o),E=``)),f=Object.create(s,{parent:{value:f}}),f}function pe(s,o,g){let p=ct(s.endRe,g);if(p){if(s[`on:end`]){let y=new X(s);s[`on:end`](o,y),y.isMatchIgnored&&(p=!1)}if(p){for(;s.endsParent&&s.parent;)s=s.parent;return s}}if(s.endsWithParent)return pe(s.parent,o,g)}function Ye(s){return f.matcher.regexIndex===0?(E+=s[0],1):(te=!0,0)}function Ze(s){let o=s[0],g=s.rule,p=new X(g),y=[g.__beforeBegin,g[`on:begin`]];for(let k of y)if(k&&(k(s,p),p.isMatchIgnored))return Ye(o);return g.skip?E+=o:(g.excludeBegin&&(E+=o),R(),!g.returnBegin&&!g.excludeBegin&&(E=o)),he(g,s),g.returnBegin?0:o.length}function Je(s){let o=s[0],g=a.substring(s.index),p=pe(f,s,g);if(!p)return Oe;let y=f;f.endScope&&f.endScope._wrap?(R(),N(o,f.endScope._wrap)):f.endScope&&f.endScope._multi?(R(),ge(f.endScope,s)):y.skip?E+=o:(y.returnEnd||y.excludeEnd||(E+=o),R(),y.excludeEnd&&(E=o));do f.scope&&O.closeNode(),!f.skip&&!f.subLanguage&&(z+=f.relevance),f=f.parent;while(f!==p.parent);return p.starts&&he(p.starts,s),y.returnEnd?0:o.length}function Ve(){let s=[];for(let o=f;o!==A;o=o.parent)o.scope&&s.unshift(o.scope);s.forEach(o=>O.openNode(o))}let K={};function de(s,o){let g=o&&o[0];if(E+=s,g==null)return R(),0;if(K.type===`begin`&&o.type===`end`&&K.index===o.index&&g===``){if(E+=a.slice(o.index,o.index+1),!b){let p=new Error(`0 width match regex (${n})`);throw p.languageName=n,p.badRule=K.rule,p}return 1}if(K=o,o.type===`begin`)return Ze(o);if(o.type===`illegal`&&!h){let p=new Error(`Illegal lexeme "`+g+`" for mode "`+(f.scope||`<unnamed>`)+`"`);throw p.mode=f,p}else if(o.type===`end`){let p=Je(o);if(p!==Oe)return p}if(o.type===`illegal`&&g===``)return o.index===a.length||(E+=`
`),1;if(ee>1e5&&ee>o.index*3)throw new Error(`potential infinite loop, way more iterations than matches`);return E+=g,g.length}let A=I(n);if(!A)throw v(_.replace(`{}`,n)),new Error(`Unknown language: "`+n+`"`);let qe=$t(A),m=``,f=d||qe,Ee={},O=new r.__emitter(r);Ve();let E=``,z=0,D=0,ee=0,te=!1;try{if(A.__emitTokens)A.__emitTokens(a,O);else{for(f.matcher.considerAll();;){ee++,te?te=!1:f.matcher.considerAll(),f.matcher.lastIndex=D;let s=f.matcher.exec(a);if(!s)break;let g=de(a.substring(D,s.index),s);D=s.index+g}de(a.substring(D))}return O.finalize(),m=O.toHTML(),{language:n,value:m,relevance:z,illegal:!1,_emitter:O,_top:f}}catch(s){if(s.message&&s.message.includes(`Illegal`))return{language:n,value:ne(a),illegal:!0,relevance:0,_illegalBy:{message:s.message,index:D,context:a.slice(D-100,D+100),mode:s.mode,resultSoFar:m},_emitter:O};if(b)return{language:n,value:ne(a),illegal:!1,relevance:0,errorRaised:s,_emitter:O,_top:f};throw s}}function V(n){let a={value:ne(n),illegal:!1,relevance:0,_top:c,_emitter:new r.__emitter(r)};return a._emitter.addText(n),a}function q(n,a){a=a||r.languages||Object.keys(t);let h=V(n),d=a.filter(I).filter(fe).map(R=>j(R,n,!1));d.unshift(h);let[S,T]=d.sort((R,N)=>{if(R.relevance!==N.relevance)return N.relevance-R.relevance;if(R.language&&N.language){if(I(R.language).supersetOf===N.language)return 1;if(I(N.language).supersetOf===R.language)return-1}return 0}),W=S;return W.secondBest=T,W}function Pe(n,a,h){let d=a&&i[a]||h;n.classList.add(`hljs`),n.classList.add(`language-${d}`)}function Q(n){let a=null,h=w(n);if(l(h))return;if(G(`before:highlightElement`,{el:n,language:h}),n.dataset.highlighted){console.log("Element previously highlighted. To highlight again, first unset `dataset.highlighted`.",n);return}if(n.children.length>0&&(r.ignoreUnescapedHTML||(console.warn(`One of your code blocks includes unescaped HTML. This is a potentially serious security risk.`),console.warn(`https://github.com/highlightjs/highlight.js/wiki/security`),console.warn(`The element with unescaped HTML:`),console.warn(n)),r.throwUnescapedHTML))throw new ce(`One of your code blocks includes unescaped HTML.`,n.innerHTML);a=n;let d=a.textContent,M=h?x(d,{language:h,ignoreIllegals:!0}):q(d);n.innerHTML=M.value,n.dataset.highlighted=`yes`,Pe(n,h,M.language),n.result={language:M.language,re:M.relevance,relevance:M.relevance},M.secondBest&&(n.secondBest={language:M.secondBest.language,relevance:M.secondBest.relevance}),G(`after:highlightElement`,{el:n,result:M,text:d})}function je(n){r=we(r,n)}let He=()=>{$(),L(`10.6.0`,`initHighlighting() deprecated.  Use highlightAll() now.`)};function Ue(){$(),L(`10.6.0`,`initHighlightingOnLoad() deprecated.  Use highlightAll() now.`)}let le=!1;function $(){function n(){$()}if(document.readyState===`loading`){le||window.addEventListener(`DOMContentLoaded`,n,!1),le=!0;return}document.querySelectorAll(r.cssSelector).forEach(Q)}function $e(n,a){let h=null;try{h=a(e)}catch(d){if(v(`Language definition for '{}' could not be registered.`.replace(`{}`,n)),b)v(d);else throw d;h=c}h.name||(h.name=n),t[n]=h,h.rawDefinition=a.bind(null,e),h.aliases&&ue(h.aliases,{languageName:n})}function Ge(n){delete t[n];for(let a of Object.keys(i))i[a]===n&&delete i[a]}function We(){return Object.keys(t)}function I(n){return n=(n||``).toLowerCase(),t[n]||t[i[n]]}function ue(n,{languageName:a}){typeof n==`string`&&(n=[n]),n.forEach(h=>{i[h.toLowerCase()]=a})}function fe(n){let a=I(n);return a&&!a.disableAutodetect}function Ke(n){n[`before:highlightBlock`]&&!n[`before:highlightElement`]&&(n[`before:highlightElement`]=a=>{n[`before:highlightBlock`](Object.assign({block:a.el},a))}),n[`after:highlightBlock`]&&!n[`after:highlightElement`]&&(n[`after:highlightElement`]=a=>{n[`after:highlightBlock`](Object.assign({block:a.el},a))})}function ze(n){Ke(n),u.push(n)}function Fe(n){let a=u.indexOf(n);a!==-1&&u.splice(a,1)}function G(n,a){let h=n;u.forEach(function(d){d[h]&&d[h](a)})}function Xe(n){return L(`10.7.0`,`highlightBlock will be removed entirely in v12.0`),L(`10.7.0`,`Please use highlightElement now.`),Q(n)}Object.assign(e,{highlight:x,highlightAuto:q,highlightAll:$,highlightElement:Q,highlightBlock:Xe,configure:je,initHighlighting:He,initHighlightingOnLoad:Ue,registerLanguage:$e,unregisterLanguage:Ge,listLanguages:We,getLanguage:I,registerAliases:ue,autoDetection:fe,inherit:we,addPlugin:ze,removePlugin:Fe}),e.debugMode=function(){b=!1},e.safeMode=function(){b=!0},e.versionString=Wt,e.regex={concat:C,lookahead:Se,either:Z,optional:st,anyNumberOfTimes:it};for(let n in F)typeof F[n]==`object`&&ye(F[n]);return Object.assign(e,F),e},P=Ce({});P.newInstance=()=>Ce({});Le.exports=P;P.HighlightJS=P;P.default=P});var I={attribute:!0,type:String,converter:V$1,reflect:!1,hasChanged:st};var Q=(e=I,r,t)=>{let{kind:i,metadata:n}=t,o=globalThis.litPropertyMetadata.get(n);if(o===void 0&&globalThis.litPropertyMetadata.set(n,o=new Map),i===`setter`&&((e=Object.create(e)).wrapped=!0),o.set(t.name,e),i===`accessor`){let{name:a}=t;return{set(c){let b=r.get.call(this);r.set.call(this,c),this.requestUpdate(a,b,e,!0,c)},init(c){return c!==void 0&&this.C(a,void 0,e,c),c}}}if(i===`setter`){let{name:a}=t;return function(c){let b=this[a];r.call(this,c),this.requestUpdate(a,b,e,!0,c)}}throw Error(`Unsupported decorator location: `+i)};function p(e){return(r,t)=>typeof t==`object`?Q(e,r,t):((i,n,o)=>{let a=n.hasOwnProperty(o);return n.constructor.createProperty(o,i),a?Object.getOwnPropertyDescriptor(n,o):void 0})(e,r,t)}function m(e){return p(s$1(r({},e),{state:!0,attribute:!1}))}var g=v(zt(),1).default;var W=X$1(`@charset "UTF-8";
:host {
  --mp-code-bg: light-dark(#fefefe, #2b2b2b);
  --mp-code-fg: light-dark(#545454, #f8f8f2);
  --mp-code-comment: light-dark(#696969, #d4d0ab);
  --mp-code-red: light-dark(#d91e18, #ffa07a);
  --mp-code-orange: light-dark(#aa5d00, #f5ab35);
  --mp-code-yellow: light-dark(#7c4a03, #ffd700);
  --mp-code-green: light-dark(#008000, #abe338);
  --mp-code-blue: light-dark(#007faa, #00e0e0);
  --mp-code-purple: light-dark(#7928a1, #dcc6e0);
  --mp-code-punctuation: light-dark(#6a6a66, #c8c8c2);
}

/* Comment */
.hljs-comment,
.hljs-quote {
  color: var(--mp-code-comment);
}

/* Red */
.hljs-variable,
.hljs-template-variable,
.hljs-tag,
.hljs-name,
.hljs-selector-id,
.hljs-selector-class,
.hljs-regexp,
.hljs-deletion {
  color: var(--mp-code-red);
}

/* Orange */
.hljs-number,
.hljs-built_in,
.hljs-literal,
.hljs-type,
.hljs-params,
.hljs-meta,
.hljs-link {
  color: var(--mp-code-orange);
}

/* Yellow */
.hljs-attribute,
.hljs-attr {
  color: var(--mp-code-yellow);
}

/* Green */
.hljs-string,
.hljs-symbol,
.hljs-bullet,
.hljs-addition {
  color: var(--mp-code-green);
}

/* Muted (JSON punctuation \u2014 braces, brackets, commas, colons) */
.hljs-punctuation {
  color: var(--mp-code-punctuation);
}

/* Blue */
.hljs-title,
.hljs-section {
  color: var(--mp-code-blue);
}

/* Purple */
.hljs-keyword,
.hljs-selector-tag {
  color: var(--mp-code-purple);
}

.hljs-emphasis {
  font-style: italic;
}

.hljs-strong {
  font-weight: bold;
}`);var Y=X$1(`:host {
  display: flex;
  flex-direction: column;
  position: relative;
  font-family: var(--bs-font-monospace);
  background: var(--mp-code-bg);
  color: var(--mp-code-fg);
  border: 1px solid var(--bs-border-color);
  border-radius: var(--bs-border-radius);
  overflow: hidden;
}

:host([theme=light]) {
  color-scheme: only light;
}

:host([theme=dark]) {
  color-scheme: only dark;
}

pre {
  margin: 0;
  padding: 2.25rem 0 1rem;
  overflow: auto;
  flex: 1 1 auto;
  min-height: 0;
  tab-size: 2;
  direction: ltr;
}

code {
  display: grid;
  grid-template-columns: auto auto auto 1fr;
  font-family: inherit;
  font-size: 0.875rem;
  line-height: 1.5;
  color: inherit;
  min-width: max-content;
}

.line {
  display: grid;
  grid-template-columns: subgrid;
  grid-column: 1/-1;
  white-space: normal;
  min-height: 1.5em;
}

.line-text {
  grid-column: 4;
  white-space: pre;
  padding-inline: 1rem 0;
}

.line-number {
  grid-column: 1;
  box-sizing: content-box;
  min-width: 2.5ch;
  padding-inline: 0.375rem;
  text-align: right;
  user-select: none;
  opacity: 0.55;
  position: sticky;
  left: 0;
  background: var(--mp-code-bg);
}

.line.active {
  outline: 2px solid var(--bs-primary);
  outline-offset: -2px;
}

.line-mark {
  padding-inline-start: 0.375rem;
  text-align: right;
  user-select: none;
  font-size: 0.85em;
  font-variant-numeric: tabular-nums;
  opacity: 0.75;
}

.line-mark:not(.secondary) {
  grid-column: 2;
}

.line-mark.secondary {
  grid-column: 3;
  padding-inline-end: 0.375rem;
  color: var(--mp-code-mark-secondary, var(--mp-code-yellow));
  opacity: 0.9;
}

a.line-number {
  color: inherit;
  text-decoration: none;
  cursor: pointer;
}

a.line-number:hover {
  text-decoration: underline;
  opacity: 1;
}

a.line-number:focus-visible {
  outline: 2px solid var(--bs-primary);
  outline-offset: -2px;
  opacity: 1;
}

:host([wrap]) code {
  min-width: 0;
}
:host([wrap]) .line-text {
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.copy {
  position: absolute;
  top: 0.5rem;
  right: 0.5rem;
  padding: 0.25rem 0.75rem;
  font-size: 0.75rem;
  color: var(--bs-body-color);
  background: var(--bs-body-bg);
  border: 1px solid var(--bs-border-color);
  border-radius: var(--bs-border-radius-sm);
  cursor: pointer;
  opacity: 0.85;
  transition: opacity 120ms ease;
}

.copy:hover,
.copy:focus-visible {
  opacity: 1;
}

.toast {
  position: absolute;
  bottom: 0.5rem;
  right: 0.5rem;
  padding: 0.25rem 0.75rem;
  font-size: 0.75rem;
  color: var(--bs-body-bg);
  background: var(--bs-success);
  border-radius: var(--bs-border-radius-sm);
  opacity: 0;
  transform: translateY(0.5rem);
  transition: opacity 150ms ease, transform 150ms ease;
  pointer-events: none;
  user-select: none;
}

.toast.visible {
  opacity: 1;
  transform: translateY(0);
}

@media (prefers-reduced-motion: reduce) {
  .copy,
  .toast {
    transition: none;
  }
}
.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  padding: 0;
  margin: -1px;
  overflow: hidden;
  clip: rect(0, 0, 0, 0);
  white-space: nowrap;
  border: 0;
  user-select: none;
}

slot {
  display: none;
}`);function J(e){return e.replace(/\r\n?/g,`
`).replace(/\n$/,``)}function X(e){return e.replace(/&/g,`&amp;`).replace(/</g,`&lt;`).replace(/>/g,`&gt;`).replace(/"/g,`&quot;`).replace(/'/g,`&#x27;`)}function U(e){let r=[],t=[],i=``,n=0;for(;n<e.length;){let o=e[n];if(o===`<`){let a=e.indexOf(`>`,n);if(a===-1){i+=e.slice(n);break}let c=e.slice(n,a+1);c[1]===`/`?t.pop():e[a-1]!==`/`&&t.push(c),i+=c,n=a+1}else o===`
`?(r.push(i+`</span>`.repeat(t.length)),i=t.join(``),n++):(i+=o,n++)}return r.push(i+`</span>`.repeat(t.length)),r}var V={atom:()=>import(`./chunk-Bs3bRvME.js`).then(e=>e.default),bash:()=>import(`./chunk-D7m0bTs2.js`).then(e=>e.default),c:()=>import(`./chunk-BULbnLhR.js`).then(e=>e.default),"c#":()=>import(`./chunk-CLzukW7I.js`).then(e=>e.default),"c++":()=>import(`./chunk-D-wqBk7r2.js`).then(e=>e.default),cc:()=>import(`./chunk-D-wqBk7r2.js`).then(e=>e.default),cjs:()=>import(`./chunk-C_y_Wcmj.js`).then(e=>e.default),console:()=>import(`./chunk-C0UvtsFb.js`).then(e=>e.default),cpp:()=>import(`./chunk-D-wqBk7r2.js`).then(e=>e.default),cs:()=>import(`./chunk-CLzukW7I.js`).then(e=>e.default),csharp:()=>import(`./chunk-CLzukW7I.js`).then(e=>e.default),css:()=>import(`./chunk-Da0ZEEu52.js`).then(e=>e.default),cts:()=>import(`./chunk-Da6TFjtL2.js`).then(e=>e.default),cxx:()=>import(`./chunk-D-wqBk7r2.js`).then(e=>e.default),diff:()=>import(`./chunk-CK_McuBd.js`).then(e=>e.default),gemspec:()=>import(`./chunk-DwP8C_H7.js`).then(e=>e.default),go:()=>import(`./chunk-Bji-1gB02.js`).then(e=>e.default),golang:()=>import(`./chunk-Bji-1gB02.js`).then(e=>e.default),gql:()=>import(`./chunk-CMY6sUet.js`).then(e=>e.default),graphql:()=>import(`./chunk-CMY6sUet.js`).then(e=>e.default),gyp:()=>import(`./chunk-DQ1Fm0JY.js`).then(e=>e.default),h:()=>import(`./chunk-BULbnLhR.js`).then(e=>e.default),"h++":()=>import(`./chunk-D-wqBk7r2.js`).then(e=>e.default),hh:()=>import(`./chunk-D-wqBk7r2.js`).then(e=>e.default),hpp:()=>import(`./chunk-D-wqBk7r2.js`).then(e=>e.default),html:()=>import(`./chunk-Bs3bRvME.js`).then(e=>e.default),hxx:()=>import(`./chunk-D-wqBk7r2.js`).then(e=>e.default),ini:()=>import(`./chunk-DgljKqVb2.js`).then(e=>e.default),ipython:()=>import(`./chunk-DQ1Fm0JY.js`).then(e=>e.default),irb:()=>import(`./chunk-DwP8C_H7.js`).then(e=>e.default),java:()=>import(`./chunk-Dp3CwsL_2.js`).then(e=>e.default),javascript:()=>import(`./chunk-C_y_Wcmj.js`).then(e=>e.default),js:()=>import(`./chunk-C_y_Wcmj.js`).then(e=>e.default),json:()=>import(`./chunk-BGC2jwKU.js`).then(e=>e.default),jsonc:()=>import(`./chunk-BGC2jwKU.js`).then(e=>e.default),jsp:()=>import(`./chunk-Dp3CwsL_2.js`).then(e=>e.default),jsx:()=>import(`./chunk-C_y_Wcmj.js`).then(e=>e.default),kotlin:()=>import(`./chunk-DAjYWiMd.js`).then(e=>e.default),kt:()=>import(`./chunk-DAjYWiMd.js`).then(e=>e.default),kts:()=>import(`./chunk-DAjYWiMd.js`).then(e=>e.default),less:()=>import(`./chunk-Ca8kKmnR2.js`).then(e=>e.default),lua:()=>import(`./chunk-BDS_7c7u.js`).then(e=>e.default),mak:()=>import(`./chunk-DSzvEjzy.js`).then(e=>e.default),make:()=>import(`./chunk-DSzvEjzy.js`).then(e=>e.default),makefile:()=>import(`./chunk-DSzvEjzy.js`).then(e=>e.default),markdown:()=>import(`./chunk-BfqOEc9H.js`).then(e=>e.default),md:()=>import(`./chunk-BfqOEc9H.js`).then(e=>e.default),mjs:()=>import(`./chunk-C_y_Wcmj.js`).then(e=>e.default),mk:()=>import(`./chunk-DSzvEjzy.js`).then(e=>e.default),mkd:()=>import(`./chunk-BfqOEc9H.js`).then(e=>e.default),mkdown:()=>import(`./chunk-BfqOEc9H.js`).then(e=>e.default),mm:()=>import(`./chunk-BlCN4l4z.js`).then(e=>e.default),mts:()=>import(`./chunk-Da6TFjtL2.js`).then(e=>e.default),"obj-c":()=>import(`./chunk-BlCN4l4z.js`).then(e=>e.default),"obj-c++":()=>import(`./chunk-BlCN4l4z.js`).then(e=>e.default),objc:()=>import(`./chunk-BlCN4l4z.js`).then(e=>e.default),"objective-c++":()=>import(`./chunk-BlCN4l4z.js`).then(e=>e.default),objectivec:()=>import(`./chunk-BlCN4l4z.js`).then(e=>e.default),patch:()=>import(`./chunk-CK_McuBd.js`).then(e=>e.default),perl:()=>import(`./chunk-CCP8t9Qc2.js`).then(e=>e.default),php:()=>import(`./chunk-DSjfVx_M.js`).then(e=>e.default),"php-template":()=>import(`./chunk-BFbCOJrv.js`).then(e=>e.default),pl:()=>import(`./chunk-CCP8t9Qc2.js`).then(e=>e.default),plaintext:()=>import(`./chunk-CrAyGtzd.js`).then(e=>e.default),plist:()=>import(`./chunk-Bs3bRvME.js`).then(e=>e.default),pluto:()=>import(`./chunk-BDS_7c7u.js`).then(e=>e.default),pm:()=>import(`./chunk-CCP8t9Qc2.js`).then(e=>e.default),podspec:()=>import(`./chunk-DwP8C_H7.js`).then(e=>e.default),py:()=>import(`./chunk-DQ1Fm0JY.js`).then(e=>e.default),pycon:()=>import(`./chunk-eV-dslwR.js`).then(e=>e.default),python:()=>import(`./chunk-DQ1Fm0JY.js`).then(e=>e.default),"python-repl":()=>import(`./chunk-eV-dslwR.js`).then(e=>e.default),r:()=>import(`./chunk-DMygRDnR2.js`).then(e=>e.default),rb:()=>import(`./chunk-DwP8C_H7.js`).then(e=>e.default),rs:()=>import(`./chunk-C6r1WTqv2.js`).then(e=>e.default),rss:()=>import(`./chunk-Bs3bRvME.js`).then(e=>e.default),ruby:()=>import(`./chunk-DwP8C_H7.js`).then(e=>e.default),rust:()=>import(`./chunk-C6r1WTqv2.js`).then(e=>e.default),scss:()=>import(`./chunk--Sjxt33j.js`).then(e=>e.default),sh:()=>import(`./chunk-D7m0bTs2.js`).then(e=>e.default),shell:()=>import(`./chunk-C0UvtsFb.js`).then(e=>e.default),shellsession:()=>import(`./chunk-C0UvtsFb.js`).then(e=>e.default),sql:()=>import(`./chunk-BFcwdEzL2.js`).then(e=>e.default),svg:()=>import(`./chunk-Bs3bRvME.js`).then(e=>e.default),swift:()=>import(`./chunk-B_RrdtNn.js`).then(e=>e.default),text:()=>import(`./chunk-CrAyGtzd.js`).then(e=>e.default),thor:()=>import(`./chunk-DwP8C_H7.js`).then(e=>e.default),toml:()=>import(`./chunk-DgljKqVb2.js`).then(e=>e.default),ts:()=>import(`./chunk-Da6TFjtL2.js`).then(e=>e.default),tsx:()=>import(`./chunk-Da6TFjtL2.js`).then(e=>e.default),txt:()=>import(`./chunk-CrAyGtzd.js`).then(e=>e.default),typescript:()=>import(`./chunk-Da6TFjtL2.js`).then(e=>e.default),vb:()=>import(`./chunk-BOg_rkWC.js`).then(e=>e.default),vbnet:()=>import(`./chunk-BOg_rkWC.js`).then(e=>e.default),wasm:()=>import(`./chunk-BEamE-yO.js`).then(e=>e.default),wsf:()=>import(`./chunk-Bs3bRvME.js`).then(e=>e.default),xhtml:()=>import(`./chunk-Bs3bRvME.js`).then(e=>e.default),xjb:()=>import(`./chunk-Bs3bRvME.js`).then(e=>e.default),xml:()=>import(`./chunk-Bs3bRvME.js`).then(e=>e.default),xsd:()=>import(`./chunk-Bs3bRvME.js`).then(e=>e.default),xsl:()=>import(`./chunk-Bs3bRvME.js`).then(e=>e.default),yaml:()=>import(`./chunk-5cKXGqGf.js`).then(e=>e.default),yml:()=>import(`./chunk-5cKXGqGf.js`).then(e=>e.default),zsh:()=>import(`./chunk-D7m0bTs2.js`).then(e=>e.default)};var Z={atom:`xml`,bash:`bash`,c:`c`,"c#":`csharp`,"c++":`cpp`,cc:`cpp`,cjs:`javascript`,console:`shell`,cpp:`cpp`,cs:`csharp`,csharp:`csharp`,css:`css`,cts:`typescript`,cxx:`cpp`,diff:`diff`,gemspec:`ruby`,go:`go`,golang:`go`,gql:`graphql`,graphql:`graphql`,gyp:`python`,h:`c`,"h++":`cpp`,hh:`cpp`,hpp:`cpp`,html:`xml`,hxx:`cpp`,ini:`ini`,ipython:`python`,irb:`ruby`,java:`java`,javascript:`javascript`,js:`javascript`,json:`json`,jsonc:`json`,jsp:`java`,jsx:`javascript`,kotlin:`kotlin`,kt:`kotlin`,kts:`kotlin`,less:`less`,lua:`lua`,mak:`makefile`,make:`makefile`,makefile:`makefile`,markdown:`markdown`,md:`markdown`,mjs:`javascript`,mk:`makefile`,mkd:`markdown`,mkdown:`markdown`,mm:`objectivec`,mts:`typescript`,"obj-c":`objectivec`,"obj-c++":`objectivec`,objc:`objectivec`,"objective-c++":`objectivec`,objectivec:`objectivec`,patch:`diff`,perl:`perl`,php:`php`,"php-template":`php-template`,pl:`perl`,plaintext:`plaintext`,plist:`xml`,pluto:`lua`,pm:`perl`,podspec:`ruby`,py:`python`,pycon:`python-repl`,python:`python`,"python-repl":`python-repl`,r:`r`,rb:`ruby`,rs:`rust`,rss:`xml`,ruby:`ruby`,rust:`rust`,scss:`scss`,sh:`bash`,shell:`shell`,shellsession:`shell`,sql:`sql`,svg:`xml`,swift:`swift`,text:`plaintext`,thor:`ruby`,toml:`ini`,ts:`typescript`,tsx:`typescript`,txt:`plaintext`,typescript:`typescript`,vb:`vbnet`,vbnet:`vbnet`,wasm:`wasm`,wsf:`xml`,xhtml:`xml`,xjb:`xml`,xml:`xml`,xsd:`xml`,xsl:`xml`,yaml:`yaml`,yml:`yaml`,zsh:`bash`};var w=new Map;var F=e=>e in V;var x=e=>g.getLanguage(e)!=null;function Re(e){return x(e)||F(e)}async function ee(e){if(x(e))return`ready`;if(!F(e))return`unknown-language`;let r=Z[e],t=w.get(r);return t||(t=V[e]().then(i=>(x(r)||g.registerLanguage(r,i),!0)).catch(()=>(w.delete(r),!1)),w.set(r,t)),await t?`ready`:`load-failed`}var L=null;function te(){return L??=import(`./chunk-BlEUi2-E.js`).then(()=>!0).catch(()=>(L=null,!1)),L}async function ne(e,r){if(!e)return{value:``,language:null,load:`ready`};if(r){let i=await ee(r);if(i!==`ready`)return{value:``,language:null,load:i};let n=g.highlight(e,{language:r,ignoreIllegals:!0});return{value:n.value,language:n.language??r,load:`ready`}}if(!await te())return{value:``,language:null,load:`load-failed`};let t=g.highlightAuto(e);return{value:t.value,language:t.language??null,load:`ready`}}var ie=Object.defineProperty;var s=(e,r,t,i)=>{for(var n=void 0,o=e.length-1,a;o>=0;o--)(a=e[o])&&(n=a(r,t,n)||n);return n&&ie(r,t,n),n};var z=`mp-code-snippet`;var l=(()=>{class e extends b{constructor(){super(...arguments),this.language=``,this.theme=`auto`,this.copyLabel="Copy ${language} code to clipboard",this.code=``,this.lineNumbers=!1,this.startLine=1,this.wrap=!1,this.annotations=[],this.activeLine=null,this.lineHref=null,this.lineLabel="Line ${line}",this.label=``,this.regionLabel="${language} code sample",this.copiedLabel=`Copied!`,this.copiedAnnouncement=`Copied to clipboard`,this.keymapHint=`Use the up and down arrow keys to move between line links, Home and End for the first and last line.`,this.detectedLanguage=`code`,this.lines=[],this.toastVisible=!1,this.rovingLine=null,this.toastTimer=null,this.annotationsByLine=new Map,this.highlightToken=0,this.highlightPending=Promise.resolve()}static{this.styles=[W,Y]}get regionName(){return this.label||this.regionLabel.replace("${language}",this.detectedLanguage)}async getUpdateComplete(){return await super.getUpdateComplete(),await this.highlightPending,super.getUpdateComplete()}connectedCallback(){super.connectedCallback()}disconnectedCallback(){super.disconnectedCallback(),this.toastTimer!==null&&(clearTimeout(this.toastTimer),this.toastTimer=null)}willUpdate(t){(t.has(`code`)||t.has(`language`))&&this.runHighlight(),t.has(`annotations`)&&(this.annotationsByLine=new Map((this.annotations??[]).map(i=>[i.line,i])))}runHighlight(){let t=J(this.code??``);if(!t){this.lines=[],this.setDetectedLanguage(`code`);return}this.lines=U(X(t));let i=++this.highlightToken,n=this.language;this.highlightPending=ne(t,n).then(({value:o,language:a,load:c})=>{i===this.highlightToken&&(c===`unknown-language`?console.warn(`[mp-code-snippet] unknown language "${n}" \u2014 rendering as plain text. Register it with registerLanguage() if it is outside the bundled set.`):c===`load-failed`&&console.warn(`[mp-code-snippet] failed to load the grammar for "${n||`auto`}".`),o&&(this.lines=U(o)),this.setDetectedLanguage(a??`code`))})}setDetectedLanguage(t){t!==this.detectedLanguage&&(this.detectedLanguage=t,this.dispatchEvent(new CustomEvent(`language-detected`,{detail:{language:t},bubbles:!0,composed:!0})))}async handleCopy(){try{await navigator.clipboard.writeText(this.code??``),this.showToast()}catch(t){console.warn(`[mp-code-snippet] clipboard write failed`,t)}}showToast(){this.toastVisible=!0,this.toastTimer!==null&&clearTimeout(this.toastTimer),this.toastTimer=setTimeout(()=>{this.toastVisible=!1,this.toastTimer=null},3e3)}render(){return Ht`
      <slot @slotchange=${this.onSlotChange}></slot>
      <button
        type="button"
        class="copy"
        part="copy-button"
        @click=${this.handleCopy}
        aria-label="${this.copyLabel.replace("${language}",this.detectedLanguage)}"
      >Copy ${this.detectedLanguage}</button>
      <pre
        part="pre"
        tabindex="0"
        role="region"
        aria-label="${this.regionName}"
        aria-describedby="${this.lineHref?`keymap`:p$2}"
      ><code part="code" class="hljs">${Array.from({length:this.rowCount},(t,i)=>this.renderLine(i))}</code></pre>
      ${this.lineHref?Ht`<div id="keymap" class="sr-only">${this.keymapHint}</div>`:p$2}
      <div class="toast ${this.toastVisible?`visible`:``}" part="toast" aria-hidden="${!this.toastVisible}">${this.copiedLabel}</div>
      <div class="sr-only" role="status" aria-live="polite">${this.toastVisible?this.copiedAnnouncement:``}</div>
    `}get rowCount(){let t=this.annotations.reduce((n,o)=>Math.max(n,o.line),0),i=t===0?0:t-this.startLine+1;return Math.max(this.lines.length,i)}scrollToLine(t){this.renderRoot?.querySelector(`#L${t}`)?.scrollIntoView({block:`center`,behavior:`auto`})}resolveLineHref(t){return!t.startsWith(`#`)||typeof location>`u`?t:`${location.pathname}${location.search}${t}`}onLineActivate(t,i){i.button!==0||i.ctrlKey||i.metaKey||i.shiftKey||i.altKey||this.dispatchEvent(new CustomEvent(`line-activate`,{detail:{line:t},bubbles:!0,composed:!0,cancelable:!0}))||i.preventDefault()}renderLine(t){let i=this.startLine+t,n=this.annotationFor(i),o=this.activeLine===i,a=this.lineLabel.replace("${line}",String(i)),c=[`line`,n?.kind?`annotation-${n.kind}`:``,o?`active-line`:``].filter(Boolean).join(` `);return Ht`<span
      class="line${o?` active`:``}${n?` annotated`:``}"
      part="${c}"
      id="L${i}"
      title="${n?.description??p$2}"
      >${this.lineNumbers?this.renderGutter(i,a):p$2}${n?.label!==void 0?Ht`<span class="line-mark" part="line-mark" aria-hidden="true">${n.label}</span>`:p$2}${n?.secondaryLabel!==void 0?Ht`<span class="line-mark secondary" part="line-mark-secondary" aria-hidden="true"
            >${n.secondaryLabel}</span
          >`:p$2}<span class="line-text" part="line-text">${f(this.lines[t]??``)}</span
      >${n?.description?Ht`<span class="sr-only">${n.description}</span>`:p$2}</span
    >`}renderGutter(t,i){return this.lineHref?Ht`<a
      class="line-number"
      part="line-number"
      href="${this.resolveLineHref(this.lineHref(t))}"
      aria-label="${i}"
      tabindex="${t===this.tabbableLine?0:-1}"
      @click=${n=>this.onLineActivate(t,n)}
      @keydown=${n=>this.onGutterKeydown(t,n)}
      >${t}</a
    >`:Ht`<span class="line-number" part="line-number" aria-hidden="true">${t}</span>`}get tabbableLine(){return this.rovingLine??this.activeLine??this.startLine}onGutterKeydown(t,i){let n=this.startLine,o=this.startLine+this.rowCount-1,a=null;switch(i.key){case`ArrowDown`:a=Math.min(t+1,o);break;case`ArrowUp`:a=Math.max(t-1,n);break;case`Home`:a=n;break;case`End`:a=o;break;default:return}i.preventDefault(),a!==t&&(this.rovingLine=a,this.moveFocusToLine(a))}async moveFocusToLine(t){await this.updateComplete,this.renderRoot?.querySelector(`#L${t} a.line-number`)?.focus()}annotationFor(t){return this.annotationsByLine.get(t)}onSlotChange(t){if(this.code)return;let i=t.target.assignedNodes({flatten:!0}).map(n=>n.textContent??``).join(``).trim();i&&(this.code=i)}}return e})();s([p({type:String})],l.prototype,`language`);s([p({type:String,reflect:!0})],l.prototype,`theme`);s([p({type:String,attribute:`copy-label`})],l.prototype,`copyLabel`);s([p({type:String})],l.prototype,`code`);s([p({type:Boolean,attribute:`line-numbers`,reflect:!0})],l.prototype,`lineNumbers`);s([p({type:Number,attribute:`start-line`})],l.prototype,`startLine`);s([p({type:Boolean,reflect:!0})],l.prototype,`wrap`);s([p({attribute:!1})],l.prototype,`annotations`);s([p({type:Number,attribute:`active-line`})],l.prototype,`activeLine`);s([p({attribute:!1})],l.prototype,`lineHref`);s([p({type:String,attribute:`line-label`})],l.prototype,`lineLabel`);s([p({type:String})],l.prototype,`label`);s([p({type:String,attribute:`region-label`})],l.prototype,`regionLabel`);s([p({type:String,attribute:`copied-label`})],l.prototype,`copiedLabel`);s([p({type:String,attribute:`copied-announcement`})],l.prototype,`copiedAnnouncement`);s([p({type:String,attribute:`keymap-hint`})],l.prototype,`keymapHint`);s([m()],l.prototype,`detectedLanguage`);s([m()],l.prototype,`lines`);s([m()],l.prototype,`toastVisible`);s([m()],l.prototype,`rovingLine`);typeof customElements<`u`&&!customElements.get(z)&&customElements.define(z,l);var oe=[`element`];var ae=[`role`,`tabindex`,`id`];var Ie=(()=>{class e{constructor(){this.code=Ir(``),this.language=Ir(``),this.lineNumbers=Ir(!1),this.startLine=Ir(1),this.wrap=Ir(!1),this.theme=Ir(`auto`),this.annotations=Ir([]),this.activeLine=Ir(null),this.lineHref=Ir(null),this.label=Ir(``),this.copyLabel=Ir(``),this.lineLabel=Ir(``),this.detectedLanguage=p$(),this.lineActivate=p$(),this.element=m$.required(`element`),this.host=p$1(mt)}ngAfterViewInit(){this.forwardHostAttributes()}scrollToLine(t){this.element().nativeElement.scrollToLine(t)}forwardHostAttributes(){let t=this.host.nativeElement,i=this.element().nativeElement,n=[...t.getAttributeNames()].filter(o=>o.startsWith(`aria-`)||ae.includes(o));for(let o of n){let a=t.getAttribute(o);a!==null&&(i.setAttribute(o,a),t.removeAttribute(o))}}onLanguageDetected(t){this.detectedLanguage.emit(t.detail.language)}onLineActivate(t){this.lineActivate.emit(t)}static{this.ɵfac=function(i){return new(i||e)}}static{this.ɵcmp=xi({type:e,selectors:[[`bs-code-snippet`]],viewQuery:function(i,n){i&1&&Iy(n.element,oe,5),i&2&&JT()},inputs:{code:[1,`code`],language:[1,`language`],lineNumbers:[1,`lineNumbers`],startLine:[1,`startLine`],wrap:[1,`wrap`],theme:[1,`theme`],annotations:[1,`annotations`],activeLine:[1,`activeLine`],lineHref:[1,`lineHref`],label:[1,`label`],copyLabel:[1,`copyLabel`],lineLabel:[1,`lineLabel`]},outputs:{detectedLanguage:`detectedLanguage`,lineActivate:`lineActivate`},decls:2,vars:12,consts:[[`element`,``],[3,`language-detected`,`line-activate`,`code`,`annotations`,`lineHref`]],template:function(i,n){i&1&&(ki(0,`mp-code-snippet`,1,0),Dy(`language-detected`,function(a){return n.onLanguageDetected(a)})(`line-activate`,function(a){return n.onLineActivate(a)}),Pi()),i&2&&(vy(`code`,n.code())(`annotations`,n.annotations())(`lineHref`,n.lineHref()),gc(`language`,n.language()||null)(`line-numbers`,n.lineNumbers()?``:null)(`start-line`,n.startLine())(`wrap`,n.wrap()?``:null)(`theme`,n.theme())(`active-line`,n.activeLine())(`label`,n.label()||null)(`copy-label`,n.copyLabel()||null)(`line-label`,n.lineLabel()||null))},styles:[`[_nghost-%COMP%]{display:flex;flex-direction:column}mp-code-snippet[_ngcontent-%COMP%]{flex:1 1 auto;min-height:0}`]})}}return e})();export{Re as n,zt as r,Ie as t};