import{a as X,c as b,l as p$1,t as Ht}from"./chunk-CwXwit_b.js";var p=X(`:host {
  display: flex;
  flex-direction: column;
  position: relative;
  --mp-shell-size: 15rem;
  --mp-shell-open: 0;
  --mp-shell-vis: hidden;
  --mp-shell-vis-delay: 0.3s;
  --mp-shell-wide: 0;
}

.topbar {
  display: flex;
  align-items: center;
  flex: 0 0 auto;
}

.sidebar-root {
  display: flex;
  align-items: stretch;
  overflow: hidden;
  flex: 1 1 auto;
  min-block-size: 0;
  position: relative;
}

.sidebar {
  flex: 0 0 auto;
  overflow-x: hidden;
  overflow-y: auto;
  min-block-size: 0;
  visibility: var(--mp-shell-vis, visible);
  transition: transform 0.3s ease-in-out, inline-size 0.3s ease-in-out, visibility 0s var(--mp-shell-vis-delay, 0s);
}

.content {
  flex: 1 1 auto;
  min-inline-size: 0;
  min-block-size: 0;
  overflow-y: auto;
  transition: margin-inline-start 0.3s ease-in-out;
}

.shell-toggle {
  position: absolute;
  inline-size: 1px;
  block-size: 1px;
  margin: -1px;
  padding: 0;
  overflow: hidden;
  clip: rect(0 0 0 0);
  white-space: nowrap;
  border: 0;
}

.shell-toggle:focus-visible ~ .topbar .shell-hamburger {
  outline: 2px solid var(--bs-primary, #0d6efd);
  outline-offset: 2px;
}

.skip-link {
  position: absolute;
  inset-block-start: 0;
  inset-inline-start: 0;
  z-index: 1050;
  padding: 0.5rem 1rem;
  background: var(--bs-body-bg, #fff);
  color: var(--bs-body-color, #212529);
  transform: translateY(-200%);
}
.skip-link:focus-visible {
  transform: none;
  outline: 2px solid var(--bs-primary, #0d6efd);
}

.shell-hamburger {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  flex: 0 0 auto;
  cursor: pointer;
  padding: 0.5rem 0.75rem;
  font-size: 1.25rem;
  line-height: 1;
  user-select: none;
}

:host([external-toggle]) .shell-hamburger {
  display: none;
}

:host([breakpoint=xs]) .sidebar {
  position: absolute;
  inset-block: 0;
  inset-inline-start: 0;
  inline-size: 100%;
  z-index: 1030;
  transform: translateX(calc((var(--mp-shell-open) - 1) * 100%));
}
:host([breakpoint=xs]) .content {
  margin-inline-start: 0;
}
:host([breakpoint=xs]) .sidebar-root {
  --mp-shell-wide: 0;
}

@media (min-width: 576px) {
  :host([breakpoint=sm]) .sidebar {
    position: absolute;
    inset-block: 0;
    inset-inline-start: 0;
    inline-size: var(--mp-shell-size);
    z-index: 1030;
    transform: translateX(calc((var(--mp-shell-open) - 1) * 100%));
  }
  :host([breakpoint=sm]) .content {
    margin-inline-start: calc(var(--mp-shell-open) * var(--mp-shell-size));
  }
  :host([breakpoint=sm]) .sidebar-root {
    --mp-shell-wide: 1;
  }
}
@media (max-width: 575.98px) {
  :host([breakpoint=sm]) .sidebar {
    position: absolute;
    inset-block: 0;
    inset-inline-start: 0;
    inline-size: 100%;
    z-index: 1030;
    transform: translateX(calc((var(--mp-shell-open) - 1) * 100%));
  }
  :host([breakpoint=sm]) .content {
    margin-inline-start: 0;
  }
  :host([breakpoint=sm]) .sidebar-root {
    --mp-shell-wide: 0;
  }
}

@media (min-width: 768px) {
  :host([breakpoint=md]) .sidebar {
    position: absolute;
    inset-block: 0;
    inset-inline-start: 0;
    inline-size: var(--mp-shell-size);
    z-index: 1030;
    transform: translateX(calc((var(--mp-shell-open) - 1) * 100%));
  }
  :host([breakpoint=md]) .content {
    margin-inline-start: calc(var(--mp-shell-open) * var(--mp-shell-size));
  }
  :host([breakpoint=md]) .sidebar-root {
    --mp-shell-wide: 1;
  }
}
@media (max-width: 767.98px) {
  :host([breakpoint=md]) .sidebar {
    position: absolute;
    inset-block: 0;
    inset-inline-start: 0;
    inline-size: 100%;
    z-index: 1030;
    transform: translateX(calc((var(--mp-shell-open) - 1) * 100%));
  }
  :host([breakpoint=md]) .content {
    margin-inline-start: 0;
  }
  :host([breakpoint=md]) .sidebar-root {
    --mp-shell-wide: 0;
  }
}

@media (min-width: 992px) {
  :host([breakpoint=lg]) .sidebar {
    position: absolute;
    inset-block: 0;
    inset-inline-start: 0;
    inline-size: var(--mp-shell-size);
    z-index: 1030;
    transform: translateX(calc((var(--mp-shell-open) - 1) * 100%));
  }
  :host([breakpoint=lg]) .content {
    margin-inline-start: calc(var(--mp-shell-open) * var(--mp-shell-size));
  }
  :host([breakpoint=lg]) .sidebar-root {
    --mp-shell-wide: 1;
  }
}
@media (max-width: 991.98px) {
  :host([breakpoint=lg]) .sidebar {
    position: absolute;
    inset-block: 0;
    inset-inline-start: 0;
    inline-size: 100%;
    z-index: 1030;
    transform: translateX(calc((var(--mp-shell-open) - 1) * 100%));
  }
  :host([breakpoint=lg]) .content {
    margin-inline-start: 0;
  }
  :host([breakpoint=lg]) .sidebar-root {
    --mp-shell-wide: 0;
  }
}

@media (min-width: 1200px) {
  :host([breakpoint=xl]) .sidebar {
    position: absolute;
    inset-block: 0;
    inset-inline-start: 0;
    inline-size: var(--mp-shell-size);
    z-index: 1030;
    transform: translateX(calc((var(--mp-shell-open) - 1) * 100%));
  }
  :host([breakpoint=xl]) .content {
    margin-inline-start: calc(var(--mp-shell-open) * var(--mp-shell-size));
  }
  :host([breakpoint=xl]) .sidebar-root {
    --mp-shell-wide: 1;
  }
}
@media (max-width: 1199.98px) {
  :host([breakpoint=xl]) .sidebar {
    position: absolute;
    inset-block: 0;
    inset-inline-start: 0;
    inline-size: 100%;
    z-index: 1030;
    transform: translateX(calc((var(--mp-shell-open) - 1) * 100%));
  }
  :host([breakpoint=xl]) .content {
    margin-inline-start: 0;
  }
  :host([breakpoint=xl]) .sidebar-root {
    --mp-shell-wide: 0;
  }
}

@media (min-width: 1400px) {
  :host([breakpoint=xxl]) .sidebar {
    position: absolute;
    inset-block: 0;
    inset-inline-start: 0;
    inline-size: var(--mp-shell-size);
    z-index: 1030;
    transform: translateX(calc((var(--mp-shell-open) - 1) * 100%));
  }
  :host([breakpoint=xxl]) .content {
    margin-inline-start: calc(var(--mp-shell-open) * var(--mp-shell-size));
  }
  :host([breakpoint=xxl]) .sidebar-root {
    --mp-shell-wide: 1;
  }
}
@media (max-width: 1399.98px) {
  :host([breakpoint=xxl]) .sidebar {
    position: absolute;
    inset-block: 0;
    inset-inline-start: 0;
    inline-size: 100%;
    z-index: 1030;
    transform: translateX(calc((var(--mp-shell-open) - 1) * 100%));
  }
  :host([breakpoint=xxl]) .content {
    margin-inline-start: 0;
  }
  :host([breakpoint=xxl]) .sidebar-root {
    --mp-shell-wide: 0;
  }
}

:host([state=show]) {
  --mp-shell-open: 1;
  --mp-shell-vis: visible;
  --mp-shell-vis-delay: 0s;
}

:host([state=hide]) {
  --mp-shell-open: 0;
  --mp-shell-vis: hidden;
  --mp-shell-vis-delay: 0.3s;
}

:host([breakpoint=xs]:not([state=show]):not([state=hide]):not([external-toggle])) .shell-toggle:not(:checked) ~ .sidebar-root {
  --mp-shell-open: 0;
  --mp-shell-vis: hidden;
  --mp-shell-vis-delay: 0.3s;
}
:host([breakpoint=xs]:not([state=show]):not([state=hide]):not([external-toggle])) .shell-toggle:checked ~ .sidebar-root {
  --mp-shell-open: 1;
  --mp-shell-vis: visible;
  --mp-shell-vis-delay: 0s;
}

@media (min-width: 576px) {
  :host([breakpoint=sm]:not([state=show]):not([state=hide]):not([external-toggle])) .shell-toggle:not(:checked) ~ .sidebar-root {
    --mp-shell-open: 1;
    --mp-shell-vis: visible;
    --mp-shell-vis-delay: 0s;
  }
  :host([breakpoint=sm]:not([state=show]):not([state=hide]):not([external-toggle])) .shell-toggle:checked ~ .sidebar-root {
    --mp-shell-open: 0;
    --mp-shell-vis: hidden;
    --mp-shell-vis-delay: 0.3s;
  }
}
@media (max-width: 575.98px) {
  :host([breakpoint=sm]:not([state=show]):not([state=hide]):not([external-toggle])) .shell-toggle:not(:checked) ~ .sidebar-root {
    --mp-shell-open: 0;
    --mp-shell-vis: hidden;
    --mp-shell-vis-delay: 0.3s;
  }
  :host([breakpoint=sm]:not([state=show]):not([state=hide]):not([external-toggle])) .shell-toggle:checked ~ .sidebar-root {
    --mp-shell-open: 1;
    --mp-shell-vis: visible;
    --mp-shell-vis-delay: 0s;
  }
}

@media (min-width: 768px) {
  :host([breakpoint=md]:not([state=show]):not([state=hide]):not([external-toggle])) .shell-toggle:not(:checked) ~ .sidebar-root {
    --mp-shell-open: 1;
    --mp-shell-vis: visible;
    --mp-shell-vis-delay: 0s;
  }
  :host([breakpoint=md]:not([state=show]):not([state=hide]):not([external-toggle])) .shell-toggle:checked ~ .sidebar-root {
    --mp-shell-open: 0;
    --mp-shell-vis: hidden;
    --mp-shell-vis-delay: 0.3s;
  }
}
@media (max-width: 767.98px) {
  :host([breakpoint=md]:not([state=show]):not([state=hide]):not([external-toggle])) .shell-toggle:not(:checked) ~ .sidebar-root {
    --mp-shell-open: 0;
    --mp-shell-vis: hidden;
    --mp-shell-vis-delay: 0.3s;
  }
  :host([breakpoint=md]:not([state=show]):not([state=hide]):not([external-toggle])) .shell-toggle:checked ~ .sidebar-root {
    --mp-shell-open: 1;
    --mp-shell-vis: visible;
    --mp-shell-vis-delay: 0s;
  }
}

@media (min-width: 992px) {
  :host([breakpoint=lg]:not([state=show]):not([state=hide]):not([external-toggle])) .shell-toggle:not(:checked) ~ .sidebar-root {
    --mp-shell-open: 1;
    --mp-shell-vis: visible;
    --mp-shell-vis-delay: 0s;
  }
  :host([breakpoint=lg]:not([state=show]):not([state=hide]):not([external-toggle])) .shell-toggle:checked ~ .sidebar-root {
    --mp-shell-open: 0;
    --mp-shell-vis: hidden;
    --mp-shell-vis-delay: 0.3s;
  }
}
@media (max-width: 991.98px) {
  :host([breakpoint=lg]:not([state=show]):not([state=hide]):not([external-toggle])) .shell-toggle:not(:checked) ~ .sidebar-root {
    --mp-shell-open: 0;
    --mp-shell-vis: hidden;
    --mp-shell-vis-delay: 0.3s;
  }
  :host([breakpoint=lg]:not([state=show]):not([state=hide]):not([external-toggle])) .shell-toggle:checked ~ .sidebar-root {
    --mp-shell-open: 1;
    --mp-shell-vis: visible;
    --mp-shell-vis-delay: 0s;
  }
}

@media (min-width: 1200px) {
  :host([breakpoint=xl]:not([state=show]):not([state=hide]):not([external-toggle])) .shell-toggle:not(:checked) ~ .sidebar-root {
    --mp-shell-open: 1;
    --mp-shell-vis: visible;
    --mp-shell-vis-delay: 0s;
  }
  :host([breakpoint=xl]:not([state=show]):not([state=hide]):not([external-toggle])) .shell-toggle:checked ~ .sidebar-root {
    --mp-shell-open: 0;
    --mp-shell-vis: hidden;
    --mp-shell-vis-delay: 0.3s;
  }
}
@media (max-width: 1199.98px) {
  :host([breakpoint=xl]:not([state=show]):not([state=hide]):not([external-toggle])) .shell-toggle:not(:checked) ~ .sidebar-root {
    --mp-shell-open: 0;
    --mp-shell-vis: hidden;
    --mp-shell-vis-delay: 0.3s;
  }
  :host([breakpoint=xl]:not([state=show]):not([state=hide]):not([external-toggle])) .shell-toggle:checked ~ .sidebar-root {
    --mp-shell-open: 1;
    --mp-shell-vis: visible;
    --mp-shell-vis-delay: 0s;
  }
}

@media (min-width: 1400px) {
  :host([breakpoint=xxl]:not([state=show]):not([state=hide]):not([external-toggle])) .shell-toggle:not(:checked) ~ .sidebar-root {
    --mp-shell-open: 1;
    --mp-shell-vis: visible;
    --mp-shell-vis-delay: 0s;
  }
  :host([breakpoint=xxl]:not([state=show]):not([state=hide]):not([external-toggle])) .shell-toggle:checked ~ .sidebar-root {
    --mp-shell-open: 0;
    --mp-shell-vis: hidden;
    --mp-shell-vis-delay: 0.3s;
  }
}
@media (max-width: 1399.98px) {
  :host([breakpoint=xxl]:not([state=show]):not([state=hide]):not([external-toggle])) .shell-toggle:not(:checked) ~ .sidebar-root {
    --mp-shell-open: 0;
    --mp-shell-vis: hidden;
    --mp-shell-vis-delay: 0.3s;
  }
  :host([breakpoint=xxl]:not([state=show]):not([state=hide]):not([external-toggle])) .shell-toggle:checked ~ .sidebar-root {
    --mp-shell-open: 1;
    --mp-shell-vis: visible;
    --mp-shell-vis-delay: 0s;
  }
}

@media (prefers-reduced-motion: reduce) {
  .sidebar,
  .content {
    transition: none;
  }
}`);var m=(()=>{class d extends b{static{this.styles=[p]}static get observedAttributes(){return[...super.observedAttributes??[],`state`,`breakpoint`,`size`,`external-toggle`]}createRenderRoot(){return this.constructor.observedAttributes.includes(`defer-hydration`)||this.shadowRoot?.replaceChildren(),super.createRenderRoot()}connectedCallback(){super.connectedCallback(),this.setAttribute(`data-js`,``),this.addEventListener(`click`,this.#i),typeof window<`u`&&window.addEventListener(`resize`,this.#l)}disconnectedCallback(){super.disconnectedCallback(),this.removeEventListener(`click`,this.#i),typeof window<`u`&&window.removeEventListener(`resize`,this.#l),this.#e!==null&&cancelAnimationFrame(this.#e)}#e=null;#l=()=>{this.#e===null&&(this.#e=requestAnimationFrame(()=>{this.#e=null,this.#t()}))};#t(){let e=this.toggleInput;!e||!this.hasAttribute(`data-js`)||(e.setAttribute(`role`,`button`),e.setAttribute(`aria-expanded`,String(this.#s())))}firstUpdated(){this.#t()}#r=e=>{e.preventDefault(),this.renderRoot?.querySelector(`.content`)?.focus()};#i=e=>{if(!this.hasAttribute(`dismiss-on-navigate`)||this.#o())return;let t=e.composedPath(),s=this.renderRoot?.querySelector(`slot[name="sidebar"]`),o=s?t.indexOf(s):-1;if(o<0)return;let n=t.slice(0,o);n.some(l=>l instanceof HTMLElement&&l.hasAttribute(`data-no-dismiss`))||n.some(l=>l instanceof HTMLElement&&l.tagName===`A`&&l.hasAttribute(`href`))&&this.open&&this.toggle(!1)};attributeChangedCallback(e,t,s){super.attributeChangedCallback(e,t,s),e===`size`&&(s?this.style.setProperty(`--mp-shell-size`,s):this.style.removeProperty(`--mp-shell-size`)),e===`state`&&this.#t()}get toggleInput(){return this.renderRoot?.querySelector(`.shell-toggle`)??null}#s(){if(typeof window>`u`)return!1;let e=this.renderRoot?.querySelector(`.sidebar-root`);return e?getComputedStyle(e).getPropertyValue(`--mp-shell-open`).trim()===`1`:!1}#h(){let e=this.getAttribute(`state`);return e===`show`||e===`hide`}#o(){if(typeof window>`u`)return!0;let e=this.renderRoot?.querySelector(`.sidebar-root`);return e?getComputedStyle(e).getPropertyValue(`--mp-shell-wide`).trim()===`1`:!0}#n(e){let t=this.toggleInput;t&&(t.checked=this.#o()?!e:e)}get open(){return this.#s()}toggle(e){let t=e??!this.open;this.#n(t),this.#t(),this.#a(t)}#d=()=>{let e=this.#s(),t=this.#h()?!e:e;this.#n(t),this.#t(),this.#a(t)};#a(e){this.dispatchEvent(new CustomEvent(`statechange`,{detail:{open:e},bubbles:!0,composed:!0}))}render(){return Ht`
      ${this.hasAttribute(`data-js`)?Ht`<a href="#" class="skip-link" @click=${this.#r}>Skip to content</a>`:p$1}
      <input
        type="checkbox"
        id="mp-shell-toggle"
        class="shell-toggle"
        aria-label="Toggle sidebar"
        aria-controls="shell-sidebar"
        @change=${this.#d}
      />
      <div class="topbar" part="topbar" role="banner">
        <label for="mp-shell-toggle" class="shell-hamburger" part="hamburger" title="Toggle sidebar">
          <slot name="hamburger">&#9776;</slot>
        </label>
        <slot name="topbar"></slot>
      </div>
      <div class="sidebar-root" part="sidebar-root">
        <aside id="shell-sidebar" class="sidebar" part="sidebar" aria-label="Sidebar"><slot name="sidebar"></slot></aside>
        <!-- tabindex=0, not -1: .content is the scroll container, and a
             scrollable region only a mouse can reach fails WCAG 2.1.1 (axe
             scrollable-region-focusable). The skip link's focus() target is
             unaffected. -->
        <div class="content" part="content" role="main" tabindex="0"><slot></slot></div>
      </div>
      <slot name="toggle"></slot>
    `}}return d})();customElements.get(`mp-shell`)||customElements.define(`mp-shell`,m);export{m as MpShell,p as shellStyles};