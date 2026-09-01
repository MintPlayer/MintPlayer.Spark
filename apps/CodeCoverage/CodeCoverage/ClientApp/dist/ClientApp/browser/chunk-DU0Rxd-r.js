import{n as s,t as r}from"./chunk-C9yOwMO6.js";import{$ as Ot$2,At as Z$1,Bt as be$3,Cn as oa,Ct as Vi,Dn as p$,Dt as Xa,En as p,Et as X$2,Fn as rn$1,Gt as en$1,I as JT,In as ro,Jn as w$,Kt as g$,Ln as si,Lt as aD,M as Ie$1,N as Ir,Nt as ZT,On as py,P as Iy,Pn as re$2,Pt as _,Qt as ii,R as Jt$1,Rn as so,Sn as ne$2,Tn as on$1,V as KT,Vn as uy,Vt as cn$1,W as Lf,Wn as vc,Wt as ee$2,X as Oi,Xn as wn,Z as On,Zn as wr,Zt as ic,ar as xi,bn as nE,bt as Um,cn as kw,cr as y$1,ct as QT,dr as z2,er as wy,et as Ow,fn as m$1,g as De$2,gn as me$1,h as D$,in as kb,kn as q$2,kt as YN,lr as y$,mn as mT,n as $C,nr as xT,o as A_,or as xn$1,pn as m$,pr as zl,q as OT,qn as vy,qt as gc,s as Af,t as $2,un as lo,ur as z$1,vn as mt$2,vt as Ty,w as G$1,wn as oe$2,z as K$2}from"./chunk-Btq1RDbg.js";import{n as _$1,r as s$1,t as T$2}from"./chunk-CjtvRoOK.js";import{a as X$3,c as b$1,d as y$2,l as p$1,r as Tt$3,t as Ht$2}from"./chunk-CwXwit_b.js";var $e$1=(()=>{class n{_renderer;_elementRef;onChange=e=>{};onTouched=()=>{};constructor(e,i){this._renderer=e,this._elementRef=i}setProperty(e,i){this._renderer.setProperty(this._elementRef.nativeElement,e,i)}registerOnTouched(e){this.onTouched=e}registerOnChange(e){this.onChange=e}setDisabledState(e){this.setProperty(`disabled`,e)}static ɵfac=function(i){return new(i||n)(X$2(Jt$1),X$2(mt$2))};static ɵdir=Ot$2({type:n})}return n})();var ne$1=(()=>{class n extends $e$1{static ɵfac=(()=>{let e;return function(r){return(e||(e=Um(n)))(r||n)}})();static ɵdir=Ot$2({type:n,features:[uy]})}return n})();var O$1=new y$1(``);var Dt$2={provide:O$1,useExisting:oa(()=>qe$1),multi:!0};function bt(){let n=on$1()?on$1().getUserAgent():``;return/android (\d+)/.test(n.toLowerCase())}var At$2=new y$1(``);var qe$1=(()=>{class n extends $e$1{_compositionMode;_composing=!1;constructor(e,i,r){super(e,i),this._compositionMode=r,this._compositionMode??=!bt()}writeValue(e){let i=e??``;this.setProperty(`value`,i)}_handleInput(e){(!this._compositionMode||this._compositionMode&&!this._composing)&&this.onChange(e)}_compositionStart(){this._composing=!0}_compositionEnd(e){this._composing=!1,this._compositionMode&&this.onChange(e)}static ɵfac=function(i){return new(i||n)(X$2(Jt$1),X$2(mt$2),X$2(At$2,8))};static ɵdir=Ot$2({type:n,selectors:[[`input`,`formControlName`,``,3,`type`,`checkbox`,3,`ngNoCva`,``],[`textarea`,`formControlName`,``,3,`ngNoCva`,``],[`input`,`formControl`,``,3,`type`,`checkbox`,3,`ngNoCva`,``],[`textarea`,`formControl`,``,3,`ngNoCva`,``],[`input`,`ngModel`,``,3,`type`,`checkbox`,3,`ngNoCva`,``],[`textarea`,`ngModel`,``,3,`ngNoCva`,``],[``,`ngDefaultControl`,``]],hostBindings:function(i,r){i&1&&vc(`input`,function(s){return r._handleInput(s.target.value)})(`blur`,function(){return r.onTouched()})(`compositionstart`,function(){return r._compositionStart()})(`compositionend`,function(s){return r._compositionEnd(s.target.value)})},standalone:!1,features:[A_([Dt$2]),uy]})}return n})();function Ce$1(n){return n==null||Ve$1(n)===0}function Ve$1(n){return n==null?null:Array.isArray(n)||typeof n==`string`?n.length:n instanceof Set?n.size:null}var A$1=new y$1(``);var De$1=new y$1(``);var Mt$1=/^(?=.{1,254}$)(?=.{1,64}@)[a-zA-Z0-9!#$%&'*+/=?^_`{|}~-]+(?:\.[a-zA-Z0-9!#$%&'*+/=?^_`{|}~-]+)*@[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*$/;var fe$1=class{static min(t){return ze$1(t)}static max(t){return Ze$2(t)}static required(t){return Xe$2(t)}static requiredTrue(t){return Et$2(t)}static email(t){return Ft$2(t)}static minLength(t){return wt$1(t)}static maxLength(t){return Ye$2(t)}static pattern(t){return Nt$1(t)}static nullValidator(t){return Z()}static compose(t){return nt$2(t)}static composeAsync(t){return rt$2(t)}};function ze$1(n){return t=>{if(t.value==null||n==null)return null;let e=parseFloat(t.value);return!isNaN(e)&&e<n?{min:{min:n,actual:t.value}}:null}}function Ze$2(n){return t=>{if(t.value==null||n==null)return null;let e=parseFloat(t.value);return!isNaN(e)&&e>n?{max:{max:n,actual:t.value}}:null}}function Xe$2(n){return Ce$1(n.value)?{required:!0}:null}function Et$2(n){return n.value===!0?null:{required:!0}}function Ft$2(n){return Ce$1(n.value)||Mt$1.test(n.value)?null:{email:!0}}function wt$1(n){return t=>{let e=t.value?.length??Ve$1(t.value);return e===null||e===0?null:e<n?{minlength:{requiredLength:n,actualLength:e}}:null}}function Ye$2(n){return t=>{let e=t.value?.length??Ve$1(t.value);return e!==null&&e>n?{maxlength:{requiredLength:n,actualLength:e}}:null}}function Nt$1(n){if(!n)return Z;let t,e;return typeof n==`string`?(e=``,n.charAt(0)!==`^`&&(e+=`^`),e+=n,n.charAt(n.length-1)!==`$`&&(e+=`$`),t=new RegExp(e)):(e=n.toString(),t=n),i=>{if(Ce$1(i.value))return null;let r=i.value;return t.test(r)?null:{pattern:{requiredPattern:e,actualValue:r}}}}function Z(n){return null}function Ke$1(n){return n!=null}function Je$2(n){return so(n)?q$2(n):n}function Qe$2(n){let t={};return n.forEach(e=>{t=e!=null?r(r({},t),e):t}),Object.keys(t).length===0?null:t}function et$2(n,t){return t.map(e=>e(n))}function It$2(n){return!n.validate}function tt$2(n){return n.map(t=>It$2(t)?t:e=>t.validate(e))}function nt$2(n){if(!n)return null;let t=n.filter(Ke$1);return t.length==0?null:function(e){return Qe$2(et$2(e,t))}}function it$2(n){return n!=null?nt$2(tt$2(n)):null}function rt$2(n){if(!n)return null;let t=n.filter(Ke$1);return t.length==0?null:function(e){return kw(et$2(e,t).map(Je$2)).pipe(z$1(Qe$2))}}function ot$2(n){return n!=null?rt$2(tt$2(n)):null}function ke(n,t){return n===null?[t]:Array.isArray(n)?[...n,t]:[n,t]}function st$2(n){return n._rawValidators}function at$2(n){return n._rawAsyncValidators}function ge$1(n){return n?Array.isArray(n)?n:[n]:[]}function X$1(n,t){return Array.isArray(n)?n.includes(t):n===t}function je$1(n,t){let e=ge$1(t);return ge$1(n).forEach(r=>{X$1(e,r)||e.push(r)}),e}function Te$1(n,t){return ge$1(t).filter(e=>!X$1(n,e))}var Y=class{get value(){return this.control?this.control.value:null}get valid(){return this.control?this.control.valid:null}get invalid(){return this.control?this.control.invalid:null}get pending(){return this.control?this.control.pending:null}get disabled(){return this.control?this.control.disabled:null}get enabled(){return this.control?this.control.enabled:null}get errors(){return this.control?this.control.errors:null}get pristine(){return this.control?this.control.pristine:null}get dirty(){return this.control?this.control.dirty:null}get touched(){return this.control?this.control.touched:null}get status(){return this.control?this.control.status:null}get untouched(){return this.control?this.control.untouched:null}get statusChanges(){return this.control?this.control.statusChanges:null}get valueChanges(){return this.control?this.control.valueChanges:null}get path(){return null}_composedValidatorFn;_composedAsyncValidatorFn;_rawValidators=[];_rawAsyncValidators=[];_setValidators(t){this._rawValidators=t||[],this._composedValidatorFn=it$2(this._rawValidators)}_setAsyncValidators(t){this._rawAsyncValidators=t||[],this._composedAsyncValidatorFn=ot$2(this._rawAsyncValidators)}get validator(){return this._composedValidatorFn||null}get asyncValidator(){return this._composedAsyncValidatorFn||null}_onDestroyCallbacks=[];_registerOnDestroy(t){this._onDestroyCallbacks.push(t)}_invokeOnDestroyCallbacks(){this._onDestroyCallbacks.forEach(t=>t()),this._onDestroyCallbacks=[]}reset(t=void 0){this.control?.reset(t)}hasError(t,e){return this.control?this.control.hasError(t,e):!1}getError(t,e){return this.control?this.control.getError(t,e):null}};var D=class extends Y{name;get formDirective(){return null}get path(){return null}};var T$1=`VALID`;var z=`INVALID`;var N$1=`PENDING`;var G=`DISABLED`;var y=class{};var K$1=class extends y{value;source;constructor(t,e){super(),this.value=t,this.source=e}};var B$1=class extends y{pristine;source;constructor(t,e){super(),this.pristine=t,this.source=e}};var U$2=class extends y{touched;source;constructor(t,e){super(),this.touched=t,this.source=e}};var I$1=class extends y{status;source;constructor(t,e){super(),this.status=t,this.source=e}};var pe=class extends y{source;constructor(t){super(),this.source=t}};var b=class extends y{source;constructor(t){super(),this.source=t}};function be$2(n){return(ie$1(n)?n.validators:n)||null}function St$2(n){return Array.isArray(n)?it$2(n):n||null}function Ae(n,t){return(ie$1(t)?t.asyncValidators:n)||null}function Ot$1(n){return Array.isArray(n)?ot$2(n):n||null}function ie$1(n){return n!=null&&!Array.isArray(n)&&typeof n==`object`}function lt$2(n,t,e){let i=n.controls;if(!(t?Object.keys(i):i).length)throw new m$1(1e3,``);if(!dt$2(i,e))throw new m$1(1001,``)}function ut$1(n,t,e){n._forEachChild((i,r)=>{if(e[r]===void 0)throw new m$1(-1002,``)})}var S=class{_pendingDirty=!1;_hasOwnPendingAsyncValidator=null;_pendingTouched=!1;_onCollectionChange=()=>{};_updateOn;_hasRequired=ne$2(!1);_parent=null;_asyncValidationSubscription;_composedValidatorFn;_composedAsyncValidatorFn;_rawValidators;_rawAsyncValidators;value;constructor(t,e){this._assignValidators(t),this._assignAsyncValidators(e)}get validator(){return this._composedValidatorFn}set validator(t){this._rawValidators=this._composedValidatorFn=t,this._updateHasRequiredValidator()}get asyncValidator(){return this._composedAsyncValidatorFn}set asyncValidator(t){this._rawAsyncValidators=this._composedAsyncValidatorFn=t}get parent(){return this._parent}get status(){return G$1(this.statusReactive)}set status(t){G$1(()=>this.statusReactive.set(t))}_status=On(()=>this.statusReactive());statusReactive=ne$2(void 0);get valid(){return this.status===T$1}get invalid(){return this.status===z}get pending(){return this.status===N$1}get disabled(){return this.status===G}get enabled(){return this.status!==G}errors;get pristine(){return G$1(this.pristineReactive)}set pristine(t){G$1(()=>this.pristineReactive.set(t))}_pristine=On(()=>this.pristineReactive());pristineReactive=ne$2(!0);get dirty(){return!this.pristine}get touched(){return G$1(this.touchedReactive)}set touched(t){G$1(()=>this.touchedReactive.set(t))}_touched=On(()=>this.touchedReactive());touchedReactive=ne$2(!1);get untouched(){return!this.touched}_events=new oe$2;events=this._events.asObservable();valueChanges;statusChanges;get updateOn(){return this._updateOn?this._updateOn:this.parent?this.parent.updateOn:`change`}setValidators(t){this._assignValidators(t)}setAsyncValidators(t){this._assignAsyncValidators(t)}addValidators(t){this.setValidators(je$1(t,this._rawValidators))}addAsyncValidators(t){this.setAsyncValidators(je$1(t,this._rawAsyncValidators))}removeValidators(t){this.setValidators(Te$1(t,this._rawValidators))}removeAsyncValidators(t){this.setAsyncValidators(Te$1(t,this._rawAsyncValidators))}hasValidator(t){return X$1(this._rawValidators,t)}hasAsyncValidator(t){return X$1(this._rawAsyncValidators,t)}clearValidators(){this.validator=null}clearAsyncValidators(){this.asyncValidator=null}markAsTouched(t={}){let e=this.touched===!1;this.touched=!0;let i=t.sourceControl??this;t.onlySelf||this._parent?.markAsTouched(s(r({},t),{sourceControl:i})),e&&t.emitEvent!==!1&&this._events.next(new U$2(!0,i))}markAllAsDirty(t={}){this.markAsDirty({onlySelf:!0,emitEvent:t.emitEvent,sourceControl:this}),this._forEachChild(e=>e.markAllAsDirty(t))}markAllAsTouched(t={}){this.markAsTouched({onlySelf:!0,emitEvent:t.emitEvent,sourceControl:this}),this._forEachChild(e=>e.markAllAsTouched(t))}markAsUntouched(t={}){let e=this.touched===!0;this.touched=!1,this._pendingTouched=!1;let i=t.sourceControl??this;this._forEachChild(r=>{r.markAsUntouched({onlySelf:!0,emitEvent:t.emitEvent,sourceControl:i})}),t.onlySelf||this._parent?._updateTouched(t,i),e&&t.emitEvent!==!1&&this._events.next(new U$2(!1,i))}markAsDirty(t={}){let e=this.pristine===!0;this.pristine=!1;let i=t.sourceControl??this;t.onlySelf||this._parent?.markAsDirty(s(r({},t),{sourceControl:i})),e&&t.emitEvent!==!1&&this._events.next(new B$1(!1,i))}markAsPristine(t={}){let e=this.pristine===!1;this.pristine=!0,this._pendingDirty=!1;let i=t.sourceControl??this;this._forEachChild(r=>{r.markAsPristine({onlySelf:!0,emitEvent:t.emitEvent})}),t.onlySelf||this._parent?._updatePristine(t,i),e&&t.emitEvent!==!1&&this._events.next(new B$1(!0,i))}markAsPending(t={}){this.status=N$1;let e=t.sourceControl??this;t.emitEvent!==!1&&(this._events.next(new I$1(this.status,e)),this.statusChanges.emit(this.status)),t.onlySelf||this._parent?.markAsPending(s(r({},t),{sourceControl:e}))}disable(t={}){let e=this._parentMarkedDirty(t.onlySelf);this.status=G,this.errors=null,this._forEachChild(r$2=>{r$2.disable(s(r({},t),{onlySelf:!0}))}),this._updateValue();let i=t.sourceControl??this;t.emitEvent!==!1&&(this._events.next(new K$1(this.value,i)),this._events.next(new I$1(this.status,i)),this.valueChanges.emit(this.value),this.statusChanges.emit(this.status)),this._updateAncestors(s(r({},t),{skipPristineCheck:e}),this),this._onDisabledChange.forEach(r=>r(!0))}enable(t={}){let e=this._parentMarkedDirty(t.onlySelf);this.status=T$1,this._forEachChild(i=>{i.enable(s(r({},t),{onlySelf:!0}))}),this.updateValueAndValidity({onlySelf:!0,emitEvent:t.emitEvent}),this._updateAncestors(s(r({},t),{skipPristineCheck:e}),this),this._onDisabledChange.forEach(i=>i(!1))}_updateAncestors(t,e){t.onlySelf||(this._parent?.updateValueAndValidity(t),t.skipPristineCheck||this._parent?._updatePristine({},e),this._parent?._updateTouched({},e))}setParent(t){this._parent=t}getRawValue(){return this.value}updateValueAndValidity(t={}){if(this._setInitialStatus(),this._updateValue(),this.enabled){let i=this._cancelExistingSubscription();this.errors=this._runValidator(),this.status=this._calculateStatus(),(this.status===T$1||this.status===N$1)&&this._runAsyncValidator(i,t.emitEvent)}let e=t.sourceControl??this;t.emitEvent!==!1&&(this._events.next(new K$1(this.value,e)),this._events.next(new I$1(this.status,e)),this.valueChanges.emit(this.value),this.statusChanges.emit(this.status)),t.onlySelf||this._parent?.updateValueAndValidity(s(r({},t),{sourceControl:e}))}_updateTreeValidity(t={emitEvent:!0}){this._forEachChild(e=>e._updateTreeValidity(t)),this.updateValueAndValidity({onlySelf:!0,emitEvent:t.emitEvent})}_setInitialStatus(){this.status=this._allControlsDisabled()?G:T$1}_runValidator(){return this.validator?this.validator(this):null}_runAsyncValidator(t,e){if(this.asyncValidator){this.status=N$1,this._hasOwnPendingAsyncValidator={emitEvent:e!==!1,shouldHaveEmitted:t!==!1};let i=Je$2(this.asyncValidator(this));this._asyncValidationSubscription=i.subscribe(r=>{this._hasOwnPendingAsyncValidator=null,this.setErrors(r,{emitEvent:e,shouldHaveEmitted:t})})}}_cancelExistingSubscription(){if(this._asyncValidationSubscription){this._asyncValidationSubscription.unsubscribe();let t=(this._hasOwnPendingAsyncValidator?.emitEvent||this._hasOwnPendingAsyncValidator?.shouldHaveEmitted)??!1;return this._hasOwnPendingAsyncValidator=null,t}return!1}setErrors(t,e={}){this.errors=t,this._updateControlsErrors(e.emitEvent!==!1,this,e.shouldHaveEmitted)}get(t){let e=t;return e==null||(Array.isArray(e)||(e=e.split(`.`)),e.length===0)?null:e.reduce((i,r)=>i&&i._find(r),this)}getError(t,e){let i=e?this.get(e):this;return i?.errors?i.errors[t]:null}hasError(t,e){return!!this.getError(t,e)}get root(){let t=this;for(;t._parent;)t=t._parent;return t}_updateControlsErrors(t,e,i){this.status=this._calculateStatus(),t&&this.statusChanges.emit(this.status),(t||i)&&this._events.next(new I$1(this.status,e)),this._parent&&this._parent._updateControlsErrors(t,e,i)}_initObservables(){this.valueChanges=new Ie$1,this.statusChanges=new Ie$1}_calculateStatus(){return this._allControlsDisabled()?G:this.errors?z:this._hasOwnPendingAsyncValidator||this._anyControlsHaveStatus(N$1)?N$1:this._anyControlsHaveStatus(z)?z:T$1}_anyControlsHaveStatus(t){return this._anyControls(e=>e.status===t)}_anyControlsDirty(){return this._anyControls(t=>t.dirty)}_anyControlsTouched(){return this._anyControls(t=>t.touched)}_updatePristine(t,e){let i=!this._anyControlsDirty(),r=this.pristine!==i;this.pristine=i,t.onlySelf||this._parent?._updatePristine(t,e),r&&this._events.next(new B$1(this.pristine,e))}_updateTouched(t={},e){this.touched=this._anyControlsTouched(),this._events.next(new U$2(this.touched,e)),t.onlySelf||this._parent?._updateTouched(t,e)}_onDisabledChange=[];_registerOnCollectionChange(t){this._onCollectionChange=t}_setUpdateStrategy(t){ie$1(t)&&t.updateOn!=null&&(this._updateOn=t.updateOn)}_parentMarkedDirty(t){return!t&&!!this._parent?.dirty&&!this._parent._anyControlsDirty()}_find(t){return null}_assignValidators(t){this._rawValidators=Array.isArray(t)?t.slice():t,this._composedValidatorFn=St$2(this._rawValidators),this._updateHasRequiredValidator()}_assignAsyncValidators(t){this._rawAsyncValidators=Array.isArray(t)?t.slice():t,this._composedAsyncValidatorFn=Ot$1(this._rawAsyncValidators)}_updateHasRequiredValidator(){G$1(()=>this._hasRequired.set(this.hasValidator(fe$1.required)))}};function dt$2(n,t){return Object.hasOwn(n,t)}function xt$1(n){return n.tagName===`INPUT`||n.tagName===`SELECT`||n.tagName===`TEXTAREA`}function Rt$1(n,t,e,i){switch(e){case`name`:n.setAttribute(t,e,i);break;case`disabled`:case`readonly`:case`required`:i?n.setAttribute(t,e,``):n.removeAttribute(t,e);break;case`max`:case`min`:case`minLength`:case`maxLength`:i!==void 0?n.setAttribute(t,e,i.toString()):n.removeAttribute(t,e);break}}var me=class{kind;context;control;message;constructor({kind:t,context:e,control:i}){this.kind=t,this.context=e,this.control=i}};function Pt(n){return typeof n==`number`?n:parseInt(n,10)}function ct$2(n){return typeof n==`number`?n:parseFloat(n)}var re$1=(()=>{class n{_validator=Z;_onChange;_enabled;ngOnChanges(e){if(this.inputName in e){let i=this.normalizeInput(e[this.inputName].currentValue);this._enabled=this.enabled(i),this._validator=this._enabled?this.createValidator(i):Z,this._onChange?.()}}validate(e){return this._validator(e)}registerOnValidatorChange(e){this._onChange=e}enabled(e){return e!=null}static ɵfac=function(i){return new(i||n)};static ɵdir=Ot$2({type:n,features:[en$1]})}return n})();var kt$2={provide:A$1,useExisting:oa(()=>jt$1),multi:!0};var jt$1=(()=>{class n extends re$1{max;inputName=`max`;normalizeInput=e=>ct$2(e);createValidator=e=>Ze$2(e);static ɵfac=(()=>{let e;return function(r){return(e||(e=Um(n)))(r||n)}})();static ɵdir=Ot$2({type:n,selectors:[[`input`,`type`,`number`,`max`,``,`formControlName`,``],[`input`,`type`,`number`,`max`,``,`formControl`,``],[`input`,`type`,`number`,`max`,``,`ngModel`,``]],hostVars:1,hostBindings:function(i,r){i&2&&gc(`max`,r._enabled?r.max:null)},inputs:{max:`max`},standalone:!1,features:[A_([kt$2]),uy]})}return n})();var Tt$2={provide:A$1,useExisting:oa(()=>Gt$1),multi:!0};var Gt$1=(()=>{class n extends re$1{min;inputName=`min`;normalizeInput=e=>ct$2(e);createValidator=e=>ze$1(e);static ɵfac=(()=>{let e;return function(r){return(e||(e=Um(n)))(r||n)}})();static ɵdir=Ot$2({type:n,selectors:[[`input`,`type`,`number`,`min`,``,`formControlName`,``],[`input`,`type`,`number`,`min`,``,`formControl`,``],[`input`,`type`,`number`,`min`,``,`ngModel`,``]],hostVars:1,hostBindings:function(i,r){i&2&&gc(`min`,r._enabled?r.min:null)},inputs:{min:`min`},standalone:!1,features:[A_([Tt$2]),uy]})}return n})();var Bt$1={provide:A$1,useExisting:oa(()=>ht$1),multi:!0};var ht$1=(()=>{class n extends re$1{required;inputName=`required`;normalizeInput=Vi;createValidator=e=>Xe$2;enabled(e){return e}static ɵfac=(()=>{let e;return function(r){return(e||(e=Um(n)))(r||n)}})();static ɵdir=Ot$2({type:n,selectors:[[``,`required`,``,`formControlName`,``,3,`type`,`checkbox`],[``,`required`,``,`formControl`,``,3,`type`,`checkbox`],[``,`required`,``,`ngModel`,``,3,`type`,`checkbox`]],hostVars:1,hostBindings:function(i,r){i&2&&gc(`required`,r._enabled?``:null)},inputs:{required:`required`},standalone:!1,features:[A_([Bt$1]),uy]})}return n})();var Ut$1={provide:A$1,useExisting:oa(()=>Ht$1),multi:!0};var Ht$1=(()=>{class n extends re$1{maxlength;inputName=`maxlength`;normalizeInput=e=>Pt(e);createValidator=e=>Ye$2(e);static ɵfac=(()=>{let e;return function(r){return(e||(e=Um(n)))(r||n)}})();static ɵdir=Ot$2({type:n,selectors:[[``,`maxlength`,``,`formControlName`,``],[``,`maxlength`,``,`formControl`,``],[``,`maxlength`,``,`ngModel`,``]],hostVars:1,hostBindings:function(i,r){i&2&&gc(`maxlength`,r._enabled?r.maxlength:null)},inputs:{maxlength:`maxlength`},standalone:!1,features:[A_([Ut$1]),uy]})}return n})();var Lt$1=new y$1(``);var oe$1=new y$1(``,{factory:()=>se$1});var se$1=`always`;function ft$1(n,t){return[...t.path,n]}function ve(n,t,e=se$1){Me$1(n,t),t.valueAccessor.writeValue(n.value),(n.disabled||e===`always`)&&t.valueAccessor.setDisabledState?.(n.disabled),$t$1(n,t),zt$1(n,t),qt$1(n,t),Wt(n,t)}function Ge$2(n,t,e=!0){let i=()=>{};t?.valueAccessor?.registerOnChange(i),t?.valueAccessor?.registerOnTouched(i),Q(n,t),n&&(t._invokeOnDestroyCallbacks(),n._registerOnCollectionChange(()=>{}))}function J$1(n,t){n.forEach(e=>{e.registerOnValidatorChange&&e.registerOnValidatorChange(t)})}function Wt(n,t){if(t.valueAccessor.setDisabledState){let e=i=>{t.valueAccessor.setDisabledState(i)};n.registerOnDisabledChange(e),t._registerOnDestroy(()=>{n._unregisterOnDisabledChange(e)})}}function Me$1(n,t){let e=st$2(n);t.validator!==null?n.setValidators(ke(e,t.validator)):typeof e==`function`&&n.setValidators([e]);let i=at$2(n);t.asyncValidator!==null?n.setAsyncValidators(ke(i,t.asyncValidator)):typeof i==`function`&&n.setAsyncValidators([i]);let r=()=>n.updateValueAndValidity();J$1(t._rawValidators,r),J$1(t._rawAsyncValidators,r)}function Q(n,t){let e=!1;if(n!==null){if(t.validator!==null){let r=st$2(n);if(Array.isArray(r)&&r.length>0){let o=r.filter(s=>s!==t.validator);o.length!==r.length&&(e=!0,n.setValidators(o))}}if(t.asyncValidator!==null){let r=at$2(n);if(Array.isArray(r)&&r.length>0){let o=r.filter(s=>s!==t.asyncValidator);o.length!==r.length&&(e=!0,n.setAsyncValidators(o))}}}let i=()=>{};return J$1(t._rawValidators,i),J$1(t._rawAsyncValidators,i),e}function $t$1(n,t){t.valueAccessor.registerOnChange(e=>{n._pendingValue=e,n._pendingChange=!0,n._pendingDirty=!0,n.updateOn===`change`&&gt$2(n,t)})}function qt$1(n,t){t.valueAccessor.registerOnTouched(()=>{n._pendingTouched=!0,n.updateOn===`blur`&&n._pendingChange&&gt$2(n,t),n.updateOn!==`submit`&&n.markAsTouched()})}function gt$2(n,t){n._pendingDirty&&n.markAsDirty(),n.setValue(n._pendingValue,{emitModelToViewChange:!1}),t.viewToModelUpdate(n._pendingValue),n._pendingChange=!1}function zt$1(n,t){let e=(i,r)=>{t.valueAccessor.writeValue(i),r&&t.viewToModelUpdate(i)};n.registerOnChange(e),t._registerOnDestroy(()=>{n._unregisterOnChange(e)})}function Zt(n,t){Me$1(n,t)}function Xt$1(n,t){return Q(n,t)}function pt$2(n,t){if(!Object.hasOwn(n,`model`))return!1;let e=n.model;return e.isFirstChange()?!0:!Object.is(t,e.currentValue)}function Yt$1(n){return Object.getPrototypeOf(n.constructor)===ne$1}function Kt$1(n,t){n._syncPendingControls(),t.forEach(e=>{let i=e.control;i.updateOn===`submit`&&i._pendingChange&&(e.viewToModelUpdate(i._pendingValue),i._pendingChange=!1)})}function Jt(n,t){if(!t)return null;let e,i,r;return t.forEach(o=>{o.constructor===qe$1?e=o:Yt$1(o)?i=o:r=o}),r||i||e||null}function Qt(n,t){let e=n.indexOf(t);e>-1&&n.splice(e,1)}var mt$1={provide:Lt$1,useFactory:()=>{let n=p(C,{self:!0});return{setParseErrors:t=>{n.setParseErrorSource(t)},set onReset(t){n.onReset=t}}}};var C=class extends Y{_parent=null;name=null;valueAccessor=null;isCustomControlBased=!1;userOnReset;resetSubscription;set onReset(t){this.userOnReset=t,this.resetSubscription?.unsubscribe(),this.resetSubscription=void 0,this.control&&(this.resetSubscription=this.control.events.subscribe(e=>{e instanceof b&&this.control&&this.userOnReset?.(this.control.value)}),this.subscription?.add(this.resetSubscription))}isNativeFormElement=!1;rawValueAccessors;_selectedValueAccessor=null;get selectedValueAccessor(){return this._selectedValueAccessor??=Jt(this,this.rawValueAccessors)}parseErrorsValidator=null;renderer;injector;requiredValidatorViaDi;subscription;customControlBindings=null;constructor(t,e,i){super(),this.injector=t,this.renderer=e,this.rawValueAccessors=i,this.injector?.get(De$2)?.onDestroy(()=>{this.removeParseErrorsValidator(this.control),this.subscription?.unsubscribe()})}setupCustomControl(){this.subscription?.unsubscribe();let t=this.injector?.get(lo);if(!this.control||!t)return;let e=t.markForCheck.bind(t);this.subscription=new re$2,this.subscription.add(this.control.valueChanges.subscribe(e)),this.subscription.add(this.control.statusChanges.subscribe(e)),this.resetSubscription?.unsubscribe(),this.resetSubscription=void 0,this.userOnReset&&(this.resetSubscription=this.control.events.subscribe(i=>{i instanceof b&&this.control&&this.userOnReset?.(this.control.value)}),this.subscription.add(this.resetSubscription)),this.parseErrorsValidator&&this.control.addValidators(this.parseErrorsValidator)}ngControlCreate(t){!t.nativeElement.hasAttribute?.(`ngNoCva`)&&(this.rawValueAccessors&&this.rawValueAccessors.length>0||this.valueAccessor!==null)||!t.customControl||(this.isCustomControlBased=!0,t.listenToCustomControlModel(r=>{this.control?.setValue(r,{emitModelToViewChange:!1}),this.control?.markAsDirty(),this.viewToModelUpdate(r)}),t.listenToCustomControlOutput(`touch`,()=>{this.control?.markAsTouched()}),this.customControlBindings={},this.isNativeFormElement=xt$1(t.nativeElement),this.requiredValidatorViaDi=this._rawValidators.find(r=>r instanceof ht$1))}ngControlUpdate(t,e){if(!this.isCustomControlBased)return;let i=this.control,r=this.customControlBindings;Object.is(r.value,i.value)||(r.value=i.value,t.setCustomControlModelInput(i.value)),this.bindControlProperty(t,r,`touched`,i.touched),this.bindControlProperty(t,r,`dirty`,i.dirty),this.bindControlProperty(t,r,`valid`,i.valid),this.bindControlProperty(t,r,`invalid`,i.invalid),this.bindControlProperty(t,r,`pending`,i.pending),this.bindControlProperty(t,r,`disabled`,i.disabled),this.shouldBindRequired&&this.bindControlProperty(t,r,`required`,this.isRequired);let o=i.errors;if(r.errors!==o){r.errors=o;let s=this._convertErrors(o);t.setInputOnDirectives(`errors`,s)}}get isRequired(){return(this.requiredValidatorViaDi?._enabled||this.control?._hasRequired())??!1}get shouldBindRequired(){return!0}bindControlProperty(t,e,i,r){if(e[i]===r)return;e[i]=r;let o=t.setInputOnDirectives(i,r);this.isNativeFormElement&&!o&&(i===`disabled`||i===`required`)&&this.renderer&&Rt$1(this.renderer,t.nativeElement,i,r)}_convertErrors(t){if(t===null)return[];let e=this.control;return Object.entries(t).map(([i,r])=>new me({context:r,kind:i,control:e}))}setParseErrorSource(t){if(t===void 0)return;let e=null,i=On(()=>{let r=t();return r.length===0?null:r.reduce((o,s)=>(o[s.kind]=s,o),{})});this.parseErrorsValidator=(()=>e).bind(this),zl(()=>{e=i(),this.control?.updateValueAndValidity({emitEvent:!1})},{injector:this.injector})}removeParseErrorsValidator(t){this.parseErrorsValidator&&(t?.removeValidators(this.parseErrorsValidator),t?.updateValueAndValidity({emitEvent:!1}))}};var ee$1=class{_cd;constructor(t){this._cd=t}get isTouched(){return this._cd?.control?._touched?.(),!!this._cd?.control?.touched}get isUntouched(){return!!this._cd?.control?.untouched}get isPristine(){return this._cd?.control?._pristine?.(),!!this._cd?.control?.pristine}get isDirty(){return!!this._cd?.control?.dirty}get isValid(){return this._cd?.control?._status?.(),!!this._cd?.control?.valid}get isInvalid(){return!!this._cd?.control?.invalid}get isPending(){return!!this._cd?.control?.pending}get isSubmitted(){return this._cd?._submitted?.(),!!this._cd?.submitted}};var xn=(()=>{class n extends ee$1{constructor(e){super(e)}static ɵfac=function(i){return new(i||n)(X$2(C,2))};static ɵdir=Ot$2({type:n,selectors:[[``,`formControlName`,``],[``,`ngModel`,``],[``,`formControl`,``]],hostVars:14,hostBindings:function(i,r){i&2&&Ty(`ng-untouched`,r.isUntouched)(`ng-touched`,r.isTouched)(`ng-pristine`,r.isPristine)(`ng-dirty`,r.isDirty)(`ng-valid`,r.isValid)(`ng-invalid`,r.isInvalid)(`ng-pending`,r.isPending)},standalone:!1,features:[uy]})}return n})();var Rn=(()=>{class n extends ee$1{constructor(e){super(e)}static ɵfac=function(i){return new(i||n)(X$2(D,10))};static ɵdir=Ot$2({type:n,selectors:[[``,`formGroupName`,``],[``,`formArrayName`,``],[``,`ngModelGroup`,``],[``,`formGroup`,``],[``,`formArray`,``],[`form`,3,`ngNoForm`,``],[``,`ngForm`,``]],hostVars:16,hostBindings:function(i,r){i&2&&Ty(`ng-untouched`,r.isUntouched)(`ng-touched`,r.isTouched)(`ng-pristine`,r.isPristine)(`ng-dirty`,r.isDirty)(`ng-valid`,r.isValid)(`ng-invalid`,r.isInvalid)(`ng-pending`,r.isPending)(`ng-submitted`,r.isSubmitted)},standalone:!1,features:[uy]})}return n})();var te$1=class extends S{constructor(t,e,i){super(be$2(e),Ae(i,e)),this.controls=t,this._initObservables(),this._setUpdateStrategy(e),this._setUpControls(),this.updateValueAndValidity({onlySelf:!0,emitEvent:!!this.asyncValidator})}controls;registerControl(t,e){return this._find(t)||(this.controls[t]=e,e.setParent(this),e._registerOnCollectionChange(this._onCollectionChange),e)}addControl(t,e,i={}){this.registerControl(t,e),this.updateValueAndValidity({emitEvent:i.emitEvent}),this._onCollectionChange()}removeControl(t,e={}){let i=this._find(t);i&&i._registerOnCollectionChange(()=>{}),delete this.controls[t],this.updateValueAndValidity({emitEvent:e.emitEvent}),this._onCollectionChange()}setControl(t,e,i={}){let r=this._find(t);r&&r._registerOnCollectionChange(()=>{}),delete this.controls[t],e&&this.registerControl(t,e),this.updateValueAndValidity({emitEvent:i.emitEvent}),this._onCollectionChange()}contains(t){return this._find(t)?.enabled===!0}setValue(t,e={}){G$1(()=>{ut$1(this,!0,t),Object.keys(t).forEach(i=>{lt$2(this,!0,i),this.controls[i].setValue(t[i],{onlySelf:!0,emitEvent:e.emitEvent})}),this.updateValueAndValidity(e)})}patchValue(t,e={}){t!=null&&(Object.keys(t).forEach(i=>{let r=this._find(i);r&&r.patchValue(t[i],{onlySelf:!0,emitEvent:e.emitEvent})}),this.updateValueAndValidity(e))}reset(t={},e={}){this._forEachChild((i,r$3)=>{i.reset(t?t[r$3]:null,s(r({},e),{onlySelf:!0}))}),this._updatePristine(e,this),this._updateTouched(e,this),this.updateValueAndValidity(e),e?.emitEvent!==!1&&this._events.next(new b(this))}getRawValue(){return this._reduceChildren({},(t,e,i)=>(t[i]=e.getRawValue(),t))}_syncPendingControls(){let t=this._reduceChildren(!1,(e,i)=>i._syncPendingControls()?!0:e);return t&&this.updateValueAndValidity({onlySelf:!0}),t}_forEachChild(t){Object.keys(this.controls).forEach(e=>{let i=this.controls[e];i&&t(i,e)})}_setUpControls(){this._forEachChild(t=>{t.setParent(this),t._registerOnCollectionChange(this._onCollectionChange)})}_updateValue(){this.value=this._reduceValue()}_anyControls(t){for(let[e,i]of Object.entries(this.controls))if(this.contains(e)&&t(i))return!0;return!1}_reduceValue(){return this._reduceChildren({},(e,i,r)=>((i.enabled||this.disabled)&&(e[r]=i.value),e))}_reduceChildren(t,e){let i=t;return this._forEachChild((r,o)=>{i=e(i,r,o)}),i}_allControlsDisabled(){for(let t of Object.keys(this.controls))if(this.controls[t].enabled)return!1;return Object.keys(this.controls).length>0||this.disabled}_find(t){return dt$2(this.controls,t)?this.controls[t]:null}};var _e$1=class extends te$1{};function Be$1(n,t){let e=n.indexOf(t);e>-1&&n.splice(e,1)}function Ue$1(n){return typeof n==`object`&&n!==null&&Object.keys(n).length===2&&`value`in n&&`disabled`in n}var H$1=class extends S{defaultValue=null;_onChange=[];_pendingValue;_pendingChange=!1;constructor(t=null,e,i){super(be$2(e),Ae(i,e)),this._applyFormState(t),this._setUpdateStrategy(e),this._initObservables(),this.updateValueAndValidity({onlySelf:!0,emitEvent:!!this.asyncValidator}),ie$1(e)&&(e.nonNullable||e.initialValueIsDefault)&&(Ue$1(t)?this.defaultValue=t.value:this.defaultValue=t)}setValue(t,e={}){G$1(()=>{this.value=this._pendingValue=t,this._onChange.length&&e.emitModelToViewChange!==!1&&this._onChange.forEach(i=>i(this.value,e.emitViewToModelChange!==!1)),this.updateValueAndValidity(e)})}patchValue(t,e={}){this.setValue(t,e)}reset(t=this.defaultValue,e={}){this._applyFormState(t),this.markAsPristine(e),this.markAsUntouched(e),this.setValue(this.value,e),e.overwriteDefaultValue&&(this.defaultValue=this.value),this._pendingChange=!1,e?.emitEvent!==!1&&this._events.next(new b(this))}_updateValue(){}_anyControls(t){return!1}_allControlsDisabled(){return this.disabled}registerOnChange(t){this._onChange.push(t)}_unregisterOnChange(t){Be$1(this._onChange,t)}registerOnDisabledChange(t){this._onDisabledChange.push(t)}_unregisterOnDisabledChange(t){Be$1(this._onDisabledChange,t)}_forEachChild(t){}_syncPendingControls(){return this.updateOn===`submit`&&(this._pendingDirty&&this.markAsDirty(),this._pendingTouched&&this.markAsTouched(),this._pendingChange)?(this.setValue(this._pendingValue,{onlySelf:!0,emitModelToViewChange:!1}),!0):!1}_applyFormState(t){Ue$1(t)?(this.value=this._pendingValue=t.value,t.disabled?this.disable({onlySelf:!0,emitEvent:!1}):this.enable({onlySelf:!0,emitEvent:!1})):this.value=this._pendingValue=t}};var en=n=>n instanceof H$1;var tn=(()=>{class n extends D{callSetDisabledState;get submitted(){return G$1(this._submittedReactive)}set submitted(e){this._submittedReactive.set(e)}_submitted=On(()=>this._submittedReactive());_submittedReactive=ne$2(!1);_oldForm;_onCollectionChange=()=>this._updateDomValue();directives=[];constructor(e,i,r){super(),this.callSetDisabledState=r,this._setValidators(e),this._setAsyncValidators(i)}ngOnChanges(e){this.onChanges(e)}ngOnDestroy(){this.onDestroy()}onChanges(e){this._checkFormPresent(),Object.hasOwn(e,`form`)&&(this._updateValidators(),this._updateDomValue(),this._updateRegistrations(),this._oldForm=this.form)}onDestroy(){this.form&&(Q(this.form,this),this.form._onCollectionChange===this._onCollectionChange&&this.form._registerOnCollectionChange(()=>{}))}get formDirective(){return this}get path(){return[]}addControl(e){let i=this.form.get(e.path);return e._setupWithForm(i,this.callSetDisabledState),i.updateValueAndValidity({emitEvent:!1}),this.directives.push(e),i}getControl(e){return this.form.get(e.path)}removeControl(e){Ge$2(e.control||null,e,!1),Qt(this.directives,e)}addFormGroup(e){this._setUpFormContainer(e)}removeFormGroup(e){this._cleanUpFormContainer(e)}getFormGroup(e){return this.form.get(e.path)}getFormArray(e){return this.form.get(e.path)}addFormArray(e){this._setUpFormContainer(e)}removeFormArray(e){this._cleanUpFormContainer(e)}updateModel(e,i){this.form.get(e.path).setValue(i)}onReset(){this.resetForm()}resetForm(e=void 0,i={}){this.form.reset(e,i),this._submittedReactive.set(!1)}onSubmit(e){return this.submitted=!0,Kt$1(this.form,this.directives),this.ngSubmit.emit(e),this.form._events.next(new pe(this.control)),e?.target?.method===`dialog`}_updateDomValue(){this.directives.forEach(e=>{let i=e.control,r=this.form.get(e.path);i!==r&&(Ge$2(i||null,e),en(r)&&e._setupWithForm(r,this.callSetDisabledState))}),this.form._updateTreeValidity({emitEvent:!1})}_setUpFormContainer(e){let i=this.form.get(e.path);Zt(i,e),i.updateValueAndValidity({emitEvent:!1})}_cleanUpFormContainer(e){let i=this.form?.get(e.path);i&&Xt$1(i,e)&&i.updateValueAndValidity({emitEvent:!1})}_updateRegistrations(){this.form._registerOnCollectionChange(this._onCollectionChange),this._oldForm?._registerOnCollectionChange(()=>{})}_updateValidators(){Me$1(this.form,this),this._oldForm&&Q(this._oldForm,this)}_checkFormPresent(){this.form}static ɵfac=function(i){return new(i||n)(X$2(A$1,10),X$2(De$1,10),X$2(oe$1,8))};static ɵdir=Ot$2({type:n,features:[uy,en$1]})}return n})();var nn={provide:D,useExisting:oa(()=>rn)};var rn=(()=>{class n extends tn{form=null;ngSubmit=new Ie$1;get control(){return this.form}static ɵfac=(()=>{let e;return function(r){return(e||(e=Um(n)))(r||n)}})();static ɵdir=Ot$2({type:n,selectors:[[``,`formGroup`,``]],hostBindings:function(i,r){i&1&&vc(`submit`,function(s){return r.onSubmit(s)})(`reset`,function(){return r.onReset()})},inputs:{form:[0,`formGroup`,`form`]},outputs:{ngSubmit:`ngSubmit`},exportAs:[`ngForm`],standalone:!1,features:[A_([nn]),uy]})}return n})();var on={provide:C,useExisting:oa(()=>sn)};var He$1=Promise.resolve();var sn=(()=>{class n extends C{_changeDetectorRef;callSetDisabledState;control=new H$1;static ngAcceptInputType_isDisabled;_registered=!1;_ngModelInjector;viewModel;name=``;isDisabled;model;options;update=new Ie$1;constructor(e,i,r,o,s,p,v,x){super(v,x,o),this._changeDetectorRef=s,this.callSetDisabledState=p,this._parent=e,this._setValidators(i),this._setAsyncValidators(r)}ngOnChanges(e){if(this._registered,this._checkForErrors(),!this._registered||`name`in e){if(this._registered&&(this._checkName(),this.formDirective)){let i=e.name.previousValue;this.formDirective.removeControl({name:i,path:this._getPath(i)})}this._setUpControl()}`isDisabled`in e&&this._updateDisabled(e),pt$2(e,this.viewModel)&&(this._updateValue(this.model),this.viewModel=this.model)}ngOnDestroy(){this.formDirective?.removeControl(this)}ɵngControlCreate(e){super.ngControlCreate(e)}ɵngControlUpdate(e){super.ngControlUpdate(e,!1)}get shouldBindRequired(){return!1}get path(){return this._getPath(this.name)}get formDirective(){return this._parent?this._parent.formDirective:null}viewToModelUpdate(e){this.viewModel=e,this.update.emit(e)}_setUpControl(){this._setUpdateStrategy(),this._isStandalone()?this._setUpStandalone():this.formDirective.addControl(this),this._registered=!0}_setUpdateStrategy(){this.options&&this.options.updateOn!=null&&(this.control._updateOn=this.options.updateOn)}_isStandalone(){return!this._parent||!!(this.options&&this.options.standalone)}_setUpStandalone(){this.isCustomControlBased?this.setupCustomControl():(this.valueAccessor??=this.selectedValueAccessor,ve(this.control,this,this.callSetDisabledState)),this.control.updateValueAndValidity({emitEvent:!1})}_setupWithForm(e){this.isCustomControlBased?this.setupCustomControl():(this.valueAccessor??=this.selectedValueAccessor,ve(this.control,this,e))}_checkForErrors(){this._checkName()}_checkName(){this.options&&this.options.name&&(this.name=this.options.name),!this._isStandalone()&&this.name}_updateValue(e){He$1.then(()=>{this.control.setValue(e,{emitViewToModelChange:!1}),this._changeDetectorRef?.markForCheck()})}_updateDisabled(e){let i=e.isDisabled.currentValue,r=i!==0&&Vi(i);He$1.then(()=>{r&&!this.control.disabled?this.control.disable():!r&&this.control.disabled&&this.control.enable(),this._changeDetectorRef?.markForCheck()})}_getPath(e){return this._parent?ft$1(e,this._parent):[e]}static ɵfac=function(i){return new(i||n)(X$2(D,9),X$2(A$1,10),X$2(De$1,10),X$2(O$1,10),X$2(lo,8),X$2(oe$1,8),X$2(be$3,8),X$2(Jt$1,8))};static ɵdir=Ot$2({type:n,selectors:[[``,`ngModel`,``,3,`formControlName`,``,3,`formControl`,``]],inputs:{name:`name`,isDisabled:[0,`disabled`,`isDisabled`],model:[0,`ngModel`,`model`],options:[0,`ngModelOptions`,`options`]},outputs:{update:`ngModelChange`},exportAs:[`ngModel`],standalone:!1,features:[A_([on,mt$1]),uy,en$1,mT(null)]})}return n})();var kn=(()=>{class n{static ɵfac=function(i){return new(i||n)};static ɵdir=Ot$2({type:n,selectors:[[`form`,3,`ngNoForm`,``,3,`ngNativeValidate`,``]],hostAttrs:[`novalidate`,``],standalone:!1})}return n})();var an={provide:O$1,useExisting:oa(()=>ln),multi:!0};var ln=(()=>{class n extends ne$1{writeValue(e){let i=e??``;this.setProperty(`value`,i)}registerOnChange(e){this.onChange=i=>{e(i==``?null:parseFloat(i))}}static ɵfac=(()=>{let e;return function(r){return(e||(e=Um(n)))(r||n)}})();static ɵdir=Ot$2({type:n,selectors:[[`input`,`type`,`number`,`formControlName`,``,3,`ngNoCva`,``],[`input`,`type`,`number`,`formControl`,``,3,`ngNoCva`,``],[`input`,`type`,`number`,`ngModel`,``,3,`ngNoCva`,``]],hostBindings:function(i,r){i&1&&vc(`input`,function(s){return r.onChange(s.target.value)})(`blur`,function(){return r.onTouched()})},standalone:!1,features:[A_([an]),uy]})}return n})();var ye=class extends S{constructor(t,e,i){super(be$2(e),Ae(i,e)),this.controls=t,this._initObservables(),this._setUpdateStrategy(e),this._setUpControls(),this.updateValueAndValidity({onlySelf:!0,emitEvent:!!this.asyncValidator})}controls;at(t){return this.controls[this._adjustIndex(t)]}push(t,e={}){Array.isArray(t)?t.forEach(i=>{this.controls.push(i),this._registerControl(i)}):(this.controls.push(t),this._registerControl(t)),this.updateValueAndValidity({emitEvent:e.emitEvent}),this._onCollectionChange()}insert(t,e,i={}){this.controls.splice(t,0,e),this._registerControl(e),this.updateValueAndValidity({emitEvent:i.emitEvent})}removeAt(t,e={}){let i=this._adjustIndex(t);i<0&&(i=0),this.controls[i]&&this.controls[i]._registerOnCollectionChange(()=>{}),this.controls.splice(i,1),this.updateValueAndValidity({emitEvent:e.emitEvent})}setControl(t,e,i={}){let r=this._adjustIndex(t);r<0&&(r=0),this.controls[r]&&this.controls[r]._registerOnCollectionChange(()=>{}),this.controls.splice(r,1),e&&(this.controls.splice(r,0,e),this._registerControl(e)),this.updateValueAndValidity({emitEvent:i.emitEvent}),this._onCollectionChange()}get length(){return this.controls.length}setValue(t,e={}){G$1(()=>{ut$1(this,!1,t),t.forEach((i,r)=>{lt$2(this,!1,r),this.at(r).setValue(i,{onlySelf:!0,emitEvent:e.emitEvent})}),this.updateValueAndValidity(e)})}patchValue(t,e={}){t!=null&&(t.forEach((i,r)=>{this.at(r)&&this.at(r).patchValue(i,{onlySelf:!0,emitEvent:e.emitEvent})}),this.updateValueAndValidity(e))}reset(t=[],e={}){this._forEachChild((i,r$4)=>{i.reset(t[r$4],s(r({},e),{onlySelf:!0}))}),this._updatePristine(e,this),this._updateTouched(e,this),this.updateValueAndValidity(e),e?.emitEvent!==!1&&this._events.next(new b(this))}getRawValue(){return this.controls.map(t=>t.getRawValue())}clear(t={}){this.controls.length<1||(this._forEachChild(e=>e._registerOnCollectionChange(()=>{})),this.controls.splice(0),this.updateValueAndValidity({emitEvent:t.emitEvent}))}_adjustIndex(t){return t<0?t+this.length:t}_syncPendingControls(){let t=this.controls.reduce((e,i)=>i._syncPendingControls()?!0:e,!1);return t&&this.updateValueAndValidity({onlySelf:!0}),t}_forEachChild(t){this.controls.forEach((e,i)=>{t(e,i)})}_updateValue(){this.value=this.controls.filter(t=>t.enabled||this.disabled).map(t=>t.value)}_anyControls(t){return this.controls.some(e=>e.enabled&&t(e))}_setUpControls(){this._forEachChild(t=>this._registerControl(t))}_allControlsDisabled(){for(let t of this.controls)if(t.enabled)return!1;return this.controls.length>0||this.disabled}_registerControl(t){t.setParent(this),t._registerOnCollectionChange(this._onCollectionChange)}_find(t){return this.at(t)??null}};var vt$1=new y$1(``);var un={provide:C,useExisting:oa(()=>dn)};var dn=(()=>{class n extends C{_ngModelWarningConfig;_added=!1;viewModel;control;name=null;set isDisabled(e){}model;update=new Ie$1;static _ngModelWarningSentOnce=!1;_ngModelWarningSent=!1;constructor(e,i,r,o,s,p,v){super(v,p,o),this._ngModelWarningConfig=s,this._parent=e,this._setValidators(i),this._setAsyncValidators(r)}_setupWithForm(e,i){this.control=e,this.isCustomControlBased?this.setupCustomControl():(this.valueAccessor??=this.selectedValueAccessor,ve(e,this,i))}ngOnChanges(e){this._added||this._setUpControl(),pt$2(e,this.viewModel)&&(this.viewModel=this.model,this.formDirective.updateModel(this,this.model))}ngOnDestroy(){this.formDirective?.removeControl(this)}viewToModelUpdate(e){this.viewModel=e,this.update.emit(e)}get path(){return ft$1(this.name==null?this.name:this.name.toString(),this._parent)}get formDirective(){return this._parent?this._parent.formDirective:null}_setUpControl(){this.control=this.formDirective.addControl(this),this._added=!0}ɵngControlCreate(e){super.ngControlCreate(e)}ɵngControlUpdate(e){this.isCustomControlBased&&(this._added||this._setUpControl(),super.ngControlUpdate(e,!0))}static ɵfac=function(i){return new(i||n)(X$2(D,13),X$2(A$1,10),X$2(De$1,10),X$2(O$1,10),X$2(vt$1,8),X$2(Jt$1,8),X$2(be$3,8))};static ɵdir=Ot$2({type:n,selectors:[[``,`formControlName`,``]],inputs:{name:[0,`formControlName`,`name`],isDisabled:[0,`disabled`,`isDisabled`],model:[0,`ngModel`,`model`]},outputs:{update:`ngModelChange`},standalone:!1,features:[A_([un,mt$1]),uy,en$1,mT(null)]})}return n})();var cn={provide:O$1,useExisting:oa(()=>yt$1),multi:!0};function _t$1(n,t){return n==null?`${t}`:(t&&typeof t==`object`&&(t=`Object`),`${n}: ${t}`.slice(0,50))}function hn(n){return n.split(`:`)[0]}var yt$1=(()=>{class n extends ne$1{value;_optionMap=new Map;_idCounter=0;set compareWith(e){this._compareWith=e}_compareWith=Object.is;appRefInjector=p(xn$1).injector;destroyRef=p(De$2);cdr=p(lo);_queuedWrite=!1;_writeValueAfterRender(){this._queuedWrite||this.appRefInjector.destroyed||(this._queuedWrite=!0,ic({write:()=>{this.destroyRef.destroyed||(this._queuedWrite=!1,this.writeValue(this.value))}},{injector:this.appRefInjector}))}writeValue(e){this.cdr.markForCheck(),this.value=e;let r=_t$1(this._getOptionId(e),e);this.setProperty(`value`,r)}registerOnChange(e){this.onChange=i=>{this.value=this._getOptionValue(i),e(this.value)}}_registerOption(){return(this._idCounter++).toString()}_getOptionId(e){for(let i of this._optionMap.keys())if(this._compareWith(this._optionMap.get(i),e))return i;return null}_getOptionValue(e){let i=hn(e);return this._optionMap.has(i)?this._optionMap.get(i):e}static ɵfac=(()=>{let e;return function(r){return(e||(e=Um(n)))(r||n)}})();static ɵdir=Ot$2({type:n,selectors:[[`select`,`formControlName`,``,3,`multiple`,``,3,`ngNoCva`,``],[`select`,`formControl`,``,3,`multiple`,``,3,`ngNoCva`,``],[`select`,`ngModel`,``,3,`multiple`,``,3,`ngNoCva`,``]],hostBindings:function(i,r){i&1&&vc(`change`,function(s){return r.onChange(s.target.value)})(`blur`,function(){return r.onTouched()})},inputs:{compareWith:`compareWith`},standalone:!1,features:[A_([cn]),uy]})}return n})();var jn=(()=>{class n{_element;_renderer;_select;id;constructor(e,i,r){this._element=e,this._renderer=i,this._select=r,this._select&&(this.id=this._select._registerOption())}set ngValue(e){this._select!=null&&(this._select._optionMap.set(this.id,e),this._setElementValue(_t$1(this.id,e)),this._select._writeValueAfterRender())}set value(e){this._setElementValue(e),this._select?._writeValueAfterRender()}_setElementValue(e){this._renderer.setProperty(this._element.nativeElement,`value`,e)}ngOnDestroy(){this._select?._optionMap.delete(this.id),this._select?._writeValueAfterRender()}static ɵfac=function(i){return new(i||n)(X$2(mt$2),X$2(Jt$1),X$2(yt$1,9))};static ɵdir=Ot$2({type:n,selectors:[[`option`]],inputs:{ngValue:`ngValue`,value:`value`},standalone:!1})}return n})();var fn={provide:O$1,useExisting:oa(()=>Ct$1),multi:!0};function Le$1(n,t){return n==null?`${t}`:(typeof t==`string`&&(t=`'${t}'`),t&&typeof t==`object`&&(t=`Object`),`${n}: ${t}`.slice(0,50))}function gn(n){return n.split(`:`)[0]}var Ct$1=(()=>{class n extends ne$1{value;_optionMap=new Map;_idCounter=0;set compareWith(e){this._compareWith=e}_compareWith=Object.is;writeValue(e){this.value=e;let i;if(Array.isArray(e)){let r=e.map(o=>this._getOptionId(o));i=(o,s)=>{o._setSelected(r.indexOf(s)>-1)}}else i=r=>{r._setSelected(!1)};this._optionMap.forEach(i)}registerOnChange(e){this.onChange=i=>{let r=[],o=i.selectedOptions;if(o!==void 0){let s=o;for(let p=0;p<s.length;p++){let v=s[p],x=this._getOptionValue(v.value);r.push(x)}}else{let s=i.options;for(let p=0;p<s.length;p++){let v=s[p];if(v.selected){let x=this._getOptionValue(v.value);r.push(x)}}}this.value=r,e(r)}}_registerOption(e){let i=(this._idCounter++).toString();return this._optionMap.set(i,e),i}_getOptionId(e){for(let i of this._optionMap.keys())if(this._compareWith(this._optionMap.get(i)._value,e))return i;return null}_getOptionValue(e){let i=gn(e);return this._optionMap.has(i)?this._optionMap.get(i)._value:e}static ɵfac=(()=>{let e;return function(r){return(e||(e=Um(n)))(r||n)}})();static ɵdir=Ot$2({type:n,selectors:[[`select`,`multiple`,``,`formControlName`,``,3,`ngNoCva`,``],[`select`,`multiple`,``,`formControl`,``,3,`ngNoCva`,``],[`select`,`multiple`,``,`ngModel`,``,3,`ngNoCva`,``]],hostBindings:function(i,r){i&1&&vc(`change`,function(s){return r.onChange(s.target)})(`blur`,function(){return r.onTouched()})},inputs:{compareWith:`compareWith`},standalone:!1,features:[A_([fn]),uy]})}return n})();var Tn=(()=>{class n{_element;_renderer;_select;id;_value;constructor(e,i,r){this._element=e,this._renderer=i,this._select=r,this._select&&(this.id=this._select._registerOption(this))}set ngValue(e){this._select!=null&&(this._value=e,this._setElementValue(Le$1(this.id,e)),this._select.writeValue(this._select.value))}set value(e){this._select?(this._value=e,this._setElementValue(Le$1(this.id,e)),this._select.writeValue(this._select.value)):this._setElementValue(e)}_setElementValue(e){this._renderer.setProperty(this._element.nativeElement,`value`,e)}_setSelected(e){this._renderer.setProperty(this._element.nativeElement,`selected`,e)}ngOnDestroy(){this._select&&(this._select._optionMap.delete(this.id),this._select.writeValue(this._select.value))}static ɵfac=function(i){return new(i||n)(X$2(mt$2),X$2(Jt$1),X$2(Ct$1,9))};static ɵdir=Ot$2({type:n,selectors:[[`option`]],inputs:{ngValue:`ngValue`,value:`value`},standalone:!1})}return n})();var Vt$1=(()=>{class n{static ɵfac=function(i){return new(i||n)};static ɵmod=wr({type:n});static ɵinj=wn({})}return n})();function We$1(n){return!!n&&(n.asyncValidators!==void 0||n.validators!==void 0||n.updateOn!==void 0)}var Gn=(()=>{class n{useNonNullable=!1;get nonNullable(){let e=new n;return e.useNonNullable=!0,e}group(e,i=null){let r=this._reduceControls(e),o={};return We$1(i)?o=i:i!==null&&(o.validators=i.validator,o.asyncValidators=i.asyncValidator),new te$1(r,o)}record(e,i=null){return new _e$1(this._reduceControls(e),i)}control(e,i,r$5){let o={};return this.useNonNullable?(We$1(i)?o=i:(o.validators=i,o.asyncValidators=r$5),new H$1(e,s(r({},o),{nonNullable:!0}))):new H$1(e,i,r$5)}array(e,i,r){return new ye(e.map(s=>this._createControl(s)),i,r)}_reduceControls(e){let i={};return Object.keys(e).forEach(r=>{i[r]=this._createControl(e[r])}),i}_createControl(e){if(e instanceof H$1)return e;if(e instanceof S)return e;if(Array.isArray(e)){let i=e[0],r=e.length>1?e[1]:null,o=e.length>2?e[2]:null;return this.control(i,r,o)}else return this.control(e)}static ɵfac=function(i){return new(i||n)};static ɵprov=K$2({token:n,factory:n.ɵfac})}return n})();var Bn=(()=>{class n{static withConfig(e){return{ngModule:n,providers:[{provide:oe$1,useValue:e.callSetDisabledState??se$1}]}}static ɵfac=function(i){return new(i||n)};static ɵmod=wr({type:n});static ɵinj=wn({imports:[Vt$1]})}return n})();var Un=(()=>{class n{static withConfig(e){return{ngModule:n,providers:[{provide:vt$1,useValue:e.warnOnNgModelWithFormControl??`always`},{provide:oe$1,useValue:e.callSetDisabledState??se$1}]}}static ɵfac=function(i){return new(i||n)};static ɵmod=wr({type:n});static ɵinj=wn({imports:[Vt$1]})}return n})();var{I:ze}=Tt$3,Ee=s=>s;var Ie=s=>s.strings===void 0;var Te=()=>document.createComment(``);var Ct=(s,t,e)=>{let r=s._$AA.parentNode,i=t===void 0?s._$AB:t._$AA;if(e===void 0)e=new ze(r.insertBefore(Te(),i),r.insertBefore(Te(),i),s,s.options);else{let n=e._$AB.nextSibling,o=e._$AM,d=o!==s;if(d){let l;e._$AQ?.(s),e._$AM=s,e._$AP!==void 0&&(l=s._$AU)!==o._$AU&&e._$AP(l)}if(n!==i||d){let l=e._$AA;for(;l!==n;){let M=Ee(l).nextSibling;Ee(r).insertBefore(l,i),l=M}}}return e};var Mt=(s,t,e=s)=>(s._$AI(t,e),s);var Ze$1={};var Dt$1=(s,t=Ze$1)=>s._$AH=t;var Ft$1=s=>s._$AH;var St$1=s=>{s._$AR(),s._$AA.remove()};var E=(s,t)=>{let e=s._$AN;if(e===void 0)return!1;for(let r of e)r._$AO?.(t,!1),E(r,t);return!0};var N=s=>{let t,e;do{if((t=s._$AM)===void 0)break;e=t._$AN,e.delete(s),s=t}while(e?.size===0)};var Re=s=>{for(let t;t=s._$AM;s=t){let e=t._$AN;if(e===void 0)t._$AN=e=new Set;else if(e.has(s))break;e.add(s),Xe$1(t)}};function Ye$1(s){this._$AN!==void 0?(N(this),this._$AM=s,Re(this)):this._$AM=s}function Qe$1(s,t=!1,e=0){let r=this._$AH,i=this._$AN;if(i!==void 0&&i.size!==0)if(t)if(Array.isArray(r))for(let n=e;n<r.length;n++)E(r[n],!1),N(r[n]);else r!=null&&(E(r,!1),N(r));else E(this,s)}var Xe$1=s=>{s.type==T$2.CHILD&&(s._$AP??=Qe$1,s._$AQ??=Ye$1)};var L=class extends s$1{constructor(){super(...arguments),this._$AN=void 0}_$AT(t,e,r){super._$AT(t,e,r),Re(this),this.isConnected=t._$AU}_$AO(t,e=!0){t!==this.isConnected&&(this.isConnected=t,t?this.reconnected?.():this.disconnected?.()),e&&(E(this,t),N(this))}setValue(t){if(Ie(this._$Ct))this._$Ct._$AI(t,this);else{let e=[...this._$Ct._$AH];e[this._$Ci]=t,this._$Ct._$AI(e,this,0)}}disconnected(){}reconnected(){}};var k=()=>new J;var J=class{};var X=new WeakMap;var g=_$1(class extends L{render(s){return p$1}update(s,[t]){let e=t!==this.G;return e&&this.rt(void 0),(e||this.lt!==this.ct)&&(this.G=t,this.ht=s.options?.host,this.rt(this.ct=s.element)),p$1}rt(s){if(this.G!==void 0)if(this.isConnected||(s=void 0),typeof this.G==`function`){let t=this.ht??globalThis,e=X.get(t);e===void 0&&(e=new WeakMap,X.set(t,e)),e.get(this.G)!==void 0&&this.G.call(this.ht,void 0),e.set(this.G,s),s!==void 0&&this.G.call(this.ht,s)}else this.G.value=s}get lt(){return typeof this.G==`function`?X.get(this.ht??globalThis)?.get(this.G):this.G?.value}disconnected(){this.lt===this.ct&&this.rt(void 0)}reconnected(){this.rt(this.ct)}});var Je$1=`position:absolute;width:1px;height:1px;padding:0;margin:-1px;overflow:hidden;clip:rect(0,0,0,0);white-space:nowrap;border:0;`;var ee=class{constructor(t,e={}){this.clearTimer=null,this.pendingMessage=null,this.regionRef=k(),this.host=t,this.politeness=e.politeness??`polite`,this.clearAfterMs=e.clearAfterMs??1500,this.omitRole=e.omitRole??!1,t.addController(this)}hostUpdated(){if(this.pendingMessage!==null&&this.regionRef.value){let t=this.pendingMessage;this.pendingMessage=null,this.writeMessage(t)}}hostDisconnected(){this.clearTimer!==null&&(clearTimeout(this.clearTimer),this.clearTimer=null)}announce(t){if(t){if(!this.regionRef.value){this.pendingMessage=t,this.host.requestUpdate();return}this.writeMessage(t)}}template(){let t=this.omitRole?p$1:this.politeness===`assertive`?`alert`:`status`;return Ht$2`<div
      ${g(this.regionRef)}
      role=${t}
      aria-live=${this.politeness}
      aria-atomic="true"
      style=${Je$1}
    ></div>`}writeMessage(t){let e=this.regionRef.value;if(e){if(e.textContent===t){e.textContent=``,queueMicrotask(()=>{let r=this.regionRef.value;r&&(r.textContent=t),this.scheduleClear()});return}e.textContent=t,this.scheduleClear()}}scheduleClear(){this.clearTimer!==null&&clearTimeout(this.clearTimer),this.clearTimer=setTimeout(()=>{let t=this.regionRef.value;t&&(t.textContent=``),this.clearTimer=null},this.clearAfterMs)}};function m(s=document){let t=s.activeElement;for(;t?.shadowRoot?.activeElement;)t=t.shadowRoot.activeElement;return t}var te=class{constructor(t,e){this.root=t,this.options=e,this.snapshot=null,this.retried=!1}capture(){this.snapshot=null,this.retried=!1;let t=this.root();if(!t)return;let e=this.activeInsideRoot(t);if(!e)return;let r=e.shadowRoot?m(e.shadowRoot):e;if(!r||r.assignedSlot)return;let i=this.candidates(),n=this.resolveKey(r);if(n===null)return;let o=i.indexOf(r);this.snapshot={key:n,index:o<0?0:o}}restore(){let t=this.snapshot;if(!t)return`unchanged`;let e=this.root();if(!e)return this.snapshot=null,`none`;let r=this.candidates(),i={preventScroll:this.options.preventScroll??!0},n=r.find(d=>this.resolveKey(d)===t.key);if(n)return!this.tryFocus(n,i)&&!this.retried?(this.retried=!0,queueMicrotask(()=>this.restore()),`same`):(this.snapshot=null,`same`);if(r.length>0){let d=r[Math.min(t.index,r.length-1)];return this.tryFocus(d,i),this.options.announce?.(`${this.options.nameOf?.(d)??`Item`} focused.`),this.snapshot=null,`neighbour`}let o=this.options.container?.()??e.firstElementChild;return o?(o.hasAttribute(`tabindex`)||o.setAttribute(`tabindex`,`-1`),this.tryFocus(o,i),this.options.announce?.(`Nothing left to focus.`),this.snapshot=null,`container`):(this.snapshot=null,`none`)}around(t){this.capture();try{return t()}finally{this.restore()}}candidates(){let t=this.root();return t?Array.from(t.querySelectorAll(this.options.selector)):[]}resolveKey(t){return this.options.keyOf?this.options.keyOf(t):t.id||t.dataset.focusKey||null}activeInsideRoot(t){if(t instanceof HTMLElement){let e=m();return e&&t.contains(e)?e:null}return t.activeElement}tryFocus(t,e){t.focus(e);let r=m();return r===t||r!==null&&t.contains(r)}};var se=class{constructor(t,e){this.inner=e,t.addController(this)}hostUpdate(){this.inner.capture()}hostUpdated(){this.inner.restore()}};var h=[];function et$1(s=`dismiss-frame`){let t=Symbol(s);return h.push(t),t}function tt$1(s){let t=h.lastIndexOf(s);t>=0&&h.splice(t,1)}function st$1(s){return h.length>0&&h[h.length-1]===s}function rt$1(){return h.length>0?h[h.length-1]:null}function it$1(){return h.length}function nt$1(){h.length=0}var A={push:et$1,release:tt$1,isTop:st$1,peek:rt$1,depth:it$1,resetForTesting:nt$1};var re=class{constructor(t,e={}){this.region=t,this.active=!1,this.restoreTo=null,this.onKeyDown=r=>{if(r.key!==`Tab`||!this.active||this.options.enabled&&!this.options.enabled())return;let i=this.region();if(!i||!i.isConnected)return;let n=ie(i);if(n.length===0){r.preventDefault();return}let o=n[0],d=n[n.length-1],l=m();if(!(l!==null&&(i===l||De(i,l)))){r.preventDefault(),(r.shiftKey?d:o).focus({preventScroll:!0});return}r.shiftKey&&l===o?(r.preventDefault(),d.focus({preventScroll:!0})):!r.shiftKey&&l===d&&(r.preventDefault(),o.focus({preventScroll:!0}))},this.options=e}get isActive(){return this.active}activate(){if(this.active)return;let t=this.region();if(!t)return;let e=m();this.restoreTo=e instanceof HTMLElement?e:null,this.active=!0,t.ownerDocument.addEventListener(`keydown`,this.onKeyDown,!0);let r=this.options.initialFocus??`first`;if(typeof r==`function`&&(r=r()??`first`),r instanceof HTMLElement)r.focus({preventScroll:!0});else if(r===`self`)t.hasAttribute(`tabindex`)||t.setAttribute(`tabindex`,`-1`),t.focus({preventScroll:!0});else if(r===`first`){let i=ie(t)[0];i?i.focus({preventScroll:!0}):(t.hasAttribute(`tabindex`)||t.setAttribute(`tabindex`,`-1`),t.focus({preventScroll:!0}))}}deactivate(){this.active&&(this.active=!1,(this.region()?.ownerDocument??document).removeEventListener(`keydown`,this.onKeyDown,!0),(this.options.returnFocus??!0)&&this.restoreTo?.isConnected&&this.restoreTo.focus({preventScroll:!0}),this.restoreTo=null)}};function De(s,t){let e=t;for(;e;){if(e===s)return!0;if(e instanceof Element&&e.assignedSlot){e=e.assignedSlot;continue}let r=e.parentNode;e=r instanceof ShadowRoot?r.host:r}return!1}var ot$1=[`a[href]`,`button`,`input`,`select`,`textarea`,`summary`,`audio[controls]`,`video[controls]`,`[contenteditable]:not([contenteditable="false"])`,`[tabindex]`].join(`,`);function ie(s){let t=[];return O(s,t),t}function O(s,t){for(let e of Array.from(s.children))if(e instanceof HTMLElement&&!Me(e)){if(e instanceof HTMLSlotElement){let r=e.assignedElements({flatten:!0}),i=r.length>0?r:Array.from(e.children);for(let n of i)!(n instanceof HTMLElement)||Me(n)||(Ce(n)&&t.push(n),O(n,t));continue}Ce(e)&&t.push(e),e.shadowRoot&&O(e.shadowRoot,t),O(e,t)}}function Ce(s){return s.tabIndex<0||s.hasAttribute(`inert`)||s.closest(`[inert]`)||s.matches(`:disabled`)||s.getAttribute(`aria-hidden`)===`true`?!1:s.matches(ot$1)}function Me(s){if(s.hidden)return!0;let t=s.style.display,e=s.style.visibility;return t===`none`||e===`hidden`}var ne=new WeakMap;function B(s){if(ne.has(s))return ne.get(s)??null;let t=null,e=s.attachInternals;if(typeof e==`function`)try{t=e.call(s)}catch{t=null}return ne.set(s,t),t}function $e(){return typeof ElementInternals<`u`&&`ariaLabelledByElements`in ElementInternals.prototype&&typeof Element<`u`&&`ariaLabelledByElements`in Element.prototype}var at$1=[`aria-labelledby`,`aria-describedby`];var Fe=!1;var v=class{constructor(t,e={}){this.host=t,this.options=e,this.internals=B(t),this.referenceAttributes=e.referenceAttributes??at$1,e.role&&this.internals?this.internals.role=e.role:e.role&&!t.hasAttribute(`role`)&&t.setAttribute(`role`,e.role)}get usesInternals(){return this.internals!==null}setState(t){for(let[e,r]of Object.entries(t)){let i=ct$1[e];if(i){if(r==null){this.clear(e,i);continue}this.write(e,i,String(r))}}}syncReferences(){let t=this.options.referenceTarget?.()??null??this.internals;if(!t||!$e()){let i=this.referenceAttributes.filter(n=>this.host.hasAttribute(n));return i.length>0&&!Fe&&(Fe=!0,console.warn(`[a11y] This browser cannot assign ARIA element references, so ${i.join(`, `)} on <${this.host.localName}> cannot cross its shadow boundary. Use the component's label property instead.`)),i}let e=[],r=this.host.getRootNode();for(let i of this.referenceAttributes){let n=lt$1[i];if(!n)continue;let o=i===`aria-describedby`?this.options.describedByExtras?.()??[]:[],d=this.host.getAttribute(i);if(d===null){this.assignReferences(t,n,o.length>0?o:null);continue}let l=d.split(/\s+/).filter(Boolean),M=l.map(V=>r.getElementById?.(V)??null).filter(V=>V instanceof HTMLElement);M.length!==l.length&&e.push(i);let fe=[...M,...o];this.assignReferences(t,n,fe.length>0?fe:null)}return e}assignReferences(t,e,r){try{t[e]=r}catch{}}write(t,e,r){if(this.internals){let i=Se[t];if(i&&i in this.internals){this.internals[i]=r;return}}this.host.setAttribute(e,r)}clear(t,e){if(this.internals){let r=Se[t];if(r&&r in this.internals){this.internals[r]=null;return}}this.host.removeAttribute(e)}};var ct$1={expanded:`aria-expanded`,selected:`aria-selected`,checked:`aria-checked`,pressed:`aria-pressed`,disabled:`aria-disabled`,invalid:`aria-invalid`,required:`aria-required`,readOnly:`aria-readonly`,current:`aria-current`,hasPopup:`aria-haspopup`,level:`aria-level`,orientation:`aria-orientation`,multiSelectable:`aria-multiselectable`,valueNow:`aria-valuenow`,valueMin:`aria-valuemin`,valueMax:`aria-valuemax`,valueText:`aria-valuetext`,label:`aria-label`};var Se={expanded:`ariaExpanded`,selected:`ariaSelected`,checked:`ariaChecked`,pressed:`ariaPressed`,disabled:`ariaDisabled`,invalid:`ariaInvalid`,required:`ariaRequired`,readOnly:`ariaReadOnly`,current:`ariaCurrent`,hasPopup:`ariaHasPopup`,level:`ariaLevel`,orientation:`ariaOrientation`,multiSelectable:`ariaMultiSelectable`,valueNow:`ariaValueNow`,valueMin:`ariaValueMin`,valueMax:`ariaValueMax`,valueText:`ariaValueText`,label:`ariaLabel`};var lt$1={"aria-labelledby":`ariaLabelledByElements`,"aria-describedby":`ariaDescribedByElements`};var Ne=`invalid-feedback`;var dt$1={id:p$1,node:p$1};function T(s,t,e){let r=t?.trim();return!r||!e?dt$1:{id:s,node:Ht$2`<small class=${Ne} id=${s}>${r}</small>`}}function I(s){let t=s?.querySelector(`.${Ne}`);return t?[t]:[]}function R(s){return(()=>{class e extends s{static{this.formAssociated=!0}#t=!1;get internals(){return B(this)}get effectiveDisabled(){return this.#t||this.hasAttribute(`disabled`)}#e(){return this}syncFormValue(){let i=this.internals;!i||typeof i.setFormValue!=`function`||i.setFormValue(this.#e().formValue())}setFormValidity(i,n){let o=this.internals;if(!o||typeof o.setValidity!=`function`)return;let d=this.#e().formValidityAnchor?.()??void 0,l=Object.values(i).some(Boolean);o.setValidity(i,l?n:void 0,d)}formDisabledCallback(i){this.#t=i,this.requestUpdate?.()}formResetCallback(){this.#e().formReset(),this.syncFormValue()}formStateRestoreCallback(i){this.#e().formRestore?.(i),this.syncFormValue()}}return e})()}function gs(s){return s.buttons===0||s.detail===0}function vs(s){let t=s.touches&&s.touches[0]||s.changedTouches&&s.changedTouches[0];return!!t&&t.identifier===-1&&(t.radiusX==null||t.radiusX===1)&&(t.radiusY==null||t.radiusY===1)}var oe;function ft(){if(oe==null){let s=typeof document<`u`?document.head:null;oe=!!(s&&(s.createShadowRoot||s.attachShadow))}return oe}function ys(s){if(ft()){let t=s.getRootNode?s.getRootNode():null;if(typeof ShadowRoot<`u`&&ShadowRoot&&t instanceof ShadowRoot)return t}return null}function ks(s){if(s.composedPath)try{return s.composedPath()[0]}catch{}return s.target}var ae$1;try{ae$1=typeof Intl<`u`&&Intl.v8BreakIterator}catch{ae$1=!1}var Le=(()=>{class s{_platformId=p(si);isBrowser=this._platformId?$2(this._platformId):typeof document==`object`&&!!document;EDGE=this.isBrowser&&/(edge)/i.test(navigator.userAgent);TRIDENT=this.isBrowser&&/(msie|trident)/i.test(navigator.userAgent);BLINK=this.isBrowser&&!!(window.chrome||ae$1)&&typeof CSS<`u`&&!this.EDGE&&!this.TRIDENT;WEBKIT=this.isBrowser&&/AppleWebKit/i.test(navigator.userAgent)&&!this.BLINK&&!this.EDGE&&!this.TRIDENT;IOS=this.isBrowser&&/iPad|iPhone|iPod/.test(navigator.userAgent)&&!(`MSStream`in window);FIREFOX=this.isBrowser&&/(firefox|minefield)/i.test(navigator.userAgent);ANDROID=this.isBrowser&&/android/i.test(navigator.userAgent)&&!this.TRIDENT;SAFARI=this.isBrowser&&/safari/i.test(navigator.userAgent)&&this.WEBKIT;static ɵfac=function(r){return new(r||s)};static ɵprov=K$2({token:s,factory:s.ɵfac})}return s})();function Rs(s,t=0){return mt(s)?Number(s):arguments.length===2?t:0}function mt(s){return!isNaN(parseFloat(s))&&!isNaN(Number(s))}function Cs(s){return s instanceof mt$2?s.nativeElement:s}var q$1=new WeakMap;var Ss=(()=>{class s{_appRef;_injector=p(be$3);_environmentInjector=p(ee$2);load(e){let r=this._appRef=this._appRef||this._injector.get(xn$1),i=q$1.get(r);i||(i={loaders:new Set,refs:[]},q$1.set(r,i),r.onDestroy(()=>{q$1.get(r)?.refs.forEach(n=>n.destroy()),q$1.delete(r)})),i.loaders.has(e)||(i.loaders.add(e),i.refs.push(w$(e,{environmentInjector:this._environmentInjector})))}static ɵfac=function(r){return new(r||s)};static ɵprov=K$2({token:s,factory:s.ɵfac})}return s})();function Ns(s){return Array.isArray(s)?s:[s]}var H=(()=>{class s{_platform=p(Le);isDisabled(e){return e.hasAttribute(`disabled`)}isVisible(e){return gt$1(e)&&getComputedStyle(e).visibility===`visible`}isTabbable(e){if(!this._platform.isBrowser)return!1;let r=pt$1(Et$1(e));if(r&&(Oe(r)===-1||!this.isVisible(r)))return!1;let i=e.nodeName.toLowerCase(),n=Oe(e);return e.hasAttribute(`contenteditable`)?n!==-1:i===`iframe`||i===`object`||this._platform.WEBKIT&&this._platform.IOS&&!wt(e)?!1:i===`audio`?e.hasAttribute(`controls`)?n!==-1:!1:i===`video`?n===-1?!1:n!==null?!0:this._platform.FIREFOX||e.hasAttribute(`controls`):e.tabIndex>=0}isFocusable(e,r){return xt(e)&&!this.isDisabled(e)&&(r?.ignoreVisibility||this.isVisible(e))}static ɵfac=function(r){return new(r||s)};static ɵprov=K$2({token:s,factory:s.ɵfac})}return s})();function pt$1(s){try{return s.frameElement}catch{return null}}function gt$1(s){return!!(s.offsetWidth||s.offsetHeight||typeof s.getClientRects==`function`&&s.getClientRects().length)}function vt(s){let t=s.nodeName.toLowerCase();return t===`input`||t===`select`||t===`button`||t===`textarea`}function _t(s){return kt$1(s)&&s.type==`hidden`}function yt(s){return At$1(s)&&s.hasAttribute(`href`)}function kt$1(s){return s.nodeName.toLowerCase()==`input`}function At$1(s){return s.nodeName.toLowerCase()==`a`}function Be(s){if(!s.hasAttribute(`tabindex`)||s.tabIndex===void 0)return!1;let t=s.getAttribute(`tabindex`);return!!(t&&!isNaN(parseInt(t,10)))}function Oe(s){if(!Be(s))return null;let t=parseInt(s.getAttribute(`tabindex`)||``,10);return isNaN(t)?-1:t}function wt(s){let t=s.nodeName.toLowerCase(),e=t===`input`&&s.type;return e===`text`||e===`password`||t===`select`||t===`textarea`}function xt(s){return _t(s)?!1:vt(s)||yt(s)||s.hasAttribute(`contenteditable`)||Be(s)}function Et$1(s){return s.ownerDocument&&s.ownerDocument.defaultView||window}var U$1=class{_element;_checker;_ngZone;_document;_injector;_startAnchor=null;_endAnchor=null;_hasAttached=!1;startAnchorListener=()=>{!this.focusLastTabbableElement()&&this._checker.isFocusable(this._element)&&this._element.focus()};endAnchorListener=()=>{!this.focusFirstTabbableElement()&&this._checker.isFocusable(this._element)&&this._element.focus()};get enabled(){return this._enabled}set enabled(t){this._enabled=t,this._startAnchor&&this._endAnchor&&(this._toggleAnchorTabIndex(t,this._startAnchor),this._toggleAnchorTabIndex(t,this._endAnchor))}_enabled=!0;constructor(t,e,r,i,n=!1,o){this._element=t,this._checker=e,this._ngZone=r,this._document=i,this._injector=o,n||this.attachAnchors()}destroy(){let t=this._startAnchor,e=this._endAnchor;t&&(t.removeEventListener(`focus`,this.startAnchorListener),t.remove()),e&&(e.removeEventListener(`focus`,this.endAnchorListener),e.remove()),this._startAnchor=this._endAnchor=null,this._hasAttached=!1}attachAnchors(){return this._hasAttached?!0:(this._ngZone.runOutsideAngular(()=>{this._startAnchor||(this._startAnchor=this._createAnchor(),this._startAnchor.addEventListener(`focus`,this.startAnchorListener)),this._endAnchor||(this._endAnchor=this._createAnchor(),this._endAnchor.addEventListener(`focus`,this.endAnchorListener))}),this._element.parentNode&&(this._element.parentNode.insertBefore(this._startAnchor,this._element),this._element.parentNode.insertBefore(this._endAnchor,this._element.nextSibling),this._hasAttached=!0),this._hasAttached)}focusInitialElementWhenReady(t){return new Promise(e=>{this._executeOnStable(()=>e(this.focusInitialElement(t)))})}focusFirstTabbableElementWhenReady(t){return new Promise(e=>{this._executeOnStable(()=>e(this.focusFirstTabbableElement(t)))})}focusLastTabbableElementWhenReady(t){return new Promise(e=>{this._executeOnStable(()=>e(this.focusLastTabbableElement(t)))})}_getRegionBoundary(t){let e=this._element.querySelectorAll(`[cdk-focus-region-${t}], [cdkFocusRegion${t}], [cdk-focus-${t}]`);return t==`start`?e.length?e[0]:this._getFirstTabbableElement(this._element):e.length?e[e.length-1]:this._getLastTabbableElement(this._element)}focusInitialElement(t){let e=this._element.querySelector(`[cdk-focus-initial], [cdkFocusInitial]`);if(e){if(!this._checker.isFocusable(e)){let r=this._getFirstTabbableElement(e);return r?.focus(t),!!r}return e.focus(t),!0}return this.focusFirstTabbableElement(t)}focusFirstTabbableElement(t){let e=this._getRegionBoundary(`start`);return e&&e.focus(t),!!e}focusLastTabbableElement(t){let e=this._getRegionBoundary(`end`);return e&&e.focus(t),!!e}hasAttached(){return this._hasAttached}_getFirstTabbableElement(t){if(this._checker.isFocusable(t)&&this._checker.isTabbable(t))return t;let e=t.children;for(let r=0;r<e.length;r++){let i=e[r].nodeType===this._document.ELEMENT_NODE?this._getFirstTabbableElement(e[r]):null;if(i)return i}return null}_getLastTabbableElement(t){if(this._checker.isFocusable(t)&&this._checker.isTabbable(t))return t;let e=t.children;for(let r=e.length-1;r>=0;r--){let i=e[r].nodeType===this._document.ELEMENT_NODE?this._getLastTabbableElement(e[r]):null;if(i)return i}return null}_createAnchor(){let t=this._document.createElement(`div`);return this._toggleAnchorTabIndex(this._enabled,t),t.classList.add(`cdk-visually-hidden`),t.classList.add(`cdk-focus-trap-anchor`),t.setAttribute(`aria-hidden`,`true`),t}_toggleAnchorTabIndex(t,e){t?e.setAttribute(`tabindex`,`0`):e.removeAttribute(`tabindex`)}toggleAnchors(t){this._startAnchor&&this._endAnchor&&(this._toggleAnchorTabIndex(t,this._startAnchor),this._toggleAnchorTabIndex(t,this._endAnchor))}_executeOnStable(t){ic(t,{injector:this._injector})}};var qe=new Map;var Ue=class s{_appId=p(ii);static _infix=`a${Math.floor(Math.random()*1e5).toString()}`;getId(t,e=!1){this._appId!==`ng`&&(t+=this._appId);let r=qe.get(t);return r===void 0?r=0:r++,qe.set(t,r),`${t}${e?s._infix+`-`:``}${r}`}static ɵfac=function(e){return new(e||s)};static ɵprov=K$2({token:s,factory:s.ɵfac})};var ce$1=class extends U$1{_focusTrapManager;_inertStrategy;get enabled(){return this._enabled}set enabled(t){this._enabled=t,this._enabled?this._focusTrapManager.register(this):this._focusTrapManager.deregister(this)}constructor(t,e,r,i,n,o,d,l){super(t,e,r,i,d.defer,l),this._focusTrapManager=n,this._inertStrategy=o,this._focusTrapManager.register(this)}destroy(){this._focusTrapManager.deregister(this),super.destroy()}_enable(){this._inertStrategy.preventFocus(this),this.toggleAnchors(!0)}_disable(){this._inertStrategy.allowFocus(this),this.toggleAnchors(!1)}};var le$1=class{_listener=null;preventFocus(t){this._listener&&t._document.removeEventListener(`focus`,this._listener,!0),this._listener=e=>this._trapFocus(t,e),t._ngZone.runOutsideAngular(()=>{t._document.addEventListener(`focus`,this._listener,!0)})}allowFocus(t){this._listener&&(t._document.removeEventListener(`focus`,this._listener,!0),this._listener=null)}_trapFocus(t,e){let r=e.target,i=t._element;r&&!i.contains(r)&&!r.closest?.(`div.cdk-overlay-pane`)&&setTimeout(()=>{t.enabled&&!i.contains(t._document.activeElement)&&t.focusFirstTabbableElement()})}};var Tt$1=new y$1(`FOCUS_TRAP_INERT_STRATEGY`);var It$1=(()=>{class s{_focusTrapStack=[];register(e){this._focusTrapStack=this._focusTrapStack.filter(i=>i!==e);let r=this._focusTrapStack;r.length&&r[r.length-1]._disable(),r.push(e),e._enable()}deregister(e){e._disable();let r=this._focusTrapStack,i=r.indexOf(e);i!==-1&&(r.splice(i,1),r.length&&r[r.length-1]._enable())}static ɵfac=function(r){return new(r||s)};static ɵprov=K$2({token:s,factory:s.ɵfac})}return s})();var He=(()=>{class s{_checker=p(H);_ngZone=p(me$1);_focusTrapManager=p(It$1);_document=p(Z$1);_inertStrategy;_injector=p(be$3);constructor(){let e=p(Tt$1,{optional:!0});this._inertStrategy=e||new le$1}create(e,r={defer:!1}){return new ce$1(e,this._checker,this._ngZone,this._document,this._focusTrapManager,this._inertStrategy,r,this._injector)}static ɵfac=function(r){return new(r||s)};static ɵprov=K$2({token:s,factory:s.ɵfac})}return s})();var vr=(()=>{class s{constructor(){this.counter=0}next(e){return`${e}-${++this.counter}`}static{this.ɵfac=function(r){return new(r||s)}}static{this.ɵprov=_({token:s,factory:s.ɵfac,providedIn:`root`})}}return s})();var _r=(()=>{class s{static{this.MOVED=[`role`,`id`,`tabindex`]}static{this.HOST_ROLE=`presentation`}constructor(){this.target=p(mt$2).nativeElement,this.host=p(mt$2,{skipSelf:!0}).nativeElement,this.platformId=p(si),this.destroyRef=p(De$2),ic(()=>{this.forward(),this.observe()})}ngOnInit(){this.forward()}forward(){for(let{name:e,value:r}of Array.from(this.host.attributes))e.startsWith(`aria-`)&&this.target.setAttribute(e,r);for(let e of s.MOVED){let r=this.host.getAttribute(e);r!==null&&(e===`role`&&r===s.HOST_ROLE||(this.target.setAttribute(e,r),this.host.removeAttribute(e)))}this.host.hasAttribute(`role`)||this.host.setAttribute(`role`,s.HOST_ROLE)}observe(){if(z2(this.platformId))return;let e=new MutationObserver(()=>this.forward());e.observe(this.host,{attributes:!0}),this.destroyRef.onDestroy(()=>e.disconnect())}static{this.ɵfac=function(r){return new(r||s)}}static{this.ɵdir=Ot$2({type:s,selectors:[[``,`bsForwardAria`,``]]})}}return s})();var yr=(()=>{class s{constructor(){this.elementRef=p(mt$2),this.trapFactory=p(He),this.interactivityChecker=p(H),this.destroyRef=p(De$2),this.active=Ir(!0,{alias:`bsOverlayFocus`}),this.initialFocus=Ir(`first`),this.returnFocus=Ir(!0),this.trap=null,this.restoreTo=null,zl(()=>{this.active()?this.engage():this.disengage()}),this.destroyRef.onDestroy(()=>this.disengage())}engage(){if(this.trap)return;this.restoreTo=typeof document<`u`&&document.activeElement instanceof HTMLElement?document.activeElement:null,this.trap=this.trapFactory.create(this.elementRef.nativeElement);let e=this.initialFocus();e instanceof HTMLElement?e.focus({preventScroll:!0}):e===`self`?this.elementRef.nativeElement.focus({preventScroll:!0}):e===`first`&&this.focusFirstTabbable()}focusFirstTabbable(){let e=this.elementRef.nativeElement,r=(e.ownerDocument??document).createTreeWalker(e,NodeFilter.SHOW_ELEMENT),i=r.currentNode;for(;i;){if(i instanceof HTMLElement&&i.tabIndex>=0&&!i.matches(`:disabled`)&&this.interactivityChecker.isFocusable(i,{ignoreVisibility:!0})){i.focus({preventScroll:!0});return}i=r.nextNode()}}disengage(){this.trap&&(this.trap.destroy(),this.trap=null,this.returnFocus()&&this.restoreTo&&typeof document<`u`&&document.contains(this.restoreTo)&&this.restoreTo.focus({preventScroll:!0}),this.restoreTo=null)}static{this.ɵfac=function(r){return new(r||s)}}static{this.ɵdir=Ot$2({type:s,selectors:[[``,`bsOverlayFocus`,``]],inputs:{active:[1,`bsOverlayFocus`,`active`],initialFocus:[1,`initialFocus`],returnFocus:[1,`returnFocus`]},exportAs:[`bsOverlayFocus`]})}}return s})();var kr=(()=>{class s{push(){return A.push(`bs-overlay-frame`)}release(e){A.release(e)}isTop(e){return A.isTop(e)}peek(){return A.peek()}static{this.ɵfac=function(r){return new(r||s)}}static{this.ɵprov=_({token:s,factory:s.ɵfac,providedIn:`root`})}}return s})();var Ar=(()=>{class s{constructor(){this.injector=p(be$3),this.host=p(mt$2),this.errorMessages=Ir(null)}ngDoCheck(){this.ngControl===void 0&&(this.ngControl=this.injector.get(C,null));let e=this.ngControl,r=this.host.nativeElement.firstElementChild;if(!e||!r)return;let i=!!e.invalid&&!!e.touched;this.mirror(r,`invalid`,i?``:null);let n=(e.control?.hasValidator(fe$1.required)??!1)||(e.control?.hasValidator(fe$1.requiredTrue)??!1);this.mirror(r,`required`,n?``:null),this.mirror(r,`error-text`,i?this.activeMessage(e):null)}activeMessage(e){let r=this.errorMessages();if(!r)return null;let i=Object.keys(e.errors??{}).find(n=>n in r);return i?r[i]:null}mirror(e,r,i){i===null?e.hasAttribute(r)&&e.removeAttribute(r):e.getAttribute(r)!==i&&e.setAttribute(r,i)}static{this.ɵfac=function(r){return new(r||s)}}static{this.ɵdir=Ot$2({type:s,selectors:[[``,`bsControlValidity`,``]],inputs:{errorMessages:[1,`errorMessages`]}})}}return s})();var de$1=s=>s??p$1;var Pe=X$3(`.form-check {
  display: block;
  min-height: 1.5rem;
  padding-left: 1.5em;
  margin-bottom: 0.125rem;
}
.form-check .form-check-input {
  float: left;
  margin-left: -1.5em;
}

.form-check-reverse {
  padding-right: 1.5em;
  padding-left: 0;
  text-align: right;
}
.form-check-reverse .form-check-input {
  float: right;
  margin-right: -1.5em;
  margin-left: 0;
}

.form-check-input {
  --bs-form-check-bg: var(--bs-body-bg);
  flex-shrink: 0;
  width: 1em;
  height: 1em;
  margin-top: 0.25em;
  vertical-align: top;
  appearance: none;
  background-color: var(--bs-form-check-bg);
  background-image: var(--bs-form-check-bg-image);
  background-repeat: no-repeat;
  background-position: center;
  background-size: contain;
  border: var(--bs-border-width) solid var(--bs-border-color);
  print-color-adjust: exact;
}
.form-check-input[type=checkbox] {
  border-radius: 0.25em;
}
.form-check-input[type=radio] {
  border-radius: 50%;
}
.form-check-input:active {
  filter: brightness(90%);
}
.form-check-input:focus {
  border-color: rgb(52.5490196078%, 71.568627451%, 99.6078431373%);
  outline: 0;
  box-shadow: 0 0 0 0.25rem rgba(13, 110, 253, 0.25);
}
.form-check-input:checked {
  background-color: #0d6efd;
  border-color: #0d6efd;
}
.form-check-input:checked[type=checkbox] {
  --bs-form-check-bg-image: url("data:image/svg+xml,%3csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 20 20'%3e%3cpath fill='none' stroke='%23fff' stroke-linecap='round' stroke-linejoin='round' stroke-width='3' d='m6 10 3 3 6-6'/%3e%3c/svg%3e");
}
.form-check-input:checked[type=radio] {
  --bs-form-check-bg-image: url("data:image/svg+xml,%3csvg xmlns='http://www.w3.org/2000/svg' viewBox='-4 -4 8 8'%3e%3ccircle r='2' fill='%23fff'/%3e%3c/svg%3e");
}
.form-check-input[type=checkbox]:indeterminate {
  background-color: #0d6efd;
  border-color: #0d6efd;
  --bs-form-check-bg-image: url("data:image/svg+xml,%3csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 20 20'%3e%3cpath fill='none' stroke='%23fff' stroke-linecap='round' stroke-linejoin='round' stroke-width='3' d='M6 10h8'/%3e%3c/svg%3e");
}
.form-check-input:disabled {
  pointer-events: none;
  filter: none;
  opacity: 0.5;
}
.form-check-input[disabled] ~ .form-check-label, .form-check-input:disabled ~ .form-check-label {
  cursor: default;
  opacity: 0.5;
}

.form-switch {
  padding-left: 2.5em;
}
.form-switch .form-check-input {
  --bs-form-switch-bg: url("data:image/svg+xml,%3csvg xmlns='http://www.w3.org/2000/svg' viewBox='-4 -4 8 8'%3e%3ccircle r='3' fill='rgba%280, 0, 0, 0.25%29'/%3e%3c/svg%3e");
  width: 2em;
  margin-left: -2.5em;
  background-image: var(--bs-form-switch-bg);
  background-position: left center;
  border-radius: 2em;
  transition: background-position 0.15s ease-in-out;
}
@media (prefers-reduced-motion: reduce) {
  .form-switch .form-check-input {
    transition: none;
  }
}
.form-switch .form-check-input:focus {
  --bs-form-switch-bg: url("data:image/svg+xml,%3csvg xmlns='http://www.w3.org/2000/svg' viewBox='-4 -4 8 8'%3e%3ccircle r='3' fill='rgb%2852.5490196078%, 71.568627451%, 99.6078431373%%29'/%3e%3c/svg%3e");
}
.form-switch .form-check-input:checked {
  background-position: right center;
  --bs-form-switch-bg: url("data:image/svg+xml,%3csvg xmlns='http://www.w3.org/2000/svg' viewBox='-4 -4 8 8'%3e%3ccircle r='3' fill='%23fff'/%3e%3c/svg%3e");
}
.form-switch.form-check-reverse {
  padding-right: 2.5em;
  padding-left: 0;
}
.form-switch.form-check-reverse .form-check-input {
  margin-right: -2.5em;
  margin-left: 0;
}

.form-check-inline {
  display: inline-block;
  margin-right: 1rem;
}

.btn-check {
  position: absolute;
  clip: rect(0, 0, 0, 0);
  pointer-events: none;
}
.btn-check[disabled] + .btn, .btn-check:disabled + .btn {
  pointer-events: none;
  filter: none;
  opacity: 0.65;
}

[data-bs-theme=dark] .form-switch .form-check-input:not(:checked):not(:focus) {
  --bs-form-switch-bg: url("data:image/svg+xml,%3csvg xmlns='http://www.w3.org/2000/svg' viewBox='-4 -4 8 8'%3e%3ccircle r='3' fill='rgba%28255, 255, 255, 0.25%29'/%3e%3c/svg%3e");
}

.form-check {
  display: flex;
  align-items: center;
  margin-bottom: 0;
  padding-left: 0;
}

.form-check .form-check-input {
  margin-top: 0;
  margin-left: 0;
  margin-right: 0.5em;
  vertical-align: middle;
}

.form-check.form-switch .form-check-input {
  float: none;
}`);var P=X$3(`.invalid-feedback {
  display: block;
  width: 100%;
  margin-top: 0.25rem;
  font-size: 0.875em;
  color: var(--bs-form-invalid-color, #dc3545);
}`);var he$1=X$3(`.btn {
  --bs-btn-padding-x: 0.75rem;
  --bs-btn-padding-y: 0.375rem;
  --bs-btn-font-family: ;
  --bs-btn-font-size: 1rem;
  --bs-btn-font-weight: 400;
  --bs-btn-line-height: 1.5;
  --bs-btn-color: var(--bs-body-color);
  --bs-btn-bg: transparent;
  --bs-btn-border-width: var(--bs-border-width);
  --bs-btn-border-color: transparent;
  --bs-btn-border-radius: var(--bs-border-radius);
  --bs-btn-hover-border-color: transparent;
  --bs-btn-box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.15), 0 1px 1px rgba(0, 0, 0, 0.075);
  --bs-btn-disabled-opacity: 0.65;
  --bs-btn-focus-box-shadow: 0 0 0 0.25rem rgba(var(--bs-btn-focus-shadow-rgb), .5);
  display: inline-block;
  padding: var(--bs-btn-padding-y) var(--bs-btn-padding-x);
  font-family: var(--bs-btn-font-family);
  font-size: var(--bs-btn-font-size);
  font-weight: var(--bs-btn-font-weight);
  line-height: var(--bs-btn-line-height);
  color: var(--bs-btn-color);
  text-align: center;
  text-decoration: none;
  vertical-align: middle;
  cursor: pointer;
  user-select: none;
  border: var(--bs-btn-border-width) solid var(--bs-btn-border-color);
  border-radius: var(--bs-btn-border-radius);
  background-color: var(--bs-btn-bg);
  transition: color 0.15s ease-in-out, background-color 0.15s ease-in-out, border-color 0.15s ease-in-out, box-shadow 0.15s ease-in-out;
}
@media (prefers-reduced-motion: reduce) {
  .btn {
    transition: none;
  }
}
.btn:hover {
  color: var(--bs-btn-hover-color);
  background-color: var(--bs-btn-hover-bg);
  border-color: var(--bs-btn-hover-border-color);
}
.btn-check + .btn:hover {
  color: var(--bs-btn-color);
  background-color: var(--bs-btn-bg);
  border-color: var(--bs-btn-border-color);
}
.btn:focus-visible {
  color: var(--bs-btn-hover-color);
  background-color: var(--bs-btn-hover-bg);
  border-color: var(--bs-btn-hover-border-color);
  outline: 0;
  box-shadow: var(--bs-btn-focus-box-shadow);
}
.btn-check:focus-visible + .btn {
  border-color: var(--bs-btn-hover-border-color);
  outline: 0;
  box-shadow: var(--bs-btn-focus-box-shadow);
}
.btn-check:checked + .btn, :not(.btn-check) + .btn:active, .btn:first-child:active, .btn.active, .btn.show {
  color: var(--bs-btn-active-color);
  background-color: var(--bs-btn-active-bg);
  border-color: var(--bs-btn-active-border-color);
}
.btn-check:checked + .btn:focus-visible, :not(.btn-check) + .btn:active:focus-visible, .btn:first-child:active:focus-visible, .btn.active:focus-visible, .btn.show:focus-visible {
  box-shadow: var(--bs-btn-focus-box-shadow);
}
.btn-check:checked:focus-visible + .btn {
  box-shadow: var(--bs-btn-focus-box-shadow);
}
.btn:disabled, .btn.disabled, fieldset:disabled .btn {
  color: var(--bs-btn-disabled-color);
  pointer-events: none;
  background-color: var(--bs-btn-disabled-bg);
  border-color: var(--bs-btn-disabled-border-color);
  opacity: var(--bs-btn-disabled-opacity);
}

.btn-primary {
  --bs-btn-color: #fff;
  --bs-btn-bg: #0d6efd;
  --bs-btn-border-color: #0d6efd;
  --bs-btn-hover-color: #fff;
  --bs-btn-hover-bg: rgb(4.3333333333%, 36.6666666667%, 84.3333333333%);
  --bs-btn-hover-border-color: rgb(4.0784313725%, 34.5098039216%, 79.3725490196%);
  --bs-btn-focus-shadow-rgb: 49, 132, 253;
  --bs-btn-active-color: #fff;
  --bs-btn-active-bg: rgb(4.0784313725%, 34.5098039216%, 79.3725490196%);
  --bs-btn-active-border-color: rgb(3.8235294118%, 32.3529411765%, 74.4117647059%);
  --bs-btn-active-shadow: inset 0 3px 5px rgba(0, 0, 0, 0.125);
  --bs-btn-disabled-color: #fff;
  --bs-btn-disabled-bg: #0d6efd;
  --bs-btn-disabled-border-color: #0d6efd;
}

.btn-secondary {
  --bs-btn-color: #fff;
  --bs-btn-bg: #6c757d;
  --bs-btn-border-color: #6c757d;
  --bs-btn-hover-color: #fff;
  --bs-btn-hover-bg: rgb(36%, 39%, 41.6666666667%);
  --bs-btn-hover-border-color: rgb(33.8823529412%, 36.7058823529%, 39.2156862745%);
  --bs-btn-focus-shadow-rgb: 130, 138, 145;
  --bs-btn-active-color: #fff;
  --bs-btn-active-bg: rgb(33.8823529412%, 36.7058823529%, 39.2156862745%);
  --bs-btn-active-border-color: rgb(31.7647058824%, 34.4117647059%, 36.7647058824%);
  --bs-btn-active-shadow: inset 0 3px 5px rgba(0, 0, 0, 0.125);
  --bs-btn-disabled-color: #fff;
  --bs-btn-disabled-bg: #6c757d;
  --bs-btn-disabled-border-color: #6c757d;
}

.btn-success {
  --bs-btn-color: #fff;
  --bs-btn-bg: #198754;
  --bs-btn-border-color: #198754;
  --bs-btn-hover-color: #fff;
  --bs-btn-hover-bg: rgb(8.3333333333%, 45%, 28%);
  --bs-btn-hover-border-color: rgb(7.8431372549%, 42.3529411765%, 26.3529411765%);
  --bs-btn-focus-shadow-rgb: 60, 153, 110;
  --bs-btn-active-color: #fff;
  --bs-btn-active-bg: rgb(7.8431372549%, 42.3529411765%, 26.3529411765%);
  --bs-btn-active-border-color: rgb(7.3529411765%, 39.7058823529%, 24.7058823529%);
  --bs-btn-active-shadow: inset 0 3px 5px rgba(0, 0, 0, 0.125);
  --bs-btn-disabled-color: #fff;
  --bs-btn-disabled-bg: #198754;
  --bs-btn-disabled-border-color: #198754;
}

.btn-info {
  --bs-btn-color: #000;
  --bs-btn-bg: #0dcaf0;
  --bs-btn-border-color: #0dcaf0;
  --bs-btn-hover-color: #000;
  --bs-btn-hover-bg: rgb(19.3333333333%, 82.3333333333%, 95%);
  --bs-btn-hover-border-color: rgb(14.5882352941%, 81.2941176471%, 94.7058823529%);
  --bs-btn-focus-shadow-rgb: 11, 172, 204;
  --bs-btn-active-color: #000;
  --bs-btn-active-bg: rgb(24.0784313725%, 83.3725490196%, 95.2941176471%);
  --bs-btn-active-border-color: rgb(14.5882352941%, 81.2941176471%, 94.7058823529%);
  --bs-btn-active-shadow: inset 0 3px 5px rgba(0, 0, 0, 0.125);
  --bs-btn-disabled-color: #000;
  --bs-btn-disabled-bg: #0dcaf0;
  --bs-btn-disabled-border-color: #0dcaf0;
}

.btn-warning {
  --bs-btn-color: #000;
  --bs-btn-bg: #ffc107;
  --bs-btn-border-color: #ffc107;
  --bs-btn-hover-color: #000;
  --bs-btn-hover-bg: rgb(100%, 79.3333333333%, 17.3333333333%);
  --bs-btn-hover-border-color: rgb(100%, 78.1176470588%, 12.4705882353%);
  --bs-btn-focus-shadow-rgb: 217, 164, 6;
  --bs-btn-active-color: #000;
  --bs-btn-active-bg: rgb(100%, 80.5490196078%, 22.1960784314%);
  --bs-btn-active-border-color: rgb(100%, 78.1176470588%, 12.4705882353%);
  --bs-btn-active-shadow: inset 0 3px 5px rgba(0, 0, 0, 0.125);
  --bs-btn-disabled-color: #000;
  --bs-btn-disabled-bg: #ffc107;
  --bs-btn-disabled-border-color: #ffc107;
}

.btn-danger {
  --bs-btn-color: #fff;
  --bs-btn-bg: #dc3545;
  --bs-btn-border-color: #dc3545;
  --bs-btn-hover-color: #fff;
  --bs-btn-hover-bg: rgb(73.3333333333%, 17.6666666667%, 23%);
  --bs-btn-hover-border-color: rgb(69.0196078431%, 16.6274509804%, 21.6470588235%);
  --bs-btn-focus-shadow-rgb: 225, 83, 97;
  --bs-btn-active-color: #fff;
  --bs-btn-active-bg: rgb(69.0196078431%, 16.6274509804%, 21.6470588235%);
  --bs-btn-active-border-color: rgb(64.7058823529%, 15.5882352941%, 20.2941176471%);
  --bs-btn-active-shadow: inset 0 3px 5px rgba(0, 0, 0, 0.125);
  --bs-btn-disabled-color: #fff;
  --bs-btn-disabled-bg: #dc3545;
  --bs-btn-disabled-border-color: #dc3545;
}

.btn-light {
  --bs-btn-color: #000;
  --bs-btn-bg: #f8f9fa;
  --bs-btn-border-color: #f8f9fa;
  --bs-btn-hover-color: #000;
  --bs-btn-hover-bg: rgb(82.6666666667%, 83%, 83.3333333333%);
  --bs-btn-hover-border-color: rgb(77.8039215686%, 78.1176470588%, 78.431372549%);
  --bs-btn-focus-shadow-rgb: 211, 212, 213;
  --bs-btn-active-color: #000;
  --bs-btn-active-bg: rgb(77.8039215686%, 78.1176470588%, 78.431372549%);
  --bs-btn-active-border-color: rgb(72.9411764706%, 73.2352941176%, 73.5294117647%);
  --bs-btn-active-shadow: inset 0 3px 5px rgba(0, 0, 0, 0.125);
  --bs-btn-disabled-color: #000;
  --bs-btn-disabled-bg: #f8f9fa;
  --bs-btn-disabled-border-color: #f8f9fa;
}

.btn-dark {
  --bs-btn-color: #fff;
  --bs-btn-bg: #212529;
  --bs-btn-border-color: #212529;
  --bs-btn-hover-color: #fff;
  --bs-btn-hover-bg: rgb(26%, 27.3333333333%, 28.6666666667%);
  --bs-btn-hover-border-color: rgb(21.6470588235%, 23.0588235294%, 24.4705882353%);
  --bs-btn-focus-shadow-rgb: 66, 70, 73;
  --bs-btn-active-color: #fff;
  --bs-btn-active-bg: rgb(30.3529411765%, 31.6078431373%, 32.862745098%);
  --bs-btn-active-border-color: rgb(21.6470588235%, 23.0588235294%, 24.4705882353%);
  --bs-btn-active-shadow: inset 0 3px 5px rgba(0, 0, 0, 0.125);
  --bs-btn-disabled-color: #fff;
  --bs-btn-disabled-bg: #212529;
  --bs-btn-disabled-border-color: #212529;
}

.btn-outline-primary {
  --bs-btn-color: #0d6efd;
  --bs-btn-border-color: #0d6efd;
  --bs-btn-hover-color: #fff;
  --bs-btn-hover-bg: #0d6efd;
  --bs-btn-hover-border-color: #0d6efd;
  --bs-btn-focus-shadow-rgb: 13, 110, 253;
  --bs-btn-active-color: #fff;
  --bs-btn-active-bg: #0d6efd;
  --bs-btn-active-border-color: #0d6efd;
  --bs-btn-active-shadow: inset 0 3px 5px rgba(0, 0, 0, 0.125);
  --bs-btn-disabled-color: #0d6efd;
  --bs-btn-disabled-bg: transparent;
  --bs-btn-disabled-border-color: #0d6efd;
  --bs-gradient: none;
}

.btn-outline-secondary {
  --bs-btn-color: #6c757d;
  --bs-btn-border-color: #6c757d;
  --bs-btn-hover-color: #fff;
  --bs-btn-hover-bg: #6c757d;
  --bs-btn-hover-border-color: #6c757d;
  --bs-btn-focus-shadow-rgb: 108, 117, 125;
  --bs-btn-active-color: #fff;
  --bs-btn-active-bg: #6c757d;
  --bs-btn-active-border-color: #6c757d;
  --bs-btn-active-shadow: inset 0 3px 5px rgba(0, 0, 0, 0.125);
  --bs-btn-disabled-color: #6c757d;
  --bs-btn-disabled-bg: transparent;
  --bs-btn-disabled-border-color: #6c757d;
  --bs-gradient: none;
}

.btn-outline-success {
  --bs-btn-color: #198754;
  --bs-btn-border-color: #198754;
  --bs-btn-hover-color: #fff;
  --bs-btn-hover-bg: #198754;
  --bs-btn-hover-border-color: #198754;
  --bs-btn-focus-shadow-rgb: 25, 135, 84;
  --bs-btn-active-color: #fff;
  --bs-btn-active-bg: #198754;
  --bs-btn-active-border-color: #198754;
  --bs-btn-active-shadow: inset 0 3px 5px rgba(0, 0, 0, 0.125);
  --bs-btn-disabled-color: #198754;
  --bs-btn-disabled-bg: transparent;
  --bs-btn-disabled-border-color: #198754;
  --bs-gradient: none;
}

.btn-outline-info {
  --bs-btn-color: #0dcaf0;
  --bs-btn-border-color: #0dcaf0;
  --bs-btn-hover-color: #000;
  --bs-btn-hover-bg: #0dcaf0;
  --bs-btn-hover-border-color: #0dcaf0;
  --bs-btn-focus-shadow-rgb: 13, 202, 240;
  --bs-btn-active-color: #000;
  --bs-btn-active-bg: #0dcaf0;
  --bs-btn-active-border-color: #0dcaf0;
  --bs-btn-active-shadow: inset 0 3px 5px rgba(0, 0, 0, 0.125);
  --bs-btn-disabled-color: #0dcaf0;
  --bs-btn-disabled-bg: transparent;
  --bs-btn-disabled-border-color: #0dcaf0;
  --bs-gradient: none;
}

.btn-outline-warning {
  --bs-btn-color: #ffc107;
  --bs-btn-border-color: #ffc107;
  --bs-btn-hover-color: #000;
  --bs-btn-hover-bg: #ffc107;
  --bs-btn-hover-border-color: #ffc107;
  --bs-btn-focus-shadow-rgb: 255, 193, 7;
  --bs-btn-active-color: #000;
  --bs-btn-active-bg: #ffc107;
  --bs-btn-active-border-color: #ffc107;
  --bs-btn-active-shadow: inset 0 3px 5px rgba(0, 0, 0, 0.125);
  --bs-btn-disabled-color: #ffc107;
  --bs-btn-disabled-bg: transparent;
  --bs-btn-disabled-border-color: #ffc107;
  --bs-gradient: none;
}

.btn-outline-danger {
  --bs-btn-color: #dc3545;
  --bs-btn-border-color: #dc3545;
  --bs-btn-hover-color: #fff;
  --bs-btn-hover-bg: #dc3545;
  --bs-btn-hover-border-color: #dc3545;
  --bs-btn-focus-shadow-rgb: 220, 53, 69;
  --bs-btn-active-color: #fff;
  --bs-btn-active-bg: #dc3545;
  --bs-btn-active-border-color: #dc3545;
  --bs-btn-active-shadow: inset 0 3px 5px rgba(0, 0, 0, 0.125);
  --bs-btn-disabled-color: #dc3545;
  --bs-btn-disabled-bg: transparent;
  --bs-btn-disabled-border-color: #dc3545;
  --bs-gradient: none;
}

.btn-outline-light {
  --bs-btn-color: #f8f9fa;
  --bs-btn-border-color: #f8f9fa;
  --bs-btn-hover-color: #000;
  --bs-btn-hover-bg: #f8f9fa;
  --bs-btn-hover-border-color: #f8f9fa;
  --bs-btn-focus-shadow-rgb: 248, 249, 250;
  --bs-btn-active-color: #000;
  --bs-btn-active-bg: #f8f9fa;
  --bs-btn-active-border-color: #f8f9fa;
  --bs-btn-active-shadow: inset 0 3px 5px rgba(0, 0, 0, 0.125);
  --bs-btn-disabled-color: #f8f9fa;
  --bs-btn-disabled-bg: transparent;
  --bs-btn-disabled-border-color: #f8f9fa;
  --bs-gradient: none;
}

.btn-outline-dark {
  --bs-btn-color: #212529;
  --bs-btn-border-color: #212529;
  --bs-btn-hover-color: #fff;
  --bs-btn-hover-bg: #212529;
  --bs-btn-hover-border-color: #212529;
  --bs-btn-focus-shadow-rgb: 33, 37, 41;
  --bs-btn-active-color: #fff;
  --bs-btn-active-bg: #212529;
  --bs-btn-active-border-color: #212529;
  --bs-btn-active-shadow: inset 0 3px 5px rgba(0, 0, 0, 0.125);
  --bs-btn-disabled-color: #212529;
  --bs-btn-disabled-bg: transparent;
  --bs-btn-disabled-border-color: #212529;
  --bs-gradient: none;
}

.btn-link {
  --bs-btn-font-weight: 400;
  --bs-btn-color: var(--bs-link-color);
  --bs-btn-bg: transparent;
  --bs-btn-border-color: transparent;
  --bs-btn-hover-color: var(--bs-link-hover-color);
  --bs-btn-hover-border-color: transparent;
  --bs-btn-active-color: var(--bs-link-hover-color);
  --bs-btn-active-border-color: transparent;
  --bs-btn-disabled-color: #6c757d;
  --bs-btn-disabled-border-color: transparent;
  --bs-btn-box-shadow: 0 0 0 #000;
  --bs-btn-focus-shadow-rgb: 49, 132, 253;
  text-decoration: underline;
}
.btn-link:focus-visible {
  color: var(--bs-btn-color);
}
.btn-link:hover {
  color: var(--bs-btn-hover-color);
}

.btn-lg {
  --bs-btn-padding-y: 0.5rem;
  --bs-btn-padding-x: 1rem;
  --bs-btn-font-size: 1.25rem;
  --bs-btn-border-radius: var(--bs-border-radius-lg);
}

.btn-sm {
  --bs-btn-padding-y: 0.25rem;
  --bs-btn-padding-x: 0.5rem;
  --bs-btn-font-size: 0.875rem;
  --bs-btn-border-radius: var(--bs-border-radius-sm);
}

.btn-check {
  position: absolute;
  clip: rect(0, 0, 0, 0);
  pointer-events: none;
}

.btn-check[disabled] + .btn,
.btn-check:disabled + .btn {
  pointer-events: none;
  filter: none;
  opacity: 0.65;
}

:host {
  display: inline-block;
  vertical-align: middle;
}`);var Ve=new Set([`primary`,`secondary`,`success`,`danger`,`warning`,`info`,`light`,`dark`,`outline-primary`,`outline-secondary`,`outline-success`,`outline-danger`,`outline-warning`,`outline-info`,`outline-light`,`outline-dark`]);var je=0;var be$1=class extends R(b$1){constructor(){super(...arguments),this._checked=!1,this._disabled=!1,this._name=null,this._value=null,this._color=`primary`,this._inputLabel=null,this._errorText=null,this.hostAria=new v(this,{referenceTarget:()=>this._inputRef.value??null,describedByExtras:()=>I(this.renderRoot)}),this._inputId=`mp-toggle-button-${++je}`,this._errorId=`mp-toggle-button-${je}-error`,this._inputRef=k(),this.onInputChange=t=>{let e=t.target.checked;this._checked=e,this.reflectBoolean(`checked`,e),this.dispatchEvent(new CustomEvent(`change`,{detail:{checked:e,value:this._value},bubbles:!0,composed:!0}))}}static{this.styles=[he$1,P]}static{this.shadowRootOptions=s(r({},b$1.shadowRootOptions),{delegatesFocus:!0})}static get observedAttributes(){return[...super.observedAttributes??[],`invalid`,`required`,`error-text`,`checked`,`disabled`,`name`,`value`,`color`,`aria-label`,`input-label`,`aria-labelledby`,`aria-describedby`]}get inputLabel(){return this._inputLabel}set inputLabel(t){let e=t??null;this._inputLabel!==e&&(this._inputLabel=e,this.requestUpdate())}get errorText(){return this._errorText}set errorText(t){let e=t??null;this._errorText!==e&&(this._errorText=e,this.requestUpdate())}get checked(){return this._checked}set checked(t){let e=!!t;this._checked!==e&&(this._checked=e,this.reflectBoolean(`checked`,e),this.requestUpdate())}get disabled(){return this._disabled}set disabled(t){let e=!!t;this._disabled!==e&&(this._disabled=e,this.reflectBoolean(`disabled`,e),this.requestUpdate())}get name(){return this._name}set name(t){let e=t??null;this._name!==e&&(this._name=e,this.requestUpdate())}get value(){return this._value}set value(t){let e=t??null;this._value!==e&&(this._value=e,this.requestUpdate())}get color(){return this._color}set color(t){!Ve.has(t)||this._color===t||(this._color=t,this.requestUpdate())}attributeChangedCallback(t,e,r){switch(super.attributeChangedCallback(t,e,r),t){case`invalid`:case`required`:this.requestUpdate();break;case`error-text`:this._errorText=r,this.requestUpdate();break;case`checked`:this._checked=r!==null,this.requestUpdate();break;case`disabled`:this._disabled=r!==null,this.requestUpdate();break;case`name`:this._name=r,this.requestUpdate();break;case`value`:this._value=r,this.requestUpdate();break;case`color`:r&&Ve.has(r)&&(this._color=r,this.requestUpdate());break;case`aria-label`:this.requestUpdate();break;case`input-label`:this._inputLabel=r,this.requestUpdate();break;case`aria-labelledby`:case`aria-describedby`:this.hostAria.syncReferences();break}}updated(){this.syncFormValue(),this.hostAria.syncReferences()}render(){let t=T(this._errorId,this._errorText,this.hasAttribute(`invalid`));return Ht$2`
      <input
        ${g(this._inputRef)}
        type="checkbox"
        class="btn-check"
        id=${this._inputId}
        .checked=${this._checked}
        ?disabled=${this._disabled}
          aria-invalid=${this.hasAttribute(`invalid`)?`true`:p$1}
          aria-required=${this.hasAttribute(`required`)?`true`:p$1}
        aria-errormessage=${t.id}
        aria-describedby=${t.id}
        name=${this._name??p$1}
        value=${this._value??p$1}
        aria-label=${this.getAttribute(`aria-label`)??this._inputLabel??p$1}
        @change=${this.onInputChange}
      />
      <label class="btn btn-${this._color}" for=${this._inputId}>
        <slot></slot>
      </label>
      ${t.node}
    `}reflectBoolean(t,e){e?this.setAttribute(t,``):this.removeAttribute(t)}formValue(){return this._checked?this._value??`on`:null}formReset(){this._checked=!1,this.hasAttribute(`checked`)&&this.removeAttribute(`checked`),this.requestUpdate()}formRestore(t){this._checked=t!=null,this.requestUpdate()}formValidityAnchor(){return this._inputRef.value??null}};typeof customElements<`u`&&!customElements.get(`mp-toggle-button`)&&customElements.define(`mp-toggle-button`,be$1);var Ke=new Set([`checkbox`,`switch`,`toggle_button`]);var We=new Set([`primary`,`secondary`,`success`,`danger`,`warning`,`info`,`light`,`dark`,`outline-primary`,`outline-secondary`,`outline-success`,`outline-danger`,`outline-warning`,`outline-info`,`outline-light`,`outline-dark`]);var Ge$1=0;var ue=class extends R(b$1){constructor(){super(...arguments),this._type=`checkbox`,this._checked=!1,this._indeterminate=!1,this._disabled=!1,this._name=null,this._value=null,this._color=`primary`,this._inputLabel=null,this._errorText=null,this.hostAria=new v(this,{referenceTarget:()=>this._inputRef.value??null,describedByExtras:()=>I(this.renderRoot)}),this._inputId=`mp-checkbox-${++Ge$1}`,this._errorId=`mp-checkbox-${Ge$1}-error`,this._inputRef=k(),this.onInputChange=t=>{let e=t.target,r=e.checked,i=e.indeterminate;this._checked=r,this._indeterminate=i,this.reflectBoolean(`checked`,r),this.reflectBoolean(`indeterminate`,i),this.dispatchEvent(new CustomEvent(`change`,{detail:{checked:r,indeterminate:i,value:this._value},bubbles:!0,composed:!0}))}}static{this.styles=[Pe,he$1,P]}static{this.shadowRootOptions=s(r({},b$1.shadowRootOptions),{delegatesFocus:!0})}static get observedAttributes(){return[...super.observedAttributes??[],`invalid`,`required`,`error-text`,`type`,`checked`,`indeterminate`,`disabled`,`name`,`value`,`color`,`aria-label`,`input-label`,`aria-labelledby`,`aria-describedby`]}get inputLabel(){return this._inputLabel}set inputLabel(t){let e=t??null;this._inputLabel!==e&&(this._inputLabel=e,this.requestUpdate())}get errorText(){return this._errorText}set errorText(t){let e=t??null;this._errorText!==e&&(this._errorText=e,this.requestUpdate())}get type(){return this._type}set type(t){!Ke.has(t)||this._type===t||(this._type=t,this.requestUpdate())}get checked(){return this._checked}set checked(t){let e=!!t;this._checked!==e&&(this._checked=e,this.reflectBoolean(`checked`,e),this.requestUpdate())}get indeterminate(){return this._indeterminate}set indeterminate(t){let e=!!t;this._indeterminate!==e&&(this._indeterminate=e,this.reflectBoolean(`indeterminate`,e),this.requestUpdate())}get disabled(){return this._disabled}set disabled(t){let e=!!t;this._disabled!==e&&(this._disabled=e,this.reflectBoolean(`disabled`,e),this.requestUpdate())}get name(){return this._name}set name(t){let e=t??null;this._name!==e&&(this._name=e,this.requestUpdate())}get value(){return this._value}set value(t){let e=t??null;this._value!==e&&(this._value=e,this.requestUpdate())}get color(){return this._color}set color(t){!We.has(t)||this._color===t||(this._color=t,this.requestUpdate())}attributeChangedCallback(t,e,r){switch(super.attributeChangedCallback(t,e,r),t){case`invalid`:case`required`:this.requestUpdate();break;case`error-text`:this._errorText=r,this.requestUpdate();break;case`type`:r&&Ke.has(r)&&(this._type=r,this.requestUpdate());break;case`checked`:this._checked=r!==null,this.requestUpdate();break;case`indeterminate`:this._indeterminate=r!==null,this.requestUpdate();break;case`disabled`:this._disabled=r!==null,this.requestUpdate();break;case`name`:this._name=r,this.requestUpdate();break;case`value`:this._value=r,this.requestUpdate();break;case`color`:r&&We.has(r)&&(this._color=r,this.requestUpdate());break;case`aria-label`:this.requestUpdate();break;case`input-label`:this._inputLabel=r,this.requestUpdate();break;case`aria-labelledby`:case`aria-describedby`:this.hostAria.syncReferences();break}}render(){return this._type===`toggle_button`?this.renderToggleButton():this.renderCheckOrSwitch()}updated(){let t=this._inputRef.value;t&&(t.indeterminate=this._indeterminate&&this._type!==`toggle_button`),this.hostAria.syncReferences(),this.syncFormValue(),this.setFormValidity({valueMissing:this.hasAttribute(`required`)&&!this._checked},`Please check this box.`)}renderCheckOrSwitch(){let t=this._type===`switch`,e=this.getAttribute(`aria-label`)??this._inputLabel??void 0,r=this.errorFeedback();return Ht$2`
      <label class=${t?`form-check form-switch`:`form-check`}>
        <input
          ${g(this._inputRef)}
          type="checkbox"
          class="form-check-input"
          id=${this._inputId}
          .checked=${this._checked}
          ?disabled=${this._disabled}
          aria-invalid=${this.hasAttribute(`invalid`)?`true`:p$1}
          aria-required=${this.hasAttribute(`required`)?`true`:p$1}
          aria-errormessage=${r.id}
          aria-describedby=${r.id}
          name=${this._name??p$1}
          value=${this._value??p$1}
          role=${t?`switch`:p$1}
          aria-checked=${this._indeterminate?`mixed`:p$1}
          aria-label=${de$1(e)}
          @change=${this.onInputChange}
        />
        <span class="form-check-label"><slot></slot></span>
      </label>
      ${r.node}
    `}errorFeedback(){return T(this._errorId,this._errorText,this.hasAttribute(`invalid`))}renderToggleButton(){let t=this.getAttribute(`aria-label`)??this._inputLabel??void 0,e=this.errorFeedback();return Ht$2`
      <input
        ${g(this._inputRef)}
        type="checkbox"
        class="btn-check"
        id=${this._inputId}
        .checked=${this._checked}
        ?disabled=${this._disabled}
          aria-invalid=${this.hasAttribute(`invalid`)?`true`:p$1}
          aria-required=${this.hasAttribute(`required`)?`true`:p$1}
        aria-errormessage=${e.id}
        aria-describedby=${e.id}
        name=${this._name??p$1}
        value=${this._value??p$1}
        role="button"
        aria-pressed=${this._checked?`true`:`false`}
        aria-label=${de$1(t)}
        @change=${this.onInputChange}
      />
      <label class="btn btn-${this._color}" for=${this._inputId}>
        <slot></slot>
      </label>
      ${e.node}
    `}reflectBoolean(t,e){e?this.setAttribute(t,``):this.removeAttribute(t)}formValue(){return this._checked?this._value??`on`:null}formReset(){this._checked=!1,this._indeterminate=!1,this.reflectBoolean(`checked`,!1),this.reflectBoolean(`indeterminate`,!1),this.requestUpdate()}formRestore(t){this._checked=t!=null,this.reflectBoolean(`checked`,this._checked),this.requestUpdate()}formValidityAnchor(){return this._inputRef.value??null}};typeof customElements<`u`&&!customElements.get(`mp-checkbox`)&&customElements.define(`mp-checkbox`,ue);var Qe=new Map;function Gt(n,i){let e=Qe.get(n);return e||(e=new Set(aD(n)?.inputs.map(t=>t.templateName)??[]),Qe.set(n,e)),Object.fromEntries(Object.entries(i).filter(([t])=>e.has(t)))}function Xt(n){return n?.value??n?.object??n?.objects}function Yt(n){return n?.value}var Ot=new y$1(`SparkAttributeRenderers`,{factory:()=>[]});function ss(n){return{provide:Ot,useValue:n}}var Ge=(e=>(e[e.Query=1]=`Query`,e[e.PersistentObject=2]=`PersistentObject`,e))(Ge||{});function is(n,i){if(n===void 0)return!0;if(typeof n==`string`){let e=n.split(`,`).map(s=>s.trim()),t=Ge[i];return e.includes(t)}return(n&i)===i}function Xe(n){if(!n)return{};let i={};for(let e of n.attributes??[])i[e.name]=St(e);return typeof n.breadcrumb==`string`&&n.breadcrumb!==``&&(i[fe]=n.breadcrumb),i}function St(n){return n.dataType===`AsDetail`?n.isArray?(n.objects??[]).map(i=>Xe(i)):n.object?Xe(n.object):null:n.value}var fe=`__sparkBreadcrumb`;function rs(n,i){let e=n?.[fe];if(typeof e!=`string`||e.trim()===``)return null;if(i){if(e===i.slice(i.lastIndexOf(`.`)+1))return null}return e}var Rt=`__sparkBreadcrumbs`;function Ye(n){if(!n)return{};let i={},e;for(let t of n.attributes??[])i[t.name]=It(t),t.dataType===`Reference`&&!t.isArray&&typeof t.breadcrumb==`string`&&t.breadcrumb!==``&&((e??={})[t.name]=t.breadcrumb);return e&&(i[`__sparkBreadcrumbs`]=e),typeof n.breadcrumb==`string`&&n.breadcrumb!==``&&(i[fe]=n.breadcrumb),i}function It(n){return n.dataType===`AsDetail`?n.isArray?(n.objects??[]).map(i=>Ye(i)):n.object?Ye(n.object):null:n.value}function Je(n,i,e){let t=(i.attributes??[]).map(s=>Nt(s,n?.[s.name],e));return{id:n?.Id??n?.id??``,name:i.name,objectTypeId:i.id,attributes:t}}function Nt(n,i,e){let t={id:n.id,name:n.name,label:n.label,dataType:n.dataType,isArray:n.isArray,isRequired:n.isRequired,isVisible:n.isVisible,isReadOnly:n.isReadOnly,order:n.order,rules:n.rules??[],isValueChanged:!0};if(n.dataType===`AsDetail`){t.value=null,t.asDetailType=n.asDetailType;let s=n.asDetailType?e(n.asDetailType):void 0;if(!s)return t.object=null,t.objects=n.isArray?[]:null,t;if(n.isArray)t.objects=(Array.isArray(i)?i:[]).map(r=>Je(r??{},s,e));else t.object=i?Je(i,s,e):null;return t}return t.value=i,t}var kt=[`*`];var Ze=(()=>{class n{static{this.ɵfac=function(t){return new(t||n)}}static{this.ɵcmp=xi({type:n,selectors:[[`bs-container`]],ngContentSelectors:kt,decls:1,vars:0,template:function(t,s){t&1&&(QT(),KT(0))},styles:[`[_nghost-%COMP%]     .container, [_nghost-%COMP%]     .container-fluid, [_nghost-%COMP%]     .container-xxl, [_nghost-%COMP%]     .container-xl, [_nghost-%COMP%]     .container-lg, [_nghost-%COMP%]     .container-md, [_nghost-%COMP%]     .container-sm{--%NS%bs-gutter-x: 1.5rem;--%NS%bs-gutter-y: 0;width:100%;padding-right:calc(var(--%NS%bs-gutter-x) * .5);padding-left:calc(var(--%NS%bs-gutter-x) * .5);margin-right:auto;margin-left:auto}@media(min-width:576px){[_nghost-%COMP%]     .container-sm, [_nghost-%COMP%]     .container{max-width:540px}}@media(min-width:768px){[_nghost-%COMP%]     .container-md, [_nghost-%COMP%]     .container-sm, [_nghost-%COMP%]     .container{max-width:720px}}@media(min-width:992px){[_nghost-%COMP%]     .container-lg, [_nghost-%COMP%]     .container-md, [_nghost-%COMP%]     .container-sm, [_nghost-%COMP%]     .container{max-width:960px}}@media(min-width:1200px){[_nghost-%COMP%]     .container-xl, [_nghost-%COMP%]     .container-lg, [_nghost-%COMP%]     .container-md, [_nghost-%COMP%]     .container-sm, [_nghost-%COMP%]     .container{max-width:1140px}}@media(min-width:1400px){[_nghost-%COMP%]     .container-xxl, [_nghost-%COMP%]     .container-xl, [_nghost-%COMP%]     .container-lg, [_nghost-%COMP%]     .container-md, [_nghost-%COMP%]     .container-sm, [_nghost-%COMP%]     .container{max-width:1320px}}[_nghost-%COMP%]{display:contents}`]})}}return n})();var Et=[`*`];var ps=(()=>{class n{constructor(){this.stopFullWidthAt=Ir(`sm`),this.containerClass=On(()=>{let e=this.stopFullWidthAt();switch(e){case`sm`:return`container`;case`never`:return`container-fluid`;default:return`container-${e}`}})}static{this.ɵfac=function(t){return new(t||n)}}static{this.ɵcmp=xi({type:n,selectors:[[`bs-grid`]],inputs:{stopFullWidthAt:[1,`stopFullWidthAt`]},ngContentSelectors:Et,decls:3,vars:2,template:function(t,s){t&1&&(QT(),Xa(0,`bs-container`)(1,`div`),KT(2),Af()()),t&2&&(kb(),Lf(s.containerClass()))},dependencies:[Ze],styles:[`[_nghost-%COMP%]     :root{--%NS%bs-breakpoint-xs: 0;--%NS%bs-breakpoint-sm: 576px;--%NS%bs-breakpoint-md: 768px;--%NS%bs-breakpoint-lg: 992px;--%NS%bs-breakpoint-xl: 1200px;--%NS%bs-breakpoint-xxl: 1400px}[_nghost-%COMP%]     .row{--%NS%bs-gutter-x: 1.5rem;--%NS%bs-gutter-y: 0;display:flex;flex-wrap:wrap;margin-top:calc(-1 * var(--%NS%bs-gutter-y));margin-right:calc(-.5 * var(--%NS%bs-gutter-x));margin-left:calc(-.5 * var(--%NS%bs-gutter-x))}[_nghost-%COMP%]     .row>*{flex-shrink:0;width:100%;max-width:100%;padding-right:calc(var(--%NS%bs-gutter-x) * .5);padding-left:calc(var(--%NS%bs-gutter-x) * .5);margin-top:var(--%NS%bs-gutter-y)}[_nghost-%COMP%]     .col{flex:1 0 0}[_nghost-%COMP%]     .row-cols-auto>*{flex:0 0 auto;width:auto}[_nghost-%COMP%]     .row-cols-1>*{flex:0 0 auto;width:100%}[_nghost-%COMP%]     .row-cols-2>*{flex:0 0 auto;width:50%}[_nghost-%COMP%]     .row-cols-3>*{flex:0 0 auto;width:33.33333333%}[_nghost-%COMP%]     .row-cols-4>*{flex:0 0 auto;width:25%}[_nghost-%COMP%]     .row-cols-5>*{flex:0 0 auto;width:20%}[_nghost-%COMP%]     .row-cols-6>*{flex:0 0 auto;width:16.66666667%}[_nghost-%COMP%]     .col-auto{flex:0 0 auto;width:auto}[_nghost-%COMP%]     .col-1{flex:0 0 auto;width:8.33333333%}[_nghost-%COMP%]     .col-2{flex:0 0 auto;width:16.66666667%}[_nghost-%COMP%]     .col-3{flex:0 0 auto;width:25%}[_nghost-%COMP%]     .col-4{flex:0 0 auto;width:33.33333333%}[_nghost-%COMP%]     .col-5{flex:0 0 auto;width:41.66666667%}[_nghost-%COMP%]     .col-6{flex:0 0 auto;width:50%}[_nghost-%COMP%]     .col-7{flex:0 0 auto;width:58.33333333%}[_nghost-%COMP%]     .col-8{flex:0 0 auto;width:66.66666667%}[_nghost-%COMP%]     .col-9{flex:0 0 auto;width:75%}[_nghost-%COMP%]     .col-10{flex:0 0 auto;width:83.33333333%}[_nghost-%COMP%]     .col-11{flex:0 0 auto;width:91.66666667%}[_nghost-%COMP%]     .col-12{flex:0 0 auto;width:100%}[_nghost-%COMP%]     .offset-1{margin-left:8.33333333%}[_nghost-%COMP%]     .offset-2{margin-left:16.66666667%}[_nghost-%COMP%]     .offset-3{margin-left:25%}[_nghost-%COMP%]     .offset-4{margin-left:33.33333333%}[_nghost-%COMP%]     .offset-5{margin-left:41.66666667%}[_nghost-%COMP%]     .offset-6{margin-left:50%}[_nghost-%COMP%]     .offset-7{margin-left:58.33333333%}[_nghost-%COMP%]     .offset-8{margin-left:66.66666667%}[_nghost-%COMP%]     .offset-9{margin-left:75%}[_nghost-%COMP%]     .offset-10{margin-left:83.33333333%}[_nghost-%COMP%]     .offset-11{margin-left:91.66666667%}[_nghost-%COMP%]     .g-0, [_nghost-%COMP%]     .gx-0{--%NS%bs-gutter-x: 0}[_nghost-%COMP%]     .g-0, [_nghost-%COMP%]     .gy-0{--%NS%bs-gutter-y: 0}[_nghost-%COMP%]     .g-1, [_nghost-%COMP%]     .gx-1{--%NS%bs-gutter-x: .25rem}[_nghost-%COMP%]     .g-1, [_nghost-%COMP%]     .gy-1{--%NS%bs-gutter-y: .25rem}[_nghost-%COMP%]     .g-2, [_nghost-%COMP%]     .gx-2{--%NS%bs-gutter-x: .5rem}[_nghost-%COMP%]     .g-2, [_nghost-%COMP%]     .gy-2{--%NS%bs-gutter-y: .5rem}[_nghost-%COMP%]     .g-3, [_nghost-%COMP%]     .gx-3{--%NS%bs-gutter-x: 1rem}[_nghost-%COMP%]     .g-3, [_nghost-%COMP%]     .gy-3{--%NS%bs-gutter-y: 1rem}[_nghost-%COMP%]     .g-4, [_nghost-%COMP%]     .gx-4{--%NS%bs-gutter-x: 1.5rem}[_nghost-%COMP%]     .g-4, [_nghost-%COMP%]     .gy-4{--%NS%bs-gutter-y: 1.5rem}[_nghost-%COMP%]     .g-5, [_nghost-%COMP%]     .gx-5{--%NS%bs-gutter-x: 3rem}[_nghost-%COMP%]     .g-5, [_nghost-%COMP%]     .gy-5{--%NS%bs-gutter-y: 3rem}@media(min-width:576px){[_nghost-%COMP%]     .col-sm{flex:1 0 0}[_nghost-%COMP%]     .row-cols-sm-auto>*{flex:0 0 auto;width:auto}[_nghost-%COMP%]     .row-cols-sm-1>*{flex:0 0 auto;width:100%}[_nghost-%COMP%]     .row-cols-sm-2>*{flex:0 0 auto;width:50%}[_nghost-%COMP%]     .row-cols-sm-3>*{flex:0 0 auto;width:33.33333333%}[_nghost-%COMP%]     .row-cols-sm-4>*{flex:0 0 auto;width:25%}[_nghost-%COMP%]     .row-cols-sm-5>*{flex:0 0 auto;width:20%}[_nghost-%COMP%]     .row-cols-sm-6>*{flex:0 0 auto;width:16.66666667%}[_nghost-%COMP%]     .col-sm-auto{flex:0 0 auto;width:auto}[_nghost-%COMP%]     .col-sm-1{flex:0 0 auto;width:8.33333333%}[_nghost-%COMP%]     .col-sm-2{flex:0 0 auto;width:16.66666667%}[_nghost-%COMP%]     .col-sm-3{flex:0 0 auto;width:25%}[_nghost-%COMP%]     .col-sm-4{flex:0 0 auto;width:33.33333333%}[_nghost-%COMP%]     .col-sm-5{flex:0 0 auto;width:41.66666667%}[_nghost-%COMP%]     .col-sm-6{flex:0 0 auto;width:50%}[_nghost-%COMP%]     .col-sm-7{flex:0 0 auto;width:58.33333333%}[_nghost-%COMP%]     .col-sm-8{flex:0 0 auto;width:66.66666667%}[_nghost-%COMP%]     .col-sm-9{flex:0 0 auto;width:75%}[_nghost-%COMP%]     .col-sm-10{flex:0 0 auto;width:83.33333333%}[_nghost-%COMP%]     .col-sm-11{flex:0 0 auto;width:91.66666667%}[_nghost-%COMP%]     .col-sm-12{flex:0 0 auto;width:100%}[_nghost-%COMP%]     .offset-sm-0{margin-left:0}[_nghost-%COMP%]     .offset-sm-1{margin-left:8.33333333%}[_nghost-%COMP%]     .offset-sm-2{margin-left:16.66666667%}[_nghost-%COMP%]     .offset-sm-3{margin-left:25%}[_nghost-%COMP%]     .offset-sm-4{margin-left:33.33333333%}[_nghost-%COMP%]     .offset-sm-5{margin-left:41.66666667%}[_nghost-%COMP%]     .offset-sm-6{margin-left:50%}[_nghost-%COMP%]     .offset-sm-7{margin-left:58.33333333%}[_nghost-%COMP%]     .offset-sm-8{margin-left:66.66666667%}[_nghost-%COMP%]     .offset-sm-9{margin-left:75%}[_nghost-%COMP%]     .offset-sm-10{margin-left:83.33333333%}[_nghost-%COMP%]     .offset-sm-11{margin-left:91.66666667%}[_nghost-%COMP%]     .g-sm-0, [_nghost-%COMP%]     .gx-sm-0{--%NS%bs-gutter-x: 0}[_nghost-%COMP%]     .g-sm-0, [_nghost-%COMP%]     .gy-sm-0{--%NS%bs-gutter-y: 0}[_nghost-%COMP%]     .g-sm-1, [_nghost-%COMP%]     .gx-sm-1{--%NS%bs-gutter-x: .25rem}[_nghost-%COMP%]     .g-sm-1, [_nghost-%COMP%]     .gy-sm-1{--%NS%bs-gutter-y: .25rem}[_nghost-%COMP%]     .g-sm-2, [_nghost-%COMP%]     .gx-sm-2{--%NS%bs-gutter-x: .5rem}[_nghost-%COMP%]     .g-sm-2, [_nghost-%COMP%]     .gy-sm-2{--%NS%bs-gutter-y: .5rem}[_nghost-%COMP%]     .g-sm-3, [_nghost-%COMP%]     .gx-sm-3{--%NS%bs-gutter-x: 1rem}[_nghost-%COMP%]     .g-sm-3, [_nghost-%COMP%]     .gy-sm-3{--%NS%bs-gutter-y: 1rem}[_nghost-%COMP%]     .g-sm-4, [_nghost-%COMP%]     .gx-sm-4{--%NS%bs-gutter-x: 1.5rem}[_nghost-%COMP%]     .g-sm-4, [_nghost-%COMP%]     .gy-sm-4{--%NS%bs-gutter-y: 1.5rem}[_nghost-%COMP%]     .g-sm-5, [_nghost-%COMP%]     .gx-sm-5{--%NS%bs-gutter-x: 3rem}[_nghost-%COMP%]     .g-sm-5, [_nghost-%COMP%]     .gy-sm-5{--%NS%bs-gutter-y: 3rem}}@media(min-width:768px){[_nghost-%COMP%]     .col-md{flex:1 0 0}[_nghost-%COMP%]     .row-cols-md-auto>*{flex:0 0 auto;width:auto}[_nghost-%COMP%]     .row-cols-md-1>*{flex:0 0 auto;width:100%}[_nghost-%COMP%]     .row-cols-md-2>*{flex:0 0 auto;width:50%}[_nghost-%COMP%]     .row-cols-md-3>*{flex:0 0 auto;width:33.33333333%}[_nghost-%COMP%]     .row-cols-md-4>*{flex:0 0 auto;width:25%}[_nghost-%COMP%]     .row-cols-md-5>*{flex:0 0 auto;width:20%}[_nghost-%COMP%]     .row-cols-md-6>*{flex:0 0 auto;width:16.66666667%}[_nghost-%COMP%]     .col-md-auto{flex:0 0 auto;width:auto}[_nghost-%COMP%]     .col-md-1{flex:0 0 auto;width:8.33333333%}[_nghost-%COMP%]     .col-md-2{flex:0 0 auto;width:16.66666667%}[_nghost-%COMP%]     .col-md-3{flex:0 0 auto;width:25%}[_nghost-%COMP%]     .col-md-4{flex:0 0 auto;width:33.33333333%}[_nghost-%COMP%]     .col-md-5{flex:0 0 auto;width:41.66666667%}[_nghost-%COMP%]     .col-md-6{flex:0 0 auto;width:50%}[_nghost-%COMP%]     .col-md-7{flex:0 0 auto;width:58.33333333%}[_nghost-%COMP%]     .col-md-8{flex:0 0 auto;width:66.66666667%}[_nghost-%COMP%]     .col-md-9{flex:0 0 auto;width:75%}[_nghost-%COMP%]     .col-md-10{flex:0 0 auto;width:83.33333333%}[_nghost-%COMP%]     .col-md-11{flex:0 0 auto;width:91.66666667%}[_nghost-%COMP%]     .col-md-12{flex:0 0 auto;width:100%}[_nghost-%COMP%]     .offset-md-0{margin-left:0}[_nghost-%COMP%]     .offset-md-1{margin-left:8.33333333%}[_nghost-%COMP%]     .offset-md-2{margin-left:16.66666667%}[_nghost-%COMP%]     .offset-md-3{margin-left:25%}[_nghost-%COMP%]     .offset-md-4{margin-left:33.33333333%}[_nghost-%COMP%]     .offset-md-5{margin-left:41.66666667%}[_nghost-%COMP%]     .offset-md-6{margin-left:50%}[_nghost-%COMP%]     .offset-md-7{margin-left:58.33333333%}[_nghost-%COMP%]     .offset-md-8{margin-left:66.66666667%}[_nghost-%COMP%]     .offset-md-9{margin-left:75%}[_nghost-%COMP%]     .offset-md-10{margin-left:83.33333333%}[_nghost-%COMP%]     .offset-md-11{margin-left:91.66666667%}[_nghost-%COMP%]     .g-md-0, [_nghost-%COMP%]     .gx-md-0{--%NS%bs-gutter-x: 0}[_nghost-%COMP%]     .g-md-0, [_nghost-%COMP%]     .gy-md-0{--%NS%bs-gutter-y: 0}[_nghost-%COMP%]     .g-md-1, [_nghost-%COMP%]     .gx-md-1{--%NS%bs-gutter-x: .25rem}[_nghost-%COMP%]     .g-md-1, [_nghost-%COMP%]     .gy-md-1{--%NS%bs-gutter-y: .25rem}[_nghost-%COMP%]     .g-md-2, [_nghost-%COMP%]     .gx-md-2{--%NS%bs-gutter-x: .5rem}[_nghost-%COMP%]     .g-md-2, [_nghost-%COMP%]     .gy-md-2{--%NS%bs-gutter-y: .5rem}[_nghost-%COMP%]     .g-md-3, [_nghost-%COMP%]     .gx-md-3{--%NS%bs-gutter-x: 1rem}[_nghost-%COMP%]     .g-md-3, [_nghost-%COMP%]     .gy-md-3{--%NS%bs-gutter-y: 1rem}[_nghost-%COMP%]     .g-md-4, [_nghost-%COMP%]     .gx-md-4{--%NS%bs-gutter-x: 1.5rem}[_nghost-%COMP%]     .g-md-4, [_nghost-%COMP%]     .gy-md-4{--%NS%bs-gutter-y: 1.5rem}[_nghost-%COMP%]     .g-md-5, [_nghost-%COMP%]     .gx-md-5{--%NS%bs-gutter-x: 3rem}[_nghost-%COMP%]     .g-md-5, [_nghost-%COMP%]     .gy-md-5{--%NS%bs-gutter-y: 3rem}}@media(min-width:992px){[_nghost-%COMP%]     .col-lg{flex:1 0 0}[_nghost-%COMP%]     .row-cols-lg-auto>*{flex:0 0 auto;width:auto}[_nghost-%COMP%]     .row-cols-lg-1>*{flex:0 0 auto;width:100%}[_nghost-%COMP%]     .row-cols-lg-2>*{flex:0 0 auto;width:50%}[_nghost-%COMP%]     .row-cols-lg-3>*{flex:0 0 auto;width:33.33333333%}[_nghost-%COMP%]     .row-cols-lg-4>*{flex:0 0 auto;width:25%}[_nghost-%COMP%]     .row-cols-lg-5>*{flex:0 0 auto;width:20%}[_nghost-%COMP%]     .row-cols-lg-6>*{flex:0 0 auto;width:16.66666667%}[_nghost-%COMP%]     .col-lg-auto{flex:0 0 auto;width:auto}[_nghost-%COMP%]     .col-lg-1{flex:0 0 auto;width:8.33333333%}[_nghost-%COMP%]     .col-lg-2{flex:0 0 auto;width:16.66666667%}[_nghost-%COMP%]     .col-lg-3{flex:0 0 auto;width:25%}[_nghost-%COMP%]     .col-lg-4{flex:0 0 auto;width:33.33333333%}[_nghost-%COMP%]     .col-lg-5{flex:0 0 auto;width:41.66666667%}[_nghost-%COMP%]     .col-lg-6{flex:0 0 auto;width:50%}[_nghost-%COMP%]     .col-lg-7{flex:0 0 auto;width:58.33333333%}[_nghost-%COMP%]     .col-lg-8{flex:0 0 auto;width:66.66666667%}[_nghost-%COMP%]     .col-lg-9{flex:0 0 auto;width:75%}[_nghost-%COMP%]     .col-lg-10{flex:0 0 auto;width:83.33333333%}[_nghost-%COMP%]     .col-lg-11{flex:0 0 auto;width:91.66666667%}[_nghost-%COMP%]     .col-lg-12{flex:0 0 auto;width:100%}[_nghost-%COMP%]     .offset-lg-0{margin-left:0}[_nghost-%COMP%]     .offset-lg-1{margin-left:8.33333333%}[_nghost-%COMP%]     .offset-lg-2{margin-left:16.66666667%}[_nghost-%COMP%]     .offset-lg-3{margin-left:25%}[_nghost-%COMP%]     .offset-lg-4{margin-left:33.33333333%}[_nghost-%COMP%]     .offset-lg-5{margin-left:41.66666667%}[_nghost-%COMP%]     .offset-lg-6{margin-left:50%}[_nghost-%COMP%]     .offset-lg-7{margin-left:58.33333333%}[_nghost-%COMP%]     .offset-lg-8{margin-left:66.66666667%}[_nghost-%COMP%]     .offset-lg-9{margin-left:75%}[_nghost-%COMP%]     .offset-lg-10{margin-left:83.33333333%}[_nghost-%COMP%]     .offset-lg-11{margin-left:91.66666667%}[_nghost-%COMP%]     .g-lg-0, [_nghost-%COMP%]     .gx-lg-0{--%NS%bs-gutter-x: 0}[_nghost-%COMP%]     .g-lg-0, [_nghost-%COMP%]     .gy-lg-0{--%NS%bs-gutter-y: 0}[_nghost-%COMP%]     .g-lg-1, [_nghost-%COMP%]     .gx-lg-1{--%NS%bs-gutter-x: .25rem}[_nghost-%COMP%]     .g-lg-1, [_nghost-%COMP%]     .gy-lg-1{--%NS%bs-gutter-y: .25rem}[_nghost-%COMP%]     .g-lg-2, [_nghost-%COMP%]     .gx-lg-2{--%NS%bs-gutter-x: .5rem}[_nghost-%COMP%]     .g-lg-2, [_nghost-%COMP%]     .gy-lg-2{--%NS%bs-gutter-y: .5rem}[_nghost-%COMP%]     .g-lg-3, [_nghost-%COMP%]     .gx-lg-3{--%NS%bs-gutter-x: 1rem}[_nghost-%COMP%]     .g-lg-3, [_nghost-%COMP%]     .gy-lg-3{--%NS%bs-gutter-y: 1rem}[_nghost-%COMP%]     .g-lg-4, [_nghost-%COMP%]     .gx-lg-4{--%NS%bs-gutter-x: 1.5rem}[_nghost-%COMP%]     .g-lg-4, [_nghost-%COMP%]     .gy-lg-4{--%NS%bs-gutter-y: 1.5rem}[_nghost-%COMP%]     .g-lg-5, [_nghost-%COMP%]     .gx-lg-5{--%NS%bs-gutter-x: 3rem}[_nghost-%COMP%]     .g-lg-5, [_nghost-%COMP%]     .gy-lg-5{--%NS%bs-gutter-y: 3rem}}@media(min-width:1200px){[_nghost-%COMP%]     .col-xl{flex:1 0 0}[_nghost-%COMP%]     .row-cols-xl-auto>*{flex:0 0 auto;width:auto}[_nghost-%COMP%]     .row-cols-xl-1>*{flex:0 0 auto;width:100%}[_nghost-%COMP%]     .row-cols-xl-2>*{flex:0 0 auto;width:50%}[_nghost-%COMP%]     .row-cols-xl-3>*{flex:0 0 auto;width:33.33333333%}[_nghost-%COMP%]     .row-cols-xl-4>*{flex:0 0 auto;width:25%}[_nghost-%COMP%]     .row-cols-xl-5>*{flex:0 0 auto;width:20%}[_nghost-%COMP%]     .row-cols-xl-6>*{flex:0 0 auto;width:16.66666667%}[_nghost-%COMP%]     .col-xl-auto{flex:0 0 auto;width:auto}[_nghost-%COMP%]     .col-xl-1{flex:0 0 auto;width:8.33333333%}[_nghost-%COMP%]     .col-xl-2{flex:0 0 auto;width:16.66666667%}[_nghost-%COMP%]     .col-xl-3{flex:0 0 auto;width:25%}[_nghost-%COMP%]     .col-xl-4{flex:0 0 auto;width:33.33333333%}[_nghost-%COMP%]     .col-xl-5{flex:0 0 auto;width:41.66666667%}[_nghost-%COMP%]     .col-xl-6{flex:0 0 auto;width:50%}[_nghost-%COMP%]     .col-xl-7{flex:0 0 auto;width:58.33333333%}[_nghost-%COMP%]     .col-xl-8{flex:0 0 auto;width:66.66666667%}[_nghost-%COMP%]     .col-xl-9{flex:0 0 auto;width:75%}[_nghost-%COMP%]     .col-xl-10{flex:0 0 auto;width:83.33333333%}[_nghost-%COMP%]     .col-xl-11{flex:0 0 auto;width:91.66666667%}[_nghost-%COMP%]     .col-xl-12{flex:0 0 auto;width:100%}[_nghost-%COMP%]     .offset-xl-0{margin-left:0}[_nghost-%COMP%]     .offset-xl-1{margin-left:8.33333333%}[_nghost-%COMP%]     .offset-xl-2{margin-left:16.66666667%}[_nghost-%COMP%]     .offset-xl-3{margin-left:25%}[_nghost-%COMP%]     .offset-xl-4{margin-left:33.33333333%}[_nghost-%COMP%]     .offset-xl-5{margin-left:41.66666667%}[_nghost-%COMP%]     .offset-xl-6{margin-left:50%}[_nghost-%COMP%]     .offset-xl-7{margin-left:58.33333333%}[_nghost-%COMP%]     .offset-xl-8{margin-left:66.66666667%}[_nghost-%COMP%]     .offset-xl-9{margin-left:75%}[_nghost-%COMP%]     .offset-xl-10{margin-left:83.33333333%}[_nghost-%COMP%]     .offset-xl-11{margin-left:91.66666667%}[_nghost-%COMP%]     .g-xl-0, [_nghost-%COMP%]     .gx-xl-0{--%NS%bs-gutter-x: 0}[_nghost-%COMP%]     .g-xl-0, [_nghost-%COMP%]     .gy-xl-0{--%NS%bs-gutter-y: 0}[_nghost-%COMP%]     .g-xl-1, [_nghost-%COMP%]     .gx-xl-1{--%NS%bs-gutter-x: .25rem}[_nghost-%COMP%]     .g-xl-1, [_nghost-%COMP%]     .gy-xl-1{--%NS%bs-gutter-y: .25rem}[_nghost-%COMP%]     .g-xl-2, [_nghost-%COMP%]     .gx-xl-2{--%NS%bs-gutter-x: .5rem}[_nghost-%COMP%]     .g-xl-2, [_nghost-%COMP%]     .gy-xl-2{--%NS%bs-gutter-y: .5rem}[_nghost-%COMP%]     .g-xl-3, [_nghost-%COMP%]     .gx-xl-3{--%NS%bs-gutter-x: 1rem}[_nghost-%COMP%]     .g-xl-3, [_nghost-%COMP%]     .gy-xl-3{--%NS%bs-gutter-y: 1rem}[_nghost-%COMP%]     .g-xl-4, [_nghost-%COMP%]     .gx-xl-4{--%NS%bs-gutter-x: 1.5rem}[_nghost-%COMP%]     .g-xl-4, [_nghost-%COMP%]     .gy-xl-4{--%NS%bs-gutter-y: 1.5rem}[_nghost-%COMP%]     .g-xl-5, [_nghost-%COMP%]     .gx-xl-5{--%NS%bs-gutter-x: 3rem}[_nghost-%COMP%]     .g-xl-5, [_nghost-%COMP%]     .gy-xl-5{--%NS%bs-gutter-y: 3rem}}@media(min-width:1400px){[_nghost-%COMP%]     .col-xxl{flex:1 0 0}[_nghost-%COMP%]     .row-cols-xxl-auto>*{flex:0 0 auto;width:auto}[_nghost-%COMP%]     .row-cols-xxl-1>*{flex:0 0 auto;width:100%}[_nghost-%COMP%]     .row-cols-xxl-2>*{flex:0 0 auto;width:50%}[_nghost-%COMP%]     .row-cols-xxl-3>*{flex:0 0 auto;width:33.33333333%}[_nghost-%COMP%]     .row-cols-xxl-4>*{flex:0 0 auto;width:25%}[_nghost-%COMP%]     .row-cols-xxl-5>*{flex:0 0 auto;width:20%}[_nghost-%COMP%]     .row-cols-xxl-6>*{flex:0 0 auto;width:16.66666667%}[_nghost-%COMP%]     .col-xxl-auto{flex:0 0 auto;width:auto}[_nghost-%COMP%]     .col-xxl-1{flex:0 0 auto;width:8.33333333%}[_nghost-%COMP%]     .col-xxl-2{flex:0 0 auto;width:16.66666667%}[_nghost-%COMP%]     .col-xxl-3{flex:0 0 auto;width:25%}[_nghost-%COMP%]     .col-xxl-4{flex:0 0 auto;width:33.33333333%}[_nghost-%COMP%]     .col-xxl-5{flex:0 0 auto;width:41.66666667%}[_nghost-%COMP%]     .col-xxl-6{flex:0 0 auto;width:50%}[_nghost-%COMP%]     .col-xxl-7{flex:0 0 auto;width:58.33333333%}[_nghost-%COMP%]     .col-xxl-8{flex:0 0 auto;width:66.66666667%}[_nghost-%COMP%]     .col-xxl-9{flex:0 0 auto;width:75%}[_nghost-%COMP%]     .col-xxl-10{flex:0 0 auto;width:83.33333333%}[_nghost-%COMP%]     .col-xxl-11{flex:0 0 auto;width:91.66666667%}[_nghost-%COMP%]     .col-xxl-12{flex:0 0 auto;width:100%}[_nghost-%COMP%]     .offset-xxl-0{margin-left:0}[_nghost-%COMP%]     .offset-xxl-1{margin-left:8.33333333%}[_nghost-%COMP%]     .offset-xxl-2{margin-left:16.66666667%}[_nghost-%COMP%]     .offset-xxl-3{margin-left:25%}[_nghost-%COMP%]     .offset-xxl-4{margin-left:33.33333333%}[_nghost-%COMP%]     .offset-xxl-5{margin-left:41.66666667%}[_nghost-%COMP%]     .offset-xxl-6{margin-left:50%}[_nghost-%COMP%]     .offset-xxl-7{margin-left:58.33333333%}[_nghost-%COMP%]     .offset-xxl-8{margin-left:66.66666667%}[_nghost-%COMP%]     .offset-xxl-9{margin-left:75%}[_nghost-%COMP%]     .offset-xxl-10{margin-left:83.33333333%}[_nghost-%COMP%]     .offset-xxl-11{margin-left:91.66666667%}[_nghost-%COMP%]     .g-xxl-0, [_nghost-%COMP%]     .gx-xxl-0{--%NS%bs-gutter-x: 0}[_nghost-%COMP%]     .g-xxl-0, [_nghost-%COMP%]     .gy-xxl-0{--%NS%bs-gutter-y: 0}[_nghost-%COMP%]     .g-xxl-1, [_nghost-%COMP%]     .gx-xxl-1{--%NS%bs-gutter-x: .25rem}[_nghost-%COMP%]     .g-xxl-1, [_nghost-%COMP%]     .gy-xxl-1{--%NS%bs-gutter-y: .25rem}[_nghost-%COMP%]     .g-xxl-2, [_nghost-%COMP%]     .gx-xxl-2{--%NS%bs-gutter-x: .5rem}[_nghost-%COMP%]     .g-xxl-2, [_nghost-%COMP%]     .gy-xxl-2{--%NS%bs-gutter-y: .5rem}[_nghost-%COMP%]     .g-xxl-3, [_nghost-%COMP%]     .gx-xxl-3{--%NS%bs-gutter-x: 1rem}[_nghost-%COMP%]     .g-xxl-3, [_nghost-%COMP%]     .gy-xxl-3{--%NS%bs-gutter-y: 1rem}[_nghost-%COMP%]     .g-xxl-4, [_nghost-%COMP%]     .gx-xxl-4{--%NS%bs-gutter-x: 1.5rem}[_nghost-%COMP%]     .g-xxl-4, [_nghost-%COMP%]     .gy-xxl-4{--%NS%bs-gutter-y: 1.5rem}[_nghost-%COMP%]     .g-xxl-5, [_nghost-%COMP%]     .gx-xxl-5{--%NS%bs-gutter-x: 3rem}[_nghost-%COMP%]     .g-xxl-5, [_nghost-%COMP%]     .gy-xxl-5{--%NS%bs-gutter-y: 3rem}}[_nghost-%COMP%]{display:contents}`]})}}return n})();var ms=(()=>{class n{static{this.ɵfac=function(t){return new(t||n)}}static{this.ɵdir=Ot$2({type:n,selectors:[[``,`bsRow`,``]],hostVars:2,hostBindings:function(t,s){t&2&&Ty(`row`,!0)}})}}return n})();var fs=(()=>{class n{constructor(){this.xxs=Ir(void 0),this.xs=Ir(void 0),this.sm=Ir(void 0),this.md=Ir(void 0),this.lg=Ir(void 0),this.xl=Ir(void 0),this.xxl=Ir(void 0),this.classList=On(()=>{let e={xxs:this.xxs(),xs:this.xs(),sm:this.sm(),md:this.md(),lg:this.lg(),xl:this.xl(),xxl:this.xxl()};return Object.keys(e).map(t=>({key:t,value:e[t]})).filter(t=>t.value).map(t=>{switch(t.key){case``:return`col`;case`xxs`:return`col-${t.value}`;default:return`col-${t.key}-${t.value}`}}).join(` `)||null})}static{this.ɵfac=function(t){return new(t||n)}}static{this.ɵdir=Ot$2({type:n,selectors:[[``,`xxs`,``],[``,`xs`,``],[``,`sm`,``],[``,`md`,``],[``,`lg`,``],[``,`xl`,``],[``,`xxl`,``]],hostVars:2,hostBindings:function(t,s){t&2&&Lf(s.classList())},inputs:{xxs:[1,`xxs`],xs:[1,`xs`],sm:[1,`sm`],md:[1,`md`],lg:[1,`lg`],xl:[1,`xl`],xxl:[1,`xxl`]}})}}return n})();var _s=(()=>{class n{constructor(){this.col=Ir(void 0)}static{this.ɵfac=function(t){return new(t||n)}}static{this.ɵdir=Ot$2({type:n,selectors:[[``,`col`,``]],hostVars:2,hostBindings:function(t,s){t&2&&Ty(`col`,!0)},inputs:{col:[1,`col`]}})}}return n})();var bs=(()=>{class n{static{this.ɵfac=function(t){return new(t||n)}}static{this.ɵdir=Ot$2({type:n,selectors:[[``,`bsColFormLabel`,``]],hostVars:2,hostBindings:function(t,s){t&2&&Ty(`col-form-label`,!0)}})}}return n})();var ae=globalThis.__sparkCurrentLanguage??=ne$2(`en`);function et(n,i){if(!n)return``;return n[i??ae()]??n.en??Object.values(n)[0]??``}var j=new y$1(`SPARK_CONFIG`);var $t=new y$1(`SPARK_AUTH_STATE`);var le=class n{http=p(nE);config=p(j,{optional:!0});baseUrl=this.config?.baseUrl??`/spark`;currentLang=ne$2(`en`);translationsMap=ne$2({});language=this.currentLang.asReadonly();languages=ne$2({});constructor(){this.loadCulture(),this.loadTranslations()}async loadCulture(){let i=await Ow(this.http.get(`${this.baseUrl}/culture`));this.languages.set(i.languages);let t=localStorage.getItem(`spark-lang`)??i.defaultLanguage;this.currentLang.set(t),ae.set(t)}async loadTranslations(){let i=await Ow(this.http.get(`${this.baseUrl}/translations`));this.translationsMap.set(i)}setLanguage(i){this.currentLang.set(i),ae.set(i),localStorage.setItem(`spark-lang`,i)}resolve(i){if(!i)return``;return i[this.currentLang()]??i.en??Object.values(i)[0]??``}t(i){let e=this.translationsMap()[i];return this.resolve(e)||i}static ɵfac=function(e){return new(e||n)};static ɵprov=_({token:n,factory:n.ɵfac,providedIn:`root`})};var tt=new y$1(`SPARK_CLIENT_OPERATION_HANDLERS`);var de=class n{handlerMap;constructor(){let i=p(tt,{optional:!0})??[],e=new Map;for(let{type:t,handler:s}of i)e.set(t,s);this.handlerMap=e}dispatch(i){if(!(!i||i.length===0))for(let e of i){let t=this.handlerMap.get(e.type);t&&t(e)}}static ɵfac=function(e){return new(e||n)};static ɵprov=_({token:n,factory:n.ɵfac,providedIn:`root`})};var he=class n{resolveRetry=null;payload=ne$2(null);show(i){return this.payload.set(i),new Promise(e=>{this.resolveRetry=e})}respond(i){this.payload.set(null),this.resolveRetry?.(i),this.resolveRetry=null}static ɵfac=function(e){return new(e||n)};static ɵprov=_({token:n,factory:n.ɵfac,providedIn:`root`})};var st=class n{config=p(j,{optional:!0});baseUrl=this.config?.baseUrl??`/spark`;http=p(nE);retryActionService=p(he);dispatcher=p(de);async getEntityTypes(){return Ow(this.http.get(`${this.baseUrl}/types`))}async getEntityType(i){return Ow(this.http.get(`${this.baseUrl}/types/${encodeURIComponent(i)}`))}async getEntityTypeByClrType(i){return(await this.getEntityTypes()).find(t=>t.clrType===i)}async getPermissions(i){return Ow(this.http.get(`${this.baseUrl}/permissions/${encodeURIComponent(i)}`))}async getQueries(){return Ow(this.http.get(`${this.baseUrl}/queries`))}async getQuery(i){return Ow(this.http.get(`${this.baseUrl}/queries/${encodeURIComponent(i)}`))}async getQueryByName(i){return(await this.getQueries()).find(t=>t.name===i)}async executeQuery(i,e){let t=new cn$1;return e?.sortColumns?.length&&(t=t.set(`sortColumns`,e.sortColumns.map(s=>`${s.property}:${s.direction===`descending`?`desc`:`asc`}`).join(`,`))),e?.parentId&&(t=t.set(`parentId`,e.parentId)),e?.parentType&&(t=t.set(`parentType`,e.parentType)),e?.skip!=null&&(t=t.set(`skip`,e.skip)),e?.take!=null&&(t=t.set(`take`,e.take)),e?.search&&(t=t.set(`search`,e.search)),Ow(this.http.get(`${this.baseUrl}/queries/${encodeURIComponent(i)}/execute`,{params:t}))}async executeQueryByName(i,e){let t=await this.getQueryByName(i);return t?this.executeQuery(t.id,{parentId:e?.parentId,parentType:e?.parentType}):{columns:[],items:[],totalItems:0,skip:0,take:50}}async getProgramUnits(){return Ow(this.http.get(`${this.baseUrl}/program-units`))}async list(i){return Ow(this.http.get(`${this.baseUrl}/po/${encodeURIComponent(i)}`))}async get(i,e){return Ow(this.http.get(`${this.baseUrl}/po/${encodeURIComponent(i)}/${encodeURIComponent(e)}`))}async create(i,e){return this.postWithEnvelope(`${this.baseUrl}/po/${encodeURIComponent(i)}`,{persistentObject:e})}async update(i,e,t){return this.putWithEnvelope(`${this.baseUrl}/po/${encodeURIComponent(i)}/${encodeURIComponent(e)}`,{persistentObject:t})}async refresh(i,e,t){return this.postWithEnvelope(`${this.baseUrl}/po/${encodeURIComponent(i)}/refresh`,{persistentObject:e,triggeredBy:t})}async delete(i,e){return this.deleteWithEnvelope(`${this.baseUrl}/po/${encodeURIComponent(i)}/${encodeURIComponent(e)}`,{})}async getCustomActions(i){return Ow(this.http.get(`${this.baseUrl}/actions/${encodeURIComponent(i)}`))}async executeCustomAction(i,e,t,s,o,r){let l={parent:t,selectedItemIds:s,parentId:o?.id,parentType:o?.type,queryId:r};return this.postWithEnvelope(`${this.baseUrl}/actions/${encodeURIComponent(i)}/${encodeURIComponent(e)}`,l)}async getLookupReferences(){return Ow(this.http.get(`${this.baseUrl}/lookupref`))}async getLookupReference(i){return Ow(this.http.get(`${this.baseUrl}/lookupref/${encodeURIComponent(i)}`))}async addLookupReferenceValue(i,e){return Ow(this.http.post(`${this.baseUrl}/lookupref/${encodeURIComponent(i)}`,e))}async updateLookupReferenceValue(i,e,t){return Ow(this.http.put(`${this.baseUrl}/lookupref/${encodeURIComponent(i)}/${encodeURIComponent(e)}`,t))}async deleteLookupReferenceValue(i,e){return Ow(this.http.delete(`${this.baseUrl}/lookupref/${encodeURIComponent(i)}/${encodeURIComponent(e)}`))}postWithEnvelope(i,e){return this.sendWithEnvelope(()=>Ow(this.http.post(i,e)),e,()=>this.postWithEnvelope(i,e))}putWithEnvelope(i,e){return this.sendWithEnvelope(()=>Ow(this.http.put(i,e)),e,()=>this.putWithEnvelope(i,e))}deleteWithEnvelope(i,e){return this.sendWithEnvelope(()=>{return Ow(e.retryResults&&e.retryResults.length>0?this.http.delete(i,{body:e}):this.http.delete(i))},e,()=>this.deleteWithEnvelope(i,e))}async sendWithEnvelope(i,e,t){try{let s=await i();return s?.operations?.length&&this.dispatcher.dispatch(s.operations),s?.result}catch(s){return this.handleEnvelopeRetryError(s,t,e)}}async handleEnvelopeRetryError(i,e,t){if(i.status!==449)throw i;let s=i.error;if(!s?.operations?.length)throw i;let o=s.operations.filter(a=>a.type!==`retry`);o.length&&this.dispatcher.dispatch(o);let r=s.operations.find(a=>a.type===`retry`);if(!r)throw i;let l={type:`retry-action`,step:r.step,title:r.title,options:r.options,defaultOption:r.defaultOption??void 0,persistentObject:r.persistentObject??void 0,message:r.message??void 0},d=await this.retryActionService.show(l);if(d.option===`Cancel`&&!l.options.includes(`Cancel`))throw i;return t.retryResults=[...t.retryResults||[],d],e()}static ɵfac=function(e){return new(e||n)};static ɵprov=_({token:n,factory:n.ɵfac,providedIn:`root`})};var nt={"arrow-left":`<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" fill="currentColor" class="bi bi-arrow-left" viewBox="0 0 16 16"><path fill-rule="evenodd" d="M15 8a.5.5 0 0 0-.5-.5H2.707l3.147-3.146a.5.5 0 1 0-.708-.708l-4 4a.5.5 0 0 0 0 .708l4 4a.5.5 0 0 0 .708-.708L2.707 8.5H14.5A.5.5 0 0 0 15 8"/></svg>`,pencil:`<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" fill="currentColor" class="bi bi-pencil" viewBox="0 0 16 16"><path d="M12.146.146a.5.5 0 0 1 .708 0l3 3a.5.5 0 0 1 0 .708l-10 10a.5.5 0 0 1-.168.11l-5 2a.5.5 0 0 1-.65-.65l2-5a.5.5 0 0 1 .11-.168zM11.207 2.5 13.5 4.793 14.793 3.5 12.5 1.207zm1.586 3L10.5 3.207 4 9.707V10h.5a.5.5 0 0 1 .5.5v.5h.5a.5.5 0 0 1 .5.5v.5h.293zm-9.761 5.175-.106.106-1.528 3.821 3.821-1.528.106-.106A.5.5 0 0 1 5 12.5V12h-.5a.5.5 0 0 1-.5-.5V11h-.5a.5.5 0 0 1-.468-.325"/></svg>`,plus:`<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" fill="currentColor" class="bi bi-plus" viewBox="0 0 16 16"><path d="M8 4a.5.5 0 0 1 .5.5v3h3a.5.5 0 0 1 0 1h-3v3a.5.5 0 0 1-1 0v-3h-3a.5.5 0 0 1 0-1h3v-3A.5.5 0 0 1 8 4"/></svg>`,"plus-lg":`<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" fill="currentColor" class="bi bi-plus-lg" viewBox="0 0 16 16"><path fill-rule="evenodd" d="M8 2a.5.5 0 0 1 .5.5v5h5a.5.5 0 0 1 0 1h-5v5a.5.5 0 0 1-1 0v-5h-5a.5.5 0 0 1 0-1h5v-5A.5.5 0 0 1 8 2"/></svg>`,search:`<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" fill="currentColor" class="bi bi-search" viewBox="0 0 16 16"><path d="M11.742 10.344a6.5 6.5 0 1 0-1.397 1.398h-.001q.044.06.098.115l3.85 3.85a1 1 0 0 0 1.415-1.414l-3.85-3.85a1 1 0 0 0-.115-.1zM12 6.5a5.5 5.5 0 1 1-11 0 5.5 5.5 0 0 1 11 0"/></svg>`,trash:`<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" fill="currentColor" class="bi bi-trash" viewBox="0 0 16 16"><path d="M5.5 5.5A.5.5 0 0 1 6 6v6a.5.5 0 0 1-1 0V6a.5.5 0 0 1 .5-.5m2.5 0a.5.5 0 0 1 .5.5v6a.5.5 0 0 1-1 0V6a.5.5 0 0 1 .5-.5m3 .5a.5.5 0 0 0-1 0v6a.5.5 0 0 0 1 0z"/><path d="M14.5 3a1 1 0 0 1-1 1H13v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V4h-.5a1 1 0 0 1-1-1V2a1 1 0 0 1 1-1H6a1 1 0 0 1 1-1h2a1 1 0 0 1 1 1h3.5a1 1 0 0 1 1 1zM4.118 4 4 4.059V13a1 1 0 0 0 1 1h6a1 1 0 0 0 1-1V4.059L11.882 4zM2.5 3h11V2h-11z"/></svg>`,"x-lg":`<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" fill="currentColor" class="bi bi-x-lg" viewBox="0 0 16 16"><path d="M2.146 2.854a.5.5 0 1 1 .708-.708L8 7.293l5.146-5.147a.5.5 0 0 1 .708.708L8.707 8l5.147 5.146a.5.5 0 0 1-.708.708L8 8.707l-5.146 5.147a.5.5 0 0 1-.708-.708L7.293 8z"/></svg>`};var ce=class n{sanitizer=p(YN);icons=new Map;constructor(){for(let[i,e]of Object.entries(nt))this.icons.set(i,this.sanitizer.bypassSecurityTrustHtml(e))}register(i,e){this.icons.set(i,e)}get(i){return this.icons.get(i)}has(i){return this.icons.has(i)}static ɵfac=function(e){return new(e||n)};static ɵprov=_({token:n,factory:n.ɵfac,providedIn:`root`})};var it=class n{lang=p(le);transform(i){return this.lang.t(i)}static ɵfac=function(e){return new(e||n)};static ɵpipe=Oi({name:`t`,type:n,pure:!1})};var ot=class n{transform(i,e){return et(i)||e||``}static ɵfac=function(e){return new(e||n)};static ɵpipe=Oi({name:`resolveTranslation`,type:n,pure:!1})};function At(n,i){n&1&&py(0,`span`,0),n&2&&vy(`innerHTML`,i,$C)}function Tt(n,i){if(n&1&&py(0,`i`,2),n&2)Lf(ZT().cssFallbackClass())}var rt=class n{registry=p(ce);name=Ir.required();iconHtml=On(()=>this.registry.get(this.name()));cssFallbackClass=On(()=>`bi-${this.name()}`);static ɵfac=function(e){return new(e||n)};static ɵcmp=xi({type:n,selectors:[[`spark-icon`]],inputs:{name:[1,`name`]},decls:2,vars:1,consts:[[3,`innerHTML`],[1,`bi`,3,`class`],[1,`bi`]],template:function(e,t){if(e&1&&xT(0,At,1,1,`span`,0)(1,Tt,1,2,`i`,1),e&2){let s;OT((s=t.iconHtml())?0:1,s)}},styles:[`[_nghost-%COMP%]{display:inline-flex;align-items:center;justify-content:center}span[_ngcontent-%COMP%]{display:inline-flex;align-items:center}span[_ngcontent-%COMP%]     svg{width:1em;height:1em;fill:currentColor}`]})};var Nn=(()=>{class n{constructor(){this.platformId=p(si),this.isNoScript=!1,z2(this.platformId)&&(this.isNoScript=!0)}static{this.ɵfac=function(t){return new(t||n)}}static{this.ɵdir=Ot$2({type:n,selectors:[[``,`bsNoNoscript`,``]],hostVars:2,hostBindings:function(t,s){t&2&&Ty(`noscript`,s.isNoScript)}})}}return n})();var at=(n,i,e)=>{let t=new Map;for(let s=i;s<=e;s++)t.set(n[s],s);return t};var ge=_$1(class extends s$1{constructor(n){if(super(n),n.type!==T$2.CHILD)throw Error(`repeat() can only be used in text expressions`)}dt(n,i,e){let t;e===void 0?e=i:i!==void 0&&(t=i);let s=[],o=[],r=0;for(let l of n)s[r]=t?t(l,r):r,o[r]=e(l,r),r++;return{values:o,keys:s}}render(n,i,e){return this.dt(n,i,e).values}update(n,[i,e,t]){let s=Ft$1(n),{values:o,keys:r}=this.dt(i,e,t);if(!Array.isArray(s))return this.ut=r,o;let l=this.ut??=[],d=[],a,h,u=0,g=s.length-1,c=0,_=o.length-1;for(;u<=g&&c<=_;)if(s[u]===null)u++;else if(s[g]===null)g--;else if(l[u]===r[c])d[c]=Mt(s[u],o[c]),u++,c++;else if(l[g]===r[_])d[_]=Mt(s[g],o[_]),g--,_--;else if(l[u]===r[_])d[_]=Mt(s[u],o[_]),Ct(n,d[_+1],s[u]),u++,_--;else if(l[g]===r[c])d[c]=Mt(s[g],o[c]),Ct(n,s[u],s[g]),g--,c++;else if(a===void 0&&(a=at(r,c,_),h=at(l,u,g)),a.has(l[u]))if(a.has(l[g])){let y=h.get(r[c]),x=y!==void 0?s[y]:null;if(x===null){let F=Ct(n,s[u]);Mt(F,o[c]),d[c]=F}else d[c]=Mt(x,o[c]),Ct(n,s[u],x),s[y]=null;c++}else St$1(s[g]),g--;else St$1(s[u]),u++;for(;c<=_;){let y=Ct(n,d[_+1]);Mt(y,o[c]),d[c++]=y}for(;u<=g;){let y=s[u++];y!==null&&St$1(y)}return this.ut=r,Dt$1(n,d),y$2}});var lt=`important`;var Dt=` !`+lt;var U=_$1(class extends s$1{constructor(n){if(super(n),n.type!==T$2.ATTRIBUTE||n.name!==`style`||n.strings?.length>2)throw Error("The `styleMap` directive must be used in the `style` attribute and must be the only part in the attribute.")}render(n){return Object.keys(n).reduce((i,e)=>{let t=n[e];return t==null?i:i+`${e=e.includes(`-`)?e:e.replace(/(?:^(webkit|moz|ms|o)|)(?=[A-Z])/g,`-$&`).toLowerCase()}:${t};`},``)}update(n,[i]){let{style:e}=n.element;if(this.ft===void 0)return this.ft=new Set(Object.keys(i)),this.render(i);for(let t of this.ft)i[t]??(this.ft.delete(t),t.includes(`-`)?e.removeProperty(t):e[t]=null);for(let t in i){let s=i[t];if(s!=null){this.ft.add(t);let o=typeof s==`string`&&s.endsWith(Dt);t.includes(`-`)||o?e.setProperty(t,o?s.slice(0,-11):s,o?lt:``):e[t]=s}}return y$2}});var Lt=X$3(`:host {
  display: block;
  --mp-pagination-bg: var(--bs-pagination-bg, transparent);
  --mp-pagination-color: var(--bs-pagination-color, var(--bs-link-color, #0d6efd));
  --mp-pagination-hover-bg: var(--bs-pagination-hover-bg, var(--bs-tertiary-bg, #e9ecef));
  --mp-pagination-hover-color: var(--bs-pagination-hover-color, var(--bs-link-hover-color, #0a58ca));
  --mp-pagination-active-bg: var(--bs-pagination-active-bg, var(--bs-primary, #0d6efd));
  --mp-pagination-active-color: var(--bs-pagination-active-color, #fff);
  --mp-pagination-disabled-bg: var(--bs-pagination-disabled-bg, transparent);
  --mp-pagination-disabled-color: var(--bs-pagination-disabled-color, var(--bs-secondary-color, #6c757d));
  --mp-pagination-border-color: var(--bs-pagination-border-color, var(--bs-border-color, #dee2e6));
  --mp-pagination-active-border-color: var(--bs-pagination-active-border-color, var(--bs-primary, #0d6efd));
  --mp-pagination-padding-y: 0.375rem;
  --mp-pagination-padding-x: 0.75rem;
  --mp-pagination-font-size: 1rem;
  --mp-pagination-border-radius: 0.375rem;
  font-family: inherit;
}

* {
  box-sizing: border-box;
}

:host([size=small]) {
  --mp-pagination-padding-y: 0.25rem;
  --mp-pagination-padding-x: 0.5rem;
  --mp-pagination-font-size: 0.875rem;
}

:host([size=large]) {
  --mp-pagination-padding-y: 0.75rem;
  --mp-pagination-padding-x: 1.5rem;
  --mp-pagination-font-size: 1.25rem;
}

nav {
  display: contents;
}

ul {
  display: inline-flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0;
  list-style: none;
  margin: 0;
  padding: 0;
}

li {
  display: inline-flex;
}

.page-link {
  appearance: none;
  background-color: var(--mp-pagination-bg);
  border: 1px solid var(--mp-pagination-border-color);
  color: var(--mp-pagination-color);
  cursor: pointer;
  font: inherit;
  font-size: var(--mp-pagination-font-size);
  padding: var(--mp-pagination-padding-y) var(--mp-pagination-padding-x);
  text-decoration: none;
  user-select: none;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: calc(var(--mp-pagination-padding-x) * 2 + 1ch);
  margin-left: -1px;
}

li:first-child .page-link {
  margin-left: 0;
  border-top-left-radius: var(--mp-pagination-border-radius);
  border-bottom-left-radius: var(--mp-pagination-border-radius);
}

li:last-child .page-link {
  border-top-right-radius: var(--mp-pagination-border-radius);
  border-bottom-right-radius: var(--mp-pagination-border-radius);
}

.page-link:hover:not([disabled]):not([aria-current=page]) {
  background-color: var(--mp-pagination-hover-bg);
  color: var(--mp-pagination-hover-color);
  z-index: 1;
}

.page-link:focus-visible {
  outline: 2px solid var(--mp-pagination-active-bg);
  outline-offset: -2px;
  z-index: 2;
}

.page-link[aria-current=page] {
  background-color: var(--mp-pagination-active-bg);
  border-color: var(--mp-pagination-active-border-color);
  color: var(--mp-pagination-active-color);
  z-index: 2;
}

.page-link[disabled],
.page-link.disabled {
  background-color: var(--mp-pagination-disabled-bg);
  color: var(--mp-pagination-disabled-color);
  cursor: default;
  pointer-events: none;
}

.ellipsis {
  background-color: var(--mp-pagination-bg);
  border: 1px solid var(--mp-pagination-border-color);
  color: var(--mp-pagination-disabled-color);
  font: inherit;
  font-size: var(--mp-pagination-font-size);
  padding: var(--mp-pagination-padding-y) var(--mp-pagination-padding-x);
  cursor: default;
  user-select: none;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  margin-left: -1px;
}

.visually-hidden {
  position: absolute !important;
  width: 1px !important;
  height: 1px !important;
  padding: 0 !important;
  margin: -1px !important;
  overflow: hidden !important;
  clip: rect(0, 0, 0, 0) !important;
  white-space: nowrap !important;
  border: 0 !important;
}`);var zt=(()=>{class n extends b$1{constructor(){super(...arguments),this._pageNumbers=[],this._selectedPageNumber=1,this._numberOfBoxes=0,this._showArrows=!0,this._size=`medium`,this._ariaLabel=`Pagination`}static{this.styles=[Lt]}static get observedAttributes(){return[...super.observedAttributes??[],`page-numbers`,`selected-page-number`,`number-of-boxes`,`show-arrows`,`size`,`aria-label`]}get pageNumbers(){return[...this._pageNumbers]}set pageNumbers(e){this._pageNumbers=Array.isArray(e)?[...e]:[],this.requestUpdate()}get selectedPageNumber(){return this._selectedPageNumber}set selectedPageNumber(e){let t=Number(e);Number.isFinite(t)&&t!==this._selectedPageNumber&&(this._selectedPageNumber=t,this.requestUpdate())}get numberOfBoxes(){return this._numberOfBoxes}set numberOfBoxes(e){let t=Math.max(0,Math.floor(e||0));this._numberOfBoxes!==t&&(this._numberOfBoxes=t,this.requestUpdate())}get showArrows(){return this._showArrows}set showArrows(e){let t=!!e;this._showArrows!==t&&(this._showArrows=t,this.requestUpdate())}get size(){return this._size}set size(e){(e===`small`||e===`medium`||e===`large`)&&this._size!==e&&(this._size=e,this.setAttribute(`size`,e),this.requestUpdate())}attributeChangedCallback(e,t,s){if(super.attributeChangedCallback(e,t,s),e===`page-numbers`&&s){let o=s.split(`,`).map(r=>Number(r.trim())).filter(r=>Number.isFinite(r));this.pageNumbers=o}else if(e===`selected-page-number`){let o=Number(s);Number.isFinite(o)&&(this.selectedPageNumber=o)}else if(e===`number-of-boxes`){let o=Number(s);Number.isFinite(o)&&(this.numberOfBoxes=o)}else e===`show-arrows`?this.showArrows=s!==`false`&&s!==null:e===`size`?(s===`small`||s===`medium`||s===`large`)&&(this.size=s):e===`aria-label`&&(this._ariaLabel=s??`Pagination`,this.requestUpdate())}effectiveBudget(){let e=this._pageNumbers.length+(this._showArrows?2:0);return this._numberOfBoxes<=0?e:Math.min(e,this._numberOfBoxes)}computeLayout(){return Ut(this._pageNumbers,this._selectedPageNumber,this.effectiveBudget(),this._showArrows)}render(){let e=this.computeLayout(),t=this.isFirstPage(),s=this.isLastPage();return Ht$2`
      <nav aria-label=${this._ariaLabel}>
        <ul>
          ${e.showPrev?Ht$2`<li>
                <button
                  type="button"
                  class="page-link"
                  aria-label="Previous"
                  ?disabled=${t}
                  @click=${()=>this.onPrevious()}
                >
                  <span aria-hidden="true">&laquo;</span>
                  <span class="visually-hidden">Previous</span>
                </button>
              </li>`:p$1}
          ${ge(e.items,(o,r)=>o.kind===`gap`?`gap-${r}`:`page-${o.page}`,o=>o.kind===`gap`?Ht$2`<li>
                  <span class="ellipsis" aria-hidden="true">&hellip;</span>
                </li>`:Ht$2`<li>
                  <button
                    type="button"
                    class="page-link"
                    aria-current=${o.current?`page`:p$1}
                    aria-label=${`Page ${o.page}`}
                    @click=${()=>this.selectPage(o.page)}
                  >
                    ${o.page}
                  </button>
                </li>`)}
          ${e.showNext?Ht$2`<li>
                <button
                  type="button"
                  class="page-link"
                  aria-label="Next"
                  ?disabled=${s}
                  @click=${()=>this.onNext()}
                >
                  <span aria-hidden="true">&raquo;</span>
                  <span class="visually-hidden">Next</span>
                </button>
              </li>`:p$1}
        </ul>
      </nav>
    `}isFirstPage(){return this._pageNumbers.indexOf(this._selectedPageNumber)===0}isLastPage(){return this._pageNumbers.indexOf(this._selectedPageNumber)===this._pageNumbers.length-1}selectPage(e){e!==this._selectedPageNumber&&this._pageNumbers.includes(e)&&(this._selectedPageNumber=e,this.requestUpdate(),this.dispatchEvent(new CustomEvent(`mp-pagination-page-change`,{detail:{page:e},bubbles:!0,composed:!0})))}onPrevious(){let e=this._pageNumbers.indexOf(this._selectedPageNumber),t=e>0?this._pageNumbers[e-1]:this._pageNumbers[0];t!=null&&this.selectPage(t)}onNext(){let e=this._pageNumbers.indexOf(this._selectedPageNumber),t=this._pageNumbers.length-1,s=e<0?this._pageNumbers[t]:e<t?this._pageNumbers[e+1]:this._pageNumbers[t];s!=null&&this.selectPage(s)}}return n})();function Ut(n,i,e,t){let s=n.length;if(s===0)return{showPrev:!1,showNext:!1,items:[]};let o=Kt(n.indexOf(i),s),r=Math.max(1,Math.floor(e)),l=!1,d=!1,a=0,h=0,u=0,g=0,c=r-1;t&&c>0&&(d=!0,c--),t&&c>0&&(l=!0,c--);let _=()=>!(a>=o||a>=s-h||u>=2&&a>=o-(u-1)),y=()=>!(s-1-h<=o||s-1-h<a||g>=2&&s-1-h<=o+(g-1)),x=()=>o===0?!1:u===0?a+1<o:o-u>=a,F=()=>o===s-1?!1:g===0?o+1<s-1-h:o+g<=s-1-h,Ce=()=>o-a-Math.max(0,u-1),ye=()=>s-1-o-h-Math.max(0,g-1);for(c>0&&_()&&(a++,c--),c>0&&y()&&(h++,c--),c>0&&(x()?(u++,c--):Ce()>0&&_()&&(a++,c--)),c>0&&(F()?(g++,c--):ye()>0&&y()&&(h++,c--));c>0;){let R=c;if(x()&&(u++,c--),c===0||(F()&&(g++,c--),c===0)||(_()&&(a++,c--),c===0)||(y()&&(h++,c--),R===c))break}let A=R=>({kind:`page`,page:n[R],current:n[R]===i}),mt=Array.from({length:a},(R,T)=>A(T)),ft=u>=2?Array.from({length:u-1},(R,T)=>A(o-(u-1)+T)):[],_t=g>=2?Array.from({length:g-1},(R,T)=>A(o+1+T)):[],bt=Array.from({length:h},(R,T)=>A(s-h+T)),we=a-1,xt=u>=2?o-(u-1):o,Ct=Ce(),yt=u>=1&&we+1<xt?Ct===1?[A(we+1)]:[{kind:`gap`}]:[],Pe=g>=2?o+(g-1):o,wt=s-h,Pt=ye(),vt=g>=1&&Pe+1<wt?Pt===1?[A(Pe+1)]:[{kind:`gap`}]:[],Mt=[...mt,...yt,...ft,A(o),..._t,...vt,...bt];return{showPrev:l,showNext:d,items:Mt}}function Kt(n,i){return n<0?0:n>=i?i-1:n}typeof customElements<`u`&&!customElements.get(`mp-pagination`)&&customElements.define(`mp-pagination`,zt);var Ft={treeChevronColumn:`Expand or collapse`,deselectAll:`Deselect all`,selectAll:`Select all`,expandRow:`Expand row`,collapseRow:`Collapse row`,loading:`Loading`,rowsPerPage:`Rows per page`,resizeColumn:n=>`Resize column ${n}`,selectRow:n=>`Select row ${n}`,announceSorted:(n,i)=>i===`none`?`Sorting removed from ${n}`:`Sorted by ${n}, ${i}`,announcePage:(n,i)=>`Page ${n} of ${i}`,announceSelection:n=>n===1?`1 row selected`:`${n} rows selected`,announceLoaded:n=>n===1?`Loaded 1 row`:`Loaded ${n} rows`};var ht=X$3(`@charset "UTF-8";
:host {
  display: block;
  width: 100%;
  position: relative;
  --mp-datatable-border-color: var(--bs-border-color, rgba(0, 0, 0, 0.125));
  --mp-datatable-row-hover-bg: var(--bs-table-hover-bg, rgba(0, 0, 0, 0.04));
  --mp-datatable-row-selected-bg: var(--bs-list-group-active-bg, var(--bs-primary, #0d6efd));
  --mp-datatable-row-selected-color: var(--bs-list-group-active-color, #fff);
  --mp-datatable-cut-opacity: 0.55;
  --mp-datatable-resize-handle-color: var(--bs-primary, #0d6efd);
}

* {
  box-sizing: border-box;
}

.datatable-shell {
  display: flex;
  flex-direction: column;
  width: 100%;
  height: 100%;
}

.datatable-scroll {
  overflow: auto;
  flex: 1 1 auto;
}

.datatable-scroll.datatable-virtual {
  max-height: var(--mp-datatable-virtual-max-height, 480px);
}
.datatable-scroll.datatable-virtual thead th {
  position: sticky;
  top: 0;
  z-index: 1;
  background-color: var(--bs-body-bg, #fff);
}
.datatable-scroll.datatable-virtual thead th[data-sortable=true]:hover {
  background-color: var(--bs-body-bg, #fff);
  background-image: linear-gradient(var(--mp-datatable-row-hover-bg), var(--mp-datatable-row-hover-bg));
}

tr.virtual-spacer {
  pointer-events: none;
}

tr.virtual-spacer > td {
  border-bottom: 0;
}

table {
  width: 100%;
  border-collapse: collapse;
  margin: 0;
  table-layout: auto;
}

table.measured {
  table-layout: fixed;
}

tbody td {
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

thead th {
  background-color: var(--bs-table-bg, transparent);
  border-bottom: 1px solid var(--mp-datatable-border-color);
  text-align: left;
  font-weight: 600;
  padding: 0.5rem 0.75rem;
  white-space: nowrap;
  position: relative;
  user-select: none;
}

thead th[data-sortable=true] {
  cursor: pointer;
  padding-right: 2rem;
}
thead th[data-sortable=true]::before, thead th[data-sortable=true]::after {
  position: absolute;
  display: block;
  opacity: 0.3;
  bottom: 0.5em;
}
thead th[data-sortable=true]::before {
  content: "\u2191";
  right: 1em;
}
thead th[data-sortable=true]::after {
  content: "\u2193";
  right: 0.5em;
}
thead th[data-sortable=true][aria-sort=ascending]::after {
  opacity: 1;
}
thead th[data-sortable=true][aria-sort=descending]::before {
  opacity: 1;
}

thead th[data-sortable=true]:hover {
  background-color: var(--mp-datatable-row-hover-bg);
}

.header-cell {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
}

button.header-sort {
  border: 0;
  padding: 0;
  margin: 0;
  background: none;
  font: inherit;
  color: inherit;
  text-align: inherit;
  cursor: pointer;
}

.resize-handle:focus-visible,
button.header-sort:focus-visible {
  outline: 2px solid var(--mp-datatable-resize-handle-color);
  outline-offset: 1px;
}

tbody tr:focus-visible {
  outline: 2px solid var(--mp-datatable-resize-handle-color);
  outline-offset: -2px;
}

.sort-index {
  position: absolute;
  right: 0.1em;
  bottom: 0.3em;
  font-size: 0.65em;
  font-weight: bold;
  opacity: 0.7;
}

.resize-handle {
  position: absolute;
  right: 0;
  top: 0;
  width: 6px;
  height: 100%;
  cursor: col-resize;
  user-select: none;
  z-index: 2;
}

.resize-handle:hover,
.resize-handle.active {
  background-color: var(--mp-datatable-resize-handle-color);
}

tbody td {
  padding: 0.5rem 0.75rem;
  border-bottom: 1px solid var(--mp-datatable-border-color);
  vertical-align: middle;
}

tbody td svg,
tbody td img {
  max-width: var(--mp-datatable-cell-media-max-width, none);
  height: auto;
  vertical-align: middle;
}

tbody tr {
  cursor: default;
}

tbody tr[data-clickable=true] {
  cursor: pointer;
}

tbody tr:hover {
  background-color: var(--mp-datatable-row-hover-bg);
}

tbody tr[data-selected=true] {
  background-color: var(--mp-datatable-row-selected-bg);
  color: var(--mp-datatable-row-selected-color);
}

tbody tr[data-cut=true] {
  opacity: var(--mp-datatable-cut-opacity);
}

tbody tr[data-focused=true] {
  outline: 2px solid var(--bs-primary, #0d6efd);
  outline-offset: -2px;
}

.checkbox-cell {
  width: 2.5rem;
  text-align: center;
}

.tree-chevron-cell {
  width: 2rem;
  padding-right: 0;
  white-space: nowrap;
  user-select: none;
}

.tree-chevron {
  appearance: none;
  background: transparent;
  border: 0;
  padding: 0;
  width: 1.5rem;
  height: 1.5rem;
  line-height: 1;
  cursor: pointer;
  color: var(--bs-body-color, inherit);
  font-size: 0.85em;
  border-radius: 0.25rem;
  display: inline-flex;
  align-items: center;
  justify-content: center;
}
.tree-chevron:hover {
  background-color: var(--mp-datatable-row-hover-bg);
}
.tree-chevron:focus-visible {
  outline: 2px solid var(--bs-primary, #0d6efd);
  outline-offset: 1px;
}

tbody tr[data-placeholder=true] {
  cursor: default;
  opacity: 0.6;
  pointer-events: none;
}

.tree-placeholder-cell {
  color: var(--bs-secondary-color, #6c757d);
  font-style: italic;
  padding-left: 1.25rem;
}

.empty-state,
.loading-state {
  padding: 2rem 1rem;
  text-align: center;
  color: var(--bs-secondary-color, #6c757d);
}

.datatable-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0.5rem 0;
  gap: 1rem;
  flex-wrap: wrap;
  flex: 0 0 auto;
}

.datatable-per-page,
.datatable-pagination {
  flex: 0 1 auto;
}

@media (prefers-reduced-motion: reduce) {
  .resize-handle {
    transition: none !important;
  }
}`);function be(n,i,e){if(e){let s$2=n.findIndex(o=>o.property===i);return s$2===-1?[...n,{property:i,direction:`ascending`}]:n[s$2].direction===`ascending`?n.map((o,r$1)=>r$1===s$2?s(r({},o),{direction:`descending`}):o):n.filter((o,r)=>r!==s$2)}return[{property:i,direction:n.length===1&&n[0].property===i&&n[0].direction===`ascending`?`descending`:`ascending`}]}function q(n,i){return i.length===0?n.slice():n.map((e,t)=>({row:e,index:t})).sort((e,t)=>{for(let s of i){let l=Bt(dt(e.row,s.property),dt(t.row,s.property));if(l!==0)return s.direction===`ascending`?l:-l}return e.index-t.index}).map(({row:e})=>e)}function dt(n,i){if(!(n==null||typeof n!=`object`))return n[i]}function Bt(n,i){return n===i?0:n==null?1:i==null?-1:typeof n==`number`&&typeof i==`number`?n-i:n instanceof Date&&i instanceof Date?n.getTime()-i.getTime():String(n).localeCompare(String(i))}var jt=0;var ct=(()=>{class n extends b$1{constructor(){super(...arguments),this.instanceId=`mp-datatable-${++jt}`,this._caption=null,this._inputLabel=null,this.hostAria=new v(this,{referenceTarget:()=>this.renderRoot?.querySelector(`table`)??null}),this.labels=void 0,this._columns=[],this._data=[],this._sortColumns=[],this._selectionMode=`none`,this._selectedIds=new Set,this._cutIds=new Set,this._focusedRowKey=null,this.liveAnnouncer=new ee(this),this.rowFocusRestore=new se(this,new te(()=>this.renderRoot??null,{selector:`tbody tr[data-row-key]:not([data-placeholder="true"])`,keyOf:e=>e.dataset.rowKey??null,container:()=>this.renderRoot?.querySelector(`table`)??null})),this._rowKey=(e,t)=>{let s=e;return s&&s.id!=null?String(s.id):`row-${t}`},this._columnWidths=new Map,this._hasMeasuredInitial=!1,this._loading=!1,this._emptyMessage=`No data`,this._pagination=!1,this._page=1,this._perPage=20,this._perPageOptions=[10,20,50],this._autoSort=!0,this._totalRecords=null,this._virtualScroll=!1,this._itemSize=40,this._virtualBuffer=10,this._virtualRange={startIndex:0,endIndex:0},this._scrollElement=null,this._resizeObserver=null,this._scrollListener=null,this._viewportHeight=0,this._tree=!1,this._idKey=null,this._childCountKey=null,this._treeIndent=1.25,this._expandedIds=new Set,this._selectionStrategy=`flat`,this._childCache=new Map,this._childTotals=new Map,this._pendingFetches=new Set,this._pageCache=new Map,this._pendingPageFetches=new Set,this._fetch=null,this._fetchGeneration=0,this._initialFetchDone=!1,this._reloadScheduled=!1,this._lastReloadKey=null,this._cachedFlatList=null,this._cachedIndeterminateKeys=null,this._resizableColumns=!0,this._selectionAnchorKey=null,this.resizeState=null,this.onColumnResizeMove=e=>{if(!this.resizeState)return;let t=e.clientX-this.resizeState.startX,s=Math.max(40,this.resizeState.startWidth+t);this._columnWidths=new Map(this._columnWidths),this._columnWidths.set(this.resizeState.columnName,s),this.requestUpdate()},this.onColumnResizeEnd=e=>{if(this.resizeState){this.resizeState.handle.classList.remove(`active`),this.resizeState.handle.removeEventListener(`pointermove`,this.onColumnResizeMove),this.resizeState.handle.removeEventListener(`pointerup`,this.onColumnResizeEnd),this.resizeState.handle.removeEventListener(`pointercancel`,this.onColumnResizeEnd);try{this.resizeState.handle.releasePointerCapture(e.pointerId)}catch{}this.resizeState=null}}}static{this.styles=[ht]}static get observedAttributes(){return[...super.observedAttributes??[],`selection-mode`,`pagination`,`resizable-columns`,`auto-sort`,`empty-message`,`virtual-scroll`,`item-size`,`tree`,`tree-indent`,`selection-strategy`,`caption`,`aria-label`,`input-label`,`aria-labelledby`,`aria-describedby`]}get caption(){return this._caption}set caption(e){let t=e??null;this._caption!==t&&(this._caption=t,this.requestUpdate())}get mergedLabels(){return r(r({},Ft),this.labels??{})}get inputLabel(){return this._inputLabel}set inputLabel(e){let t=e??null;this._inputLabel!==t&&(this._inputLabel=t,this.requestUpdate())}get autoSort(){return this._autoSort}set autoSort(e){this._autoSort=!!e,this.requestUpdate()}get rowRenderer(){return this._rowRenderer}set rowRenderer(e){this._rowRenderer=e,this.requestUpdate()}get virtualScroll(){return this._virtualScroll}set virtualScroll(e){let t=!!e;this._virtualScroll!==t&&(this._virtualScroll=t,this.requestUpdate())}get itemSize(){return this._itemSize}set itemSize(e){let t=Math.max(1,Math.floor(e||0))||40;this._itemSize!==t&&(this._itemSize=t,this.requestUpdate())}get virtualBuffer(){return this._virtualBuffer}set virtualBuffer(e){let t=Math.max(0,Math.floor(e||0));this._virtualBuffer!==t&&(this._virtualBuffer=t,this.requestUpdate())}get columns(){return this._columns}set columns(e){this._columns=Array.isArray(e)?e:[],this.requestUpdate()}get data(){return this._data}set data(e){this._data=Array.isArray(e)?e:[],this.requestUpdate()}get fetch(){return this._fetch}set fetch(e){this._fetch=typeof e==`function`?e:null,this._fetch&&(this._autoSort=!1,this._initialFetchDone=!1,this._lastReloadKey=null,this.scheduleFetchReload())}get sortColumns(){return[...this._sortColumns]}set sortColumns(e){this._sortColumns=Array.isArray(e)?[...e]:[],this.requestUpdate(),this.scheduleFetchReload()}get selectionMode(){return this._selectionMode}set selectionMode(e){this._selectionMode=e,e===`none`&&this._selectedIds.clear(),this.requestUpdate()}get selectedIds(){return[...this._selectedIds]}set selectedIds(e){this._selectedIds=new Set(e??[]),this.requestUpdate()}get cutIds(){return[...this._cutIds]}set cutIds(e){this._cutIds=new Set(e??[]),this.requestUpdate()}get focusedRowKey(){return this._focusedRowKey}set focusedRowKey(e){this._focusedRowKey=e,this.requestUpdate()}get rowKey(){return this._rowKey}set rowKey(e){this._rowKey=typeof e==`function`?e:this._rowKey,this.requestUpdate()}get tree(){return this._tree}set tree(e){let t=!!e;this._tree!==t&&(this._tree=t,this.requestUpdate())}get idKey(){return this._idKey}set idKey(e){this._idKey=e,this.requestUpdate()}get childCountKey(){return this._childCountKey}set childCountKey(e){this._childCountKey=e||null,this.requestUpdate()}get treeIndent(){return this._treeIndent}set treeIndent(e){let t=Number(e);this._treeIndent=Number.isFinite(t)&&t>=0?t:1.25,this.requestUpdate()}get expandedIds(){return new Set(this._expandedIds)}set expandedIds(e){e?e instanceof Set?this._expandedIds=new Set(e):this._expandedIds=new Set(e):this._expandedIds=new Set,this.requestUpdate()}get selectionStrategy(){return this._selectionStrategy}set selectionStrategy(e){this._selectionStrategy=e===`cascading`?`cascading`:`flat`,this.requestUpdate()}get loading(){return this._loading}set loading(e){let t=this._loading;this._loading=!!e,t&&!this._loading&&this.liveAnnouncer.announce(this.mergedLabels.announceLoaded(this._data.length)),this.requestUpdate()}get emptyMessage(){return this._emptyMessage}set emptyMessage(e){this._emptyMessage=e||`No data`,this.requestUpdate()}get pagination(){return this._pagination}set pagination(e){this._pagination=!!e,this.requestUpdate()}get page(){return this._page}set page(e){let t=Math.max(1,Math.floor(e||1));this._page!==t&&(this._page=t,this.requestUpdate(),this.scheduleFetchReload())}get perPage(){return this._perPage}set perPage(e){let t=Math.max(1,Math.floor(e||1));this._perPage!==t&&(this._perPage=t,this._page=1,this.requestUpdate(),this.scheduleFetchReload())}get perPageOptions(){return[...this._perPageOptions]}set perPageOptions(e){this._perPageOptions=Array.isArray(e)&&e.length>0?[...e]:[10,20,50],this.requestUpdate()}get totalRecords(){return this._totalRecords}isExternallyPaged(){return this._totalRecords!=null&&this._totalRecords>this._data.length}isRootWindowed(){return this._virtualScroll&&this.isExternallyPaged()}attributeChangedCallback(e,t,s){if(super.attributeChangedCallback(e,t,s),e===`caption`)this._caption=s,this.requestUpdate();else if(e===`input-label`)this._inputLabel=s,this.requestUpdate();else if(e===`aria-label`)this.requestUpdate();else if(e===`aria-labelledby`||e===`aria-describedby`)this.hostAria.syncReferences();else if(e===`selection-mode`){let o=s;(o===`none`||o===`single`||o===`multiple`)&&(this.selectionMode=o)}else if(e===`pagination`)this.pagination=s!==null;else if(e===`resizable-columns`)this.resizableColumns=s!==null;else if(e===`auto-sort`)this.autoSort=s!==`false`;else if(e===`empty-message`)this.emptyMessage=s??`No data`;else if(e===`virtual-scroll`)this.virtualScroll=s!==null;else if(e===`item-size`){let o=Number(s);Number.isFinite(o)&&(this.itemSize=o)}else if(e===`tree`)this.tree=s!==null;else if(e===`tree-indent`){let o=Number(s);Number.isFinite(o)&&(this.treeIndent=o)}else e===`selection-strategy`&&(this.selectionStrategy=s===`cascading`?`cascading`:`flat`)}get resizableColumns(){return this._resizableColumns}set resizableColumns(e){this._resizableColumns=!!e,this.requestUpdate()}connectedCallback(){super.connectedCallback()}firstUpdated(){this._scrollElement=this.shadowRoot?.querySelector(`.datatable-scroll`),this._scrollElement&&(this._scrollListener=()=>this.refreshVirtualRange(),this._scrollElement.addEventListener(`scroll`,this._scrollListener,{passive:!0}),typeof ResizeObserver<`u`&&(this._resizeObserver=new ResizeObserver(()=>this.refreshVirtualRange()),this._resizeObserver.observe(this._scrollElement))),this.refreshVirtualRange(),this.hostAria.syncReferences()}willUpdate(e){super.willUpdate(e),this._cachedFlatList=null,this._cachedIndeterminateKeys=null}updated(e){super.updated(e),this.refreshVirtualRange(),this.maybeMeasureInitialColumnWidths();let t=this.renderRoot?.querySelectorAll(`.resize-handle:not([aria-valuenow])`)??[];for(let s of t){let o=s.closest(`th`);o&&s.setAttribute(`aria-valuenow`,String(Math.round(o.getBoundingClientRect().width)))}}maybeMeasureInitialColumnWidths(){if(this._hasMeasuredInitial||this._columns.length===0||this._data.length===0||!this.shadowRoot||this.shadowRoot.querySelectorAll(`tbody tr[data-row-key]:not([data-placeholder="true"])`).length===0)return;let e=new Map(this._columnWidths),t=!1;for(let s of this._columns){if(e.has(s.name))continue;if(typeof s.width==`number`){e.set(s.name,s.width),t=!0;continue}let o=this.measureColumnWidth(s.name);o!=null&&(e.set(s.name,o),t=!0)}t&&(this._columnWidths=e,this._hasMeasuredInitial=!0,this.requestUpdate())}measureColumnWidth(e){if(!this.shadowRoot)return null;let t=this.shadowRoot.querySelector(`th[data-column="${e}"]`);if(!t)return null;let s=Math.ceil(t.getBoundingClientRect().width);return s>0?s:null}disconnectedCallback(){super.disconnectedCallback(),this._scrollElement&&this._scrollListener&&this._scrollElement.removeEventListener(`scroll`,this._scrollListener),this._scrollListener=null,this._resizeObserver?.disconnect(),this._resizeObserver=null,this._scrollElement=null}refreshVirtualRange(){if(!this._virtualScroll||!this._scrollElement)return;let e=this._scrollElement.scrollTop,t=this._scrollElement.clientHeight;this._viewportHeight=t;let s=this.getEffectiveData().length,o=this._itemSize,r=Math.max(0,Math.floor(e/o)-this._virtualBuffer),l=Math.ceil(t/o),d=Math.min(s,r+l+this._virtualBuffer*2);(r!==this._virtualRange.startIndex||d!==this._virtualRange.endIndex)&&(this._virtualRange={startIndex:r,endIndex:d},this.requestUpdate()),this._tree&&this.maybeFetchPlaceholdersInViewport(),this.isRootWindowed()&&this.maybeFetchPagesInViewport()}maybeFetchPlaceholdersInViewport(){let e=this.getFlatList(),{startIndex:t,endIndex:s}=this._virtualRange,o=this._virtualScroll?t:0,r=this._virtualScroll?s:e.length,l=new Set;for(let d=o;d<r;d++){let a=e[d];!a.isPlaceholder||a.parentId==null||this._childCache.has(a.parentId)||this._pendingFetches.has(a.parentId)||l.add(a.parentId)}for(let d of l)this.fetchChildren(d)}maybeFetchPagesInViewport(){let e=this.getFlatList(),{startIndex:t,endIndex:s}=this._virtualRange,o=this._virtualScroll?t:0,r=this._virtualScroll?s:e.length,l=new Set;for(let d=o;d<r;d++){let a=e[d];!a.isPlaceholder||a.page==null||a.page<=1||this._pageCache.has(a.page)||this._pendingPageFetches.has(a.page)||l.add(a.page)}for(let d of l)this.fetchWindowPage(d)}render(){let e=this.computeVisibleRows(),t=this._totalRecords??this._data.length,s=this.pagination&&!this._tree?Math.max(1,Math.ceil(t/this._perPage)):1,o=this._selectionMode===`multiple`,r=this._columns.length+(o?1:0)+(this._tree?1:0),l=this.getVirtualSpacerHeights(),d=(this._tree||this.isRootWindowed()?this.getFlatList().length:this._data.length)+1;return Ht$2`
      ${this.liveAnnouncer.template()}
      <div class="datatable-shell">
        <div class="datatable-scroll ${this._virtualScroll?`datatable-virtual`:``}" role="presentation">
          <table
            role=${this._tree?`treegrid`:this._selectionMode!==`none`?`grid`:p$1}
            aria-rowcount=${d}
            aria-colcount=${r}
            aria-busy=${this._loading?`true`:p$1}
            aria-label=${this.getAttribute(`aria-label`)??this._inputLabel??p$1}
            class=${this._hasMeasuredInitial?`measured`:``}
          >
            ${this._caption?Ht$2`<caption>${this._caption}</caption>`:p$1}
            <thead>
              <tr role="row" aria-rowindex="1">
                ${this._tree?Ht$2`<th class="tree-chevron-cell" scope="col" aria-label=${this.mergedLabels.treeChevronColumn}></th>`:p$1}
                ${o?Ht$2`<th class="checkbox-cell" scope="col">
                      <mp-checkbox
                        aria-label=${this.mergedLabels.deselectAll}
                        aria-hidden=${this._selectedIds.size===0?`true`:p$1}
                        style=${U({visibility:this._selectedIds.size>0?`visible`:`hidden`})}
                        .checked=${!1}
                        .indeterminate=${this._selectedIds.size>0}
                        @change=${this.onDeselectAll}
                      ></mp-checkbox>
                    </th>`:p$1}
                ${this._columns.map((a,h)=>this.renderHeader(a,h))}
              </tr>
            </thead>
            <tbody>
              ${this._loading?Ht$2`<tr><td colspan=${r} class="loading-state">Loading…</td></tr>`:e.length===0?Ht$2`<tr><td colspan=${r} class="empty-state">${this._emptyMessage}</td></tr>`:Ht$2`
                      ${l.top>0?Ht$2`<tr class="virtual-spacer" aria-hidden="true"><td colspan=${r} style=${U({height:`${l.top}px`,padding:`0`,border:`0`})}></td></tr>`:p$1}
                      ${ge(e,({key:a})=>a,({key:a,rowIndex:h,flat:u})=>this.renderRow(u,a,h,o))}
                      ${l.bottom>0?Ht$2`<tr class="virtual-spacer" aria-hidden="true"><td colspan=${r} style=${U({height:`${l.bottom}px`,padding:`0`,border:`0`})}></td></tr>`:p$1}
                    `}
            </tbody>
          </table>
        </div>
        ${this._pagination&&!this._tree?this.renderFooter(s):p$1}
      </div>
    `}getVirtualSpacerHeights(){if(!this._virtualScroll)return{top:0,bottom:0};let e=this.getEffectiveData().length,{startIndex:t,endIndex:s}=this._virtualRange;return{top:t*this._itemSize,bottom:Math.max(0,(e-s)*this._itemSize)}}renderHeader(e,t){let s=e.sortable??!0,o=this._sortColumns.findIndex(h=>h.property===e.name),r=o>=0?this._sortColumns[o].direction:null,l=this._columnWidths.get(e.name)??e.width,d=e.headerRenderer?e.headerRenderer(e):e.label??e.name,a={};return typeof l==`number`&&(a.width=`${l}px`,a.minWidth=`${l}px`),Ht$2`
      <th
        scope="col"
        data-column=${e.name}
        data-sortable=${s?`true`:`false`}
        aria-sort=${r?r===`ascending`?`ascending`:`descending`:`none`}
        style=${U(a)}
      >
        ${s?Ht$2`<button
              type="button"
              class="header-cell header-sort"
              @click=${h=>this.onHeaderClick(e,h)}
            >
              <span>${_e(d)}</span>
              ${o>=0&&this._sortColumns.length>1?Ht$2`<span class="sort-index">${o+1}</span>`:p$1}
            </button>`:Ht$2`<span class="header-cell">
              <span>${_e(d)}</span>
            </span>`}
        ${this._resizableColumns?Ht$2`<span
              class="resize-handle"
              role="separator"
              tabindex="0"
              aria-orientation="vertical"
              aria-label=${this.mergedLabels.resizeColumn(e.label??e.name)}
              aria-valuemin="40"
              aria-valuenow=${Math.round(l??0)||p$1}
              @pointerdown=${h=>this.startColumnResize(e,h)}
              @keydown=${h=>this.onResizeHandleKeydown(e,h)}
            ></span>`:p$1}
      </th>
    `}renderRow(e,t,s,o){let{row:r,depth:l,isExpanded:d,isPlaceholder:a}=e,h=!a&&this._selectedIds.has(t),u=!a&&this._cutIds.has(t),g=!a&&this._focusedRowKey===t,c=a?0:this.extractChildCount(r),_=this._tree&&!a&&c>0,y=!a&&r!=null&&this.isParentIndeterminate(r);return Ht$2`
      <tr
        role="row"
        aria-rowindex=${s+2}
        aria-level=${this._tree?l+1:p$1}
        aria-expanded=${this._tree&&c>0?d?`true`:`false`:p$1}
        aria-busy=${a?`true`:p$1}
        aria-selected=${!a&&this._selectionMode!==`none`?h?`true`:`false`:p$1}
        data-row-key=${t}
        data-selected=${h?`true`:`false`}
        data-cut=${u?`true`:`false`}
        data-focused=${g?`true`:`false`}
        data-placeholder=${a?`true`:`false`}
        data-depth=${this._tree?String(l):p$1}
        data-clickable=${!a&&(this._selectionMode!==`none`||this.hasRowClickListeners())?`true`:`false`}
        tabindex=${!a&&(this._selectionMode!==`none`||this._tree)?this._focusedRowKey===t||this._focusedRowKey===null&&s===0?`0`:`-1`:p$1}
        @focus=${a?null:()=>{this._focusedRowKey=t,this.requestUpdate()}}
        @click=${a?null:x=>this.onRowClick(r,t,s,x)}
        @dblclick=${a?null:x=>this.onRowDblClick(r,t,s,x)}
        @contextmenu=${a?null:x=>this.onRowContextMenu(r,t,s,x)}
        @keydown=${a?null:x=>this.onRowKeydown(r,t,s,e.parentId,l,d,c,x)}
      >
        ${this._tree?Ht$2`<td class="tree-chevron-cell" style=${U({paddingInlineStart:`${l*this._treeIndent}rem`})}>
              ${_?Ht$2`<button
                    type="button"
                    class="tree-chevron"
                    aria-label=${d?this.mergedLabels.collapseRow:this.mergedLabels.expandRow}
                    aria-expanded=${d?`true`:`false`}
                    data-expanded=${d?`true`:`false`}
                    @click=${x=>this.toggleExpand(r,e.parentId,l,x)}
                  >${d?`▾`:`▸`}</button>`:p$1}
            </td>`:p$1}
        ${o?Ht$2`<td class="checkbox-cell" @click=${x=>x.stopPropagation()}>
              ${a?p$1:Ht$2`<mp-checkbox
                    aria-label=${this.mergedLabels.selectRow(s+1)}
                    .checked=${h}
                    .indeterminate=${y}
                    @change=${x=>this.onRowCheckboxToggle(r,t,s,x)}
                  ></mp-checkbox>`}
            </td>`:p$1}
        ${this._rowRenderer?this.renderRowFromRenderer(r,s,{depth:l,isExpanded:d,isPlaceholder:a}):a?Ht$2`<td colspan=${this._columns.length} class="tree-placeholder-cell" aria-label=${this.mergedLabels.loading}>…</td>`:this._columns.map(x=>this.renderCell(r,x,s))}
      </tr>
    `}renderRowFromRenderer(e,t,s){let o=this._rowRenderer(e,t,s);if(o==null)return s.isPlaceholder?Ht$2`<td colspan=${this._columns.length} class="tree-placeholder-cell" aria-label=${this.mergedLabels.loading}>…</td>`:this._columns.map(r=>this.renderCell(e,r,t));if(Array.isArray(o)||Vt(o)){let r=[];for(let l of o)r.push(l);return r}return o}renderCell(e,t,s){let o=t.cellRenderer?t.cellRenderer(e,t,s):qt(e,t);return Ht$2`<td class=${t.cellClass??``} data-column=${t.name}>${_e(o)}</td>`}renderFooter(e){let t=Array.from({length:e},(s,o)=>o+1);return Ht$2`
      <div class="datatable-footer">
        <mp-pagination
          class="datatable-per-page"
          aria-label=${this.mergedLabels.rowsPerPage}
          .pageNumbers=${this._perPageOptions}
          .selectedPageNumber=${this._perPage}
          .showArrows=${!1}
          @mp-pagination-page-change=${s=>this.setPerPage(s.detail.page)}
        ></mp-pagination>
        <mp-pagination
          class="datatable-pagination"
          .pageNumbers=${t}
          .selectedPageNumber=${this._page}
          .numberOfBoxes=${7}
          .showArrows=${!0}
          @mp-pagination-page-change=${s=>this.gotoPage(s.detail.page)}
        ></mp-pagination>
      </div>
    `}setPerPage(e){let t=Math.max(1,Math.floor(e||1));this._perPage!==t&&(this._perPage=t,this._page=1,this.requestUpdate(),this.scheduleFetchReload(),this.dispatchEvent(new CustomEvent(`mp-datatable-per-page-change`,{detail:{perPage:t},bubbles:!0,composed:!0})),this.dispatchEvent(new CustomEvent(`mp-datatable-page-change`,{detail:{page:1},bubbles:!0,composed:!0})))}getEffectiveData(){if(this._tree||this.isRootWindowed())return this.getFlatList().map(t=>t.row);let e=this._data;if(this._autoSort&&this._sortColumns.length>0&&(e=q(e,this._sortColumns)),this._pagination&&!this.isExternallyPaged()){let t=(this._page-1)*this._perPage;e=e.slice(t,t+this._perPage)}return e}getFlatList(){if(this._cachedFlatList!==null)return this._cachedFlatList;if(!this._tree){if(this.isRootWindowed()){let l=this._totalRecords??this._data.length,d=Math.max(1,this._perPage),a=new Array(l);for(let h=0;h<l;h++){let u=Math.floor(h/d)+1,g=h%d,c=u===1?this._data[g]:this._pageCache.get(u)?.[g];a[h]=c!==void 0?{row:c,key:this._rowKey(c,h),depth:0,parentId:null,isExpanded:!1,isPlaceholder:!1}:{row:void 0,key:`__placeholder-flat-${h}`,depth:0,parentId:null,page:u,isExpanded:!1,isPlaceholder:!0}}return this._cachedFlatList=a}let r=(()=>{let l=this._data;if(this._autoSort&&this._sortColumns.length>0&&(l=q(l,this._sortColumns)),this._pagination&&!this.isExternallyPaged()){let d=(this._page-1)*this._perPage;l=l.slice(d,d+this._perPage)}return l})();return this._cachedFlatList=r.map((l,d)=>({row:l,key:this._rowKey(l,d),depth:0,parentId:null,isExpanded:!1,isPlaceholder:!1}))}let e=[],t=Math.max(1,this._perPage),s=r=>this._autoSort&&this._sortColumns.length>0?q(r,this._sortColumns):r,o=(r,l,d)=>{let a=this.extractId(r),h=a!=null&&this._expandedIds.has(a),u=this.extractChildCount(r);if(e.push({row:r,key:this._rowKey(r,e.length),depth:l,parentId:d,isExpanded:h,isPlaceholder:!1}),!h||u===0||a==null)return;let g=this._childCache.get(a);if(g&&g.length>0){for(let _ of s(g))o(_,l+1,a);let c=this._childTotals.get(a)??g.length;for(let _=0;_<Math.max(0,c-g.length);_++)e.push({row:void 0,key:`__placeholder-${String(a)}-${g.length+_}`,depth:l+1,parentId:a,isExpanded:!1,isPlaceholder:!0})}else for(let c=0;c<u;c++)e.push({row:void 0,key:`__placeholder-${String(a)}-${c}`,depth:l+1,parentId:a,isExpanded:!1,isPlaceholder:!0})};if(this.isRootWindowed()){let r=this._totalRecords??this._data.length;for(let l=0;l<r;l++){let d=Math.floor(l/t)+1,a=l%t,h=d===1?this._data[a]:this._pageCache.get(d)?.[a];h===void 0?e.push({row:void 0,key:`__placeholder-root-${l}`,depth:0,parentId:null,page:d,isExpanded:!1,isPlaceholder:!0}):o(h,0,null)}}else{let r=this._autoSort&&this._sortColumns.length>0?q(this._data,this._sortColumns):this._data;for(let l of r)o(l,0,null)}return this._cachedFlatList=e}computeVisibleRows(){let e=this.getFlatList(),t=this._tree?0:this._pagination?(this._page-1)*this._perPage:0,s=e,o=0;if(this._virtualScroll){let{startIndex:r,endIndex:l}=this._virtualRange;s=e.slice(r,l),o=r}return s.map((r,l)=>{let d=t+o+l;return{row:r.row,rowIndex:d,key:r.key,flat:r}})}extractId(e){return!this._idKey||e==null||typeof e!=`object`?null:typeof this._idKey==`function`?this._idKey(e):e[this._idKey]}extractChildCount(e){if(!this._childCountKey||e==null||typeof e!=`object`)return 0;let t=e[this._childCountKey];return typeof t==`number`&&t>0?t:0}collectDescendantKeys(e){let t=this.extractId(e),s=t!=null?this._childCache.get(t):void 0;if(!s)return[];let o=[];for(let r of s){o.push(this._rowKey(r,-1));let l=this.collectDescendantKeys(r);for(let d of l)o.push(d)}return o}isParentIndeterminate(e){return this._selectionStrategy!==`cascading`||e==null?!1:this.getIndeterminateKeys().has(this._rowKey(e,-1))}getIndeterminateKeys(){if(this._cachedIndeterminateKeys!==null)return this._cachedIndeterminateKeys;let e=new Set;if(this._selectionStrategy!==`cascading`||!this._tree)return this._cachedIndeterminateKeys=e;let t=s=>{let o=this.extractId(s);if(o==null)return{selected:0,total:0};let r=this._childCache.get(o);if(!r||r.length===0)return{selected:0,total:0};let l=0,d=0;for(let a of r){let h=this._rowKey(a,-1),u=this._selectedIds.has(h)?1:0,g=t(a);l+=u+g.selected,d+=1+g.total}return d>0&&l>0&&l<d&&e.add(this._rowKey(s,-1)),{selected:l,total:d}};for(let s of this._data)t(s);return this._cachedIndeterminateKeys=e}toggleExpand(e,t,s,o){o.stopPropagation(),o.preventDefault();let r=this.extractId(e);if(r==null)return;let l=new Set(this._expandedIds),d;l.has(r)?(l.delete(r),d=!1):(l.add(r),d=!0),this._expandedIds=l,d?(this.dispatchEvent(new CustomEvent(`mp-datatable-row-expand`,{detail:{row:e,depth:s,parentId:t},bubbles:!0,composed:!0})),this.extractChildCount(e)>0&&!this._childCache.has(r)&&!this._pendingFetches.has(r)&&this.fetchChildren(r)):this.dispatchEvent(new CustomEvent(`mp-datatable-row-collapse`,{detail:{row:e,depth:s,parentId:t},bubbles:!0,composed:!0})),this.emitExpandedIdsChange(),this.requestUpdate()}emitExpandedIdsChange(){this.dispatchEvent(new CustomEvent(`mp-datatable-expanded-ids-change`,{detail:{expandedIds:new Set(this._expandedIds)},bubbles:!0,composed:!0}))}async fetchWindowPage(e){if(!this._fetch||e<=1)return;this._pendingPageFetches.add(e);let t=this._fetchGeneration;try{let s=await this._fetch({parentId:null,page:e,perPage:this._perPage,sortColumns:[...this._sortColumns]});if(t!==this._fetchGeneration||!this._fetch)return;this._pendingPageFetches.delete(e),this._pageCache.set(e,[...s?.data??[]]);let o=s?.totalRecords==null?null:Math.max(0,Math.floor(s.totalRecords));o!=null&&(this._totalRecords=o),this.requestUpdate()}catch(s){this._pendingPageFetches.delete(e),console.error(`[mp-datatable] fetch failed for root page`,e,s)}}async fetchChildren(e){if(!this._fetch||e==null)return;this._pendingFetches.add(e);let t=this._fetchGeneration;try{let s=await this._fetch({parentId:e,page:1,perPage:this._perPage,sortColumns:[...this._sortColumns]});if(t!==this._fetchGeneration||!this._fetch)return;this._pendingFetches.delete(e),this._childCache.set(e,[...s?.data??[]]),this._childTotals.set(e,s?.totalRecords??s?.data?.length??0),this.requestUpdate()}catch(s){this._pendingFetches.delete(e),console.error(`[mp-datatable] fetch failed for children of`,e,s)}}async loadPage(e){if(!this._fetch)return;let t=this._fetchGeneration,s;try{s=await this._fetch({parentId:null,page:e,perPage:this._perPage,sortColumns:[...this._sortColumns]})}catch(r){console.error(`[mp-datatable] fetch failed for page`,e,r);return}if(t!==this._fetchGeneration||!this._fetch)return;this._data=[...s?.data??[]];let o=s?.totalRecords==null?null:Math.max(0,Math.floor(s.totalRecords));this._totalRecords=o,this._initialFetchDone=!0,this.requestUpdate()}scheduleFetchReload(){!this._fetch||this._reloadScheduled||(this._reloadScheduled=!0,queueMicrotask(()=>{if(this._reloadScheduled=!1,!this._fetch)return;let e=JSON.stringify({s:this._sortColumns,pp:this._perPage,p:this._virtualScroll?1:this._page});this._initialFetchDone&&e===this._lastReloadKey||(this._lastReloadKey=e,this._fetchGeneration++,this._pageCache.clear(),this._pendingPageFetches.clear(),this._childCache.clear(),this._childTotals.clear(),this._pendingFetches.clear(),this.loadPage(this._virtualScroll?1:this._page))}))}onRowKeydown(e,t,s,o,r,l,d,a){if(!a.altKey&&a.composedPath()[0]===a.currentTarget){if(a.key===`ArrowDown`||a.key===`ArrowUp`){a.preventDefault();let h=Array.from(this.renderRoot.querySelectorAll(`tbody tr[data-row-key]`)),g=h[h.findIndex(c=>c.dataset.rowKey===t)+(a.key===`ArrowDown`?1:-1)];g&&g.focus();return}if(this._tree){if(a.key===`ArrowRight`&&d>0&&!l){this.toggleExpand(e,o,r,a);return}if(a.key===`ArrowLeft`&&l){this.toggleExpand(e,o,r,a);return}if((a.key===`Enter`||a.key===` `)&&d>0){this.toggleExpand(e,o,r,a);return}}(a.key===`Enter`||a.key===` `)&&this._selectionMode!==`none`&&(a.preventDefault(),this._focusedRowKey=t,this.handleSelectionOnClick(t,a),this.requestUpdate(),this.dispatchEvent(new CustomEvent(`mp-datatable-row-click`,{detail:{row:e,rowIndex:s,rowKey:t,originalEvent:a},bubbles:!0,composed:!0})))}}hasRowClickListeners(){return!0}onHeaderClick(e,t){if(!(e.sortable??!0)||t.target.closest(`.resize-handle`))return;let s=be(this._sortColumns,e.name,t.shiftKey);this._sortColumns=s,this._page=1;let o=s.find(r=>r.property===e.name);this.liveAnnouncer.announce(this.mergedLabels.announceSorted(e.label??e.name,o?o.direction===`ascending`?`ascending`:`descending`:`none`)),this.requestUpdate(),this.scheduleFetchReload(),this.dispatchEvent(new CustomEvent(`mp-datatable-sort-change`,{detail:{sortColumns:s},bubbles:!0,composed:!0}))}onRowClick(e,t,s,o){o.target.closest(`input[type="checkbox"]`)||(this._focusedRowKey=t,this.handleSelectionOnClick(t,o),this.requestUpdate(),this.dispatchEvent(new CustomEvent(`mp-datatable-row-click`,{detail:{row:e,rowIndex:s,rowKey:t,originalEvent:o},bubbles:!0,composed:!0})))}onRowDblClick(e,t,s,o){this.dispatchEvent(new CustomEvent(`mp-datatable-row-dblclick`,{detail:{row:e,rowIndex:s,rowKey:t,originalEvent:o},bubbles:!0,composed:!0}))}onRowContextMenu(e,t,s,o){this._selectionMode!==`none`&&!this._selectedIds.has(t)&&(this._selectedIds=new Set([t]),this._focusedRowKey=t,this.emitSelectionChange(),this.requestUpdate()),this.dispatchEvent(new CustomEvent(`mp-datatable-row-contextmenu`,{detail:{row:e,rowIndex:s,rowKey:t,originalEvent:o},bubbles:!0,composed:!0,cancelable:!0}))}onRowCheckboxToggle(e,t,s,o){if(this._selectionMode!==`none`){if(this._selectionMode===`single`)this._selectedIds=new Set([t]);else{let r=new Set(this._selectedIds),l=!r.has(t);if(l?r.add(t):r.delete(t),this._tree&&this._selectionStrategy===`cascading`&&e!=null){let d=this.collectDescendantKeys(e);if(l)for(let a of d)r.add(a);else for(let a of d)r.delete(a)}this._selectedIds=r}this.emitSelectionChange(),this.requestUpdate()}}onDeselectAll(){this._selectedIds.size!==0&&(this._selectedIds=new Set,this.emitSelectionChange(),this.requestUpdate())}handleSelectionOnClick(e,t){if(this._selectionMode!==`none`){if(this._selectionMode===`single`){this._selectedIds=new Set([e]),this.emitSelectionChange();return}if(t.shiftKey&&this._selectionAnchorKey&&this._selectionAnchorKey!==e){let s=this.computeVisibleRows(),o=s.findIndex(l=>l.key===this._selectionAnchorKey),r=s.findIndex(l=>l.key===e);if(o>=0&&r>=0){let[l,d]=o<r?[o,r]:[r,o],a=s.slice(l,d+1).map(h=>h.key);this._selectedIds=new Set([...this._selectedIds,...a]),this.emitSelectionChange();return}}if(t.ctrlKey||t.metaKey){let s=new Set(this._selectedIds);s.has(e)?s.delete(e):s.add(e),this._selectedIds=s,this._selectionAnchorKey=e,this.emitSelectionChange();return}this._selectedIds=new Set([e]),this._selectionAnchorKey=e,this.emitSelectionChange()}}emitSelectionChange(){let e=[...this._selectedIds];this.liveAnnouncer.announce(this.mergedLabels.announceSelection(e.length)),this.dispatchEvent(new CustomEvent(`mp-datatable-selection-change`,{detail:{selectedIds:e,selectedRows:this.resolveRows(e)},bubbles:!0,composed:!0}))}resolveRows(e){if(e.length===0)return[];let t=new Map,s=o=>{for(let r of o)t.set(this._rowKey(r,-1),r)};s(this._data);for(let o of this._pageCache.values())s(o);for(let o of this._childCache.values())s(o);return e.map(o=>t.get(o)).filter(o=>o!==void 0)}gotoPage(e){let t=this._totalRecords??this._data.length,s=Math.max(1,Math.ceil(t/this._perPage));this._page=Math.max(1,Math.min(s,e)),this.liveAnnouncer.announce(this.mergedLabels.announcePage(this._page,s)),this.requestUpdate(),this.scheduleFetchReload(),this.dispatchEvent(new CustomEvent(`mp-datatable-page-change`,{detail:{page:this._page},bubbles:!0,composed:!0}))}onResizeHandleKeydown(e,t){if(!this._resizableColumns||t.key!==`ArrowLeft`&&t.key!==`ArrowRight`)return;t.preventDefault(),t.stopPropagation();let s=t.currentTarget.closest(`th`),o=this._columnWidths.get(e.name)??s?.getBoundingClientRect().width??100,r=Math.max(40,o+(t.key===`ArrowRight`?10:-10));this._columnWidths=new Map(this._columnWidths),this._columnWidths.set(e.name,r),this.requestUpdate()}startColumnResize(e,t){if(!this._resizableColumns)return;t.preventDefault(),t.stopPropagation();let s=t.currentTarget,o=s.closest(`th`);if(!o)return;let r=o.getBoundingClientRect().width;this.resizeState={columnName:e.name,startX:t.clientX,startWidth:r,handle:s},s.classList.add(`active`),s.setPointerCapture(t.pointerId),s.addEventListener(`pointermove`,this.onColumnResizeMove),s.addEventListener(`pointerup`,this.onColumnResizeEnd),s.addEventListener(`pointercancel`,this.onColumnResizeEnd)}}return n})();function qt(n,i){if(n==null||typeof n!=`object`)return``;let e=n[i.name];return e==null?``:String(e)}function Vt(n){return n!=null&&typeof n!=`string`&&typeof n[Symbol.iterator]==`function`}function _e(n){return n==null||n===!1?p$1:(n instanceof Node||typeof n==`object`&&`_$litType$`in n,n)}typeof customElements<`u`&&!customElements.get(`mp-datatable`)&&customElements.define(`mp-datatable`,ct);var Ht=[`datatable`];var K=class{constructor(i){this.sortColumns=[],this.pageNumberOfBoxes=11,Object.assign(this,i),i&&i.perPage?this.perPage=i.perPage:this.perPage={values:[10,20,50],selected:20},i&&i.page?this.page=i.page:this.page={values:[1],selected:1}}toPagination(){return{sortColumns:this.sortColumns,perPage:this.perPage.selected,page:this.page.selected}}};var gt=(()=>{class n{constructor(){this.templateRef=p(ro),this.name=Ir(``,{alias:`bsDatatableColumn`}),this.sortable=Ir(!0,{alias:`bsDatatableColumnSortable`})}static{this.ɵfac=function(t){return new(t||n)}}static{this.ɵdir=Ot$2({type:n,selectors:[[``,`bsDatatableColumn`,``]],inputs:{name:[1,`bsDatatableColumn`,`name`],sortable:[1,`bsDatatableColumnSortable`,`sortable`]}})}}return n})();var ut=(()=>{class n{constructor(){this.templateRef=p(ro)}static ngTemplateContextGuard(e,t){return!0}static{this.ɵfac=function(t){return new(t||n)}}static{this.ɵdir=Ot$2({type:n,selectors:[[``,`bsRowTemplate`,``]]})}}return n})();var xe=class{constructor(){this.$implicit=void 0,this.depth=0,this.isExpanded=!1,this.isPlaceholder=!1}};var ui=(()=>{class n{constructor(){this.platformId=p(si),this.vcr=p(rn$1),this.destroyRef=p(De$2),this.columnsInput=Ir(null,{alias:`columns`}),this.data=Ir(null),this.fetch=Ir(null),this.settings=g$(new K),this.selectionMode=Ir(`none`),this.selectable=Ir(void 0),this.selection=g$([]),this.rowKey=Ir((e,t)=>{let s=e;return s&&s.id!=null?String(s.id):`row-${t}`}),this.resizableColumns=Ir(!0),this.pagination=Ir(!0),this.virtualScroll=Ir(!1),this.itemSize=Ir(40),this.virtualBuffer=Ir(10),this.isResponsive=Ir(!1),this.compareWith=Ir(void 0),this.rowClick=p$(),this.rowDblClick=p$(),this.rowContextMenu=p$(),this.tree=Ir(!1),this.idKey=Ir(null),this.childCountKey=Ir(null),this.treeIndent=Ir(1.25),this.expandedIds=g$(new Set),this.selectionStrategy=Ir(`flat`),this.rowExpand=p$(),this.rowCollapse=p$(),this.datatableRef=m$(`datatable`),this.columnDirectives=D$(gt),this.rowTemplate=y$(ut),this.effectiveColumns=On(()=>{let e=this.columnsInput();return e&&e.length?e:this.columnDirectives().map(t=>{let s;return{name:t.name(),sortable:t.sortable(),headerRenderer:()=>{s||(s=this.vcr.createEmbeddedView(t.templateRef),this.headerViews.push(s)),s.detectChanges();let o=s.rootNodes.filter(l=>l instanceof Node);if(o.length===0)return``;if(o.length===1)return o[0];let r=document.createDocumentFragment();for(let l of o)r.appendChild(l);return r}}})}),this.headerViews=[],this.rowViews=new Map,this.destroyRef.onDestroy(()=>{for(let e of this.headerViews)e.destroy();this.headerViews=[];for(let e of this.rowViews.values())e.destroy();this.rowViews.clear()}),zl(()=>{let e=this.datatableRef()?.nativeElement;e&&(z2(this.platformId)||(e.fetch=this.fetch()??null))}),zl(()=>{let e=this.datatableRef()?.nativeElement;e&&(e.columns=this.effectiveColumns())}),zl(()=>{let e=this.datatableRef()?.nativeElement;if(!e||this.fetch())return;e.data=this.data()??[]}),zl(()=>{let e=this.datatableRef()?.nativeElement;if(!e)return;let t=this.settings(),s=!!this.fetch(),o=this.virtualScroll();e.sortColumns=t.sortColumns.map(r=>({property:r.property,direction:r.direction})),e.autoSort=!s,e.pagination=this.pagination()&&!o,e.page=t.page.selected,e.perPage=t.perPage.selected,e.perPageOptions=t.perPage.values}),zl(()=>{let e=this.datatableRef()?.nativeElement;if(!e)return;e.selectionMode=this.selectable()??this.selectionMode()}),zl(()=>{let e=this.datatableRef()?.nativeElement;e&&(e.rowKey=(t,s)=>this.rowKey()(t,s))}),zl(()=>{let e=this.datatableRef()?.nativeElement;e&&(e.resizableColumns=this.resizableColumns())}),zl(()=>{let e=this.datatableRef()?.nativeElement;e&&(e.virtualScroll=this.virtualScroll(),e.itemSize=this.itemSize(),e.virtualBuffer=this.virtualBuffer())}),zl(()=>{let e=this.datatableRef()?.nativeElement;e&&(e.tree=this.tree(),e.idKey=this.idKey(),e.childCountKey=this.childCountKey(),e.treeIndent=this.treeIndent(),e.selectionStrategy=this.selectionStrategy())}),zl(()=>{let e=this.expandedIds(),t=this.datatableRef()?.nativeElement;if(!t)return;let s=t.expandedIds;pt(s,e)||(t.expandedIds=e)}),zl(()=>{let e=this.datatableRef()?.nativeElement;if(!e)return;let t=this.rowTemplate();if(!t){e.rowRenderer=void 0;for(let s of this.rowViews.values())s.destroy();this.rowViews.clear();return}e.rowRenderer=this.buildRowRenderer(t)}),zl(()=>{let e=this.datatableRef()?.nativeElement;if(!e)return;let t=this.selection(),s=this.rowKey();e.selectedIds=t.map((o,r)=>s(o,r))})}ngAfterViewInit(){}buildRowRenderer(e){return(t,s,o)=>{let r=o?.isPlaceholder??t===void 0,l=o?.depth??0,d=o?.isExpanded??!1,a=r?`__placeholder-${s}`:this.rowKey()(t,s),h=this.rowViews.get(a);if(h)h.context.$implicit=t,h.context.index=s,h.context.depth=l,h.context.isExpanded=d,h.context.isPlaceholder=r;else{let g=new xe;g.$implicit=t,g.index=s,g.depth=l,g.isExpanded=d,g.isPlaceholder=r,h=this.vcr.createEmbeddedView(e.templateRef,g),this.rowViews.set(a,h)}return h.detectChanges(),h.rootNodes.filter(g=>g instanceof Node)}}onSortChange(e){let t=e.detail,s$3=this.settings();this.settings.set(new K(s(r({},s$3),{sortColumns:t.sortColumns,page:s(r({},s$3.page),{selected:1})})))}onSelectionChange(e){let t=e.detail;this.selection.set([...t.selectedRows])}onPageChange(e){let t=e.detail,s$4=this.settings();this.settings.set(new K(s(r({},s$4),{page:s(r({},s$4.page),{selected:t.page})})))}onPerPageChange(e){let t=e.detail,s$5=this.settings();this.settings.set(new K(s(r({},s$5),{perPage:s(r({},s$5.perPage),{selected:t.perPage}),page:s(r({},s$5.page),{selected:1})})))}onRowClick(e){this.rowClick.emit(this.toBsEvent(e))}onRowDblClick(e){this.rowDblClick.emit(this.toBsEvent(e))}onRowContextMenu(e){this.rowContextMenu.emit(this.toBsEvent(e))}onRowExpand(e){let t=e.detail;this.rowExpand.emit({row:t.row,depth:t.depth,parentId:t.parentId})}onRowCollapse(e){let t=e.detail;this.rowCollapse.emit({row:t.row,depth:t.depth,parentId:t.parentId})}onExpandedIdsChange(e){let t=e.detail,s=new Set(t.expandedIds);pt(this.expandedIds(),s)||this.expandedIds.set(s)}toBsEvent(e){let t=e.detail;return{row:t.row,rowIndex:t.rowIndex,rowKey:t.rowKey,originalEvent:t.originalEvent}}static{this.ɵfac=function(t){return new(t||n)}}static{this.ɵcmp=xi({type:n,selectors:[[`bs-datatable`]],contentQueries:function(t,s,o){t&1&&wy(o,s.columnDirectives,gt,4)(o,s.rowTemplate,ut,5),t&2&&JT(2)},viewQuery:function(t,s){t&1&&Iy(s.datatableRef,Ht,5),t&2&&JT()},inputs:{columnsInput:[1,`columns`,`columnsInput`],data:[1,`data`],fetch:[1,`fetch`],settings:[1,`settings`],selectionMode:[1,`selectionMode`],selectable:[1,`selectable`],selection:[1,`selection`],rowKey:[1,`rowKey`],resizableColumns:[1,`resizableColumns`],pagination:[1,`pagination`],virtualScroll:[1,`virtualScroll`],itemSize:[1,`itemSize`],virtualBuffer:[1,`virtualBuffer`],isResponsive:[1,`isResponsive`],compareWith:[1,`compareWith`],tree:[1,`tree`],idKey:[1,`idKey`],childCountKey:[1,`childCountKey`],treeIndent:[1,`treeIndent`],expandedIds:[1,`expandedIds`],selectionStrategy:[1,`selectionStrategy`]},outputs:{settings:`settingsChange`,selection:`selectionChange`,rowClick:`rowClick`,rowDblClick:`rowDblClick`,rowContextMenu:`rowContextMenu`,expandedIds:`expandedIdsChange`,rowExpand:`rowExpand`,rowCollapse:`rowCollapse`},decls:2,vars:0,consts:[[`datatable`,``],[`bsForwardAria`,``,1,`bs-datatable`,3,`mp-datatable-sort-change`,`mp-datatable-selection-change`,`mp-datatable-page-change`,`mp-datatable-per-page-change`,`mp-datatable-row-click`,`mp-datatable-row-dblclick`,`mp-datatable-row-contextmenu`,`mp-datatable-row-expand`,`mp-datatable-row-collapse`,`mp-datatable-expanded-ids-change`]],template:function(t,s){t&1&&(Xa(0,`mp-datatable`,1,0),vc(`mp-datatable-sort-change`,function(r){return s.onSortChange(r)})(`mp-datatable-selection-change`,function(r){return s.onSelectionChange(r)})(`mp-datatable-page-change`,function(r){return s.onPageChange(r)})(`mp-datatable-per-page-change`,function(r){return s.onPerPageChange(r)})(`mp-datatable-row-click`,function(r){return s.onRowClick(r)})(`mp-datatable-row-dblclick`,function(r){return s.onRowDblClick(r)})(`mp-datatable-row-contextmenu`,function(r){return s.onRowContextMenu(r)})(`mp-datatable-row-expand`,function(r){return s.onRowExpand(r)})(`mp-datatable-row-collapse`,function(r){return s.onRowCollapse(r)})(`mp-datatable-expanded-ids-change`,function(r){return s.onExpandedIdsChange(r)}),Af())},dependencies:[_r],styles:[`[_nghost-%COMP%]{display:block;width:100%}.bs-datatable[_ngcontent-%COMP%]{display:block;width:100%}`]})}}return n})();function pt(n,i){if(n==null&&i==null)return!0;if(n==null||i==null)return!1;if((n instanceof Set?n.size:n.length)!==(i instanceof Set?i.size:i.length))return!1;let s=i instanceof Set?i:new Set(i);for(let o of n)if(!s.has(o))return!1;return!0}export{ie as $,rt as A,Le as B,it as C,ot as D,ms as E,ut as F,Ss as G,P as H,A as I,_r as J,T as K,Ar as L,st as M,tt as N,ps as O,ui as P,gs as Q,Cs as R,is as S,le as T,R as U,Ns as V,Rs as W,ee as X,de$1 as Y,g as Z,et as _,ln as _t,Nn as a,v as at,gt as b,xn as bt,U as c,yr as ct,Ye as d,Gt$1 as dt,k as et,Yt as f,O$1 as ft,bs as g,jt$1 as gt,ae as h,jn as ht,K as i,re as it,ss as j,rs as k,Xe as l,ys as lt,_s as m,ht$1 as mt,Gt as n,ks as nt,Ot as o,vr as ot,Ze as p,Tn as pt,Ue as q,Je as r,m as rt,Rt as s,vs as st,$t as t,kr as tt,Xt as u,Bn as ut,fs as v,qe$1 as vt,j as w,he as x,ge as y,sn as yt,I as z};