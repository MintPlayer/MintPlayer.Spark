// SeoTester's init script (docs/seo_analyzer_PRD.md §6, M10): runs in every frame before the page's own scripts.
// It removes the ways out that Playwright's request routing cannot see (UDP: WebRTC, WebTransport), records a
// service worker registration instead of performing it (service workers are blocked), and records what the render
// checks read later (client-side route changes, the Largest Contentful Paint element). The browser flags
// (ChromiumBrowser.HardeningArgs) are the second layer; spike S3's escape test proves both.
(() => {
  const remove = (name) => {
    try { delete window[name]; } catch (e) { }
    try { Object.defineProperty(window, name, { value: undefined, configurable: false, writable: false }); } catch (e) { }
  };
  ['RTCPeerConnection', 'webkitRTCPeerConnection', 'RTCDataChannel', 'RTCIceCandidate', 'WebTransport'].forEach(remove);

  try {
    const container = navigator.serviceWorker;
    if (container) {
      const register = function (url, options) {
        try {
          if (!window.__seoSw)
            window.__seoSw = { url: new URL(String(url), location.href).href, scope: options && options.scope ? String(options.scope) : null };
        } catch (e) { }
        // Never settles: the page waits as it would for a slow registration, and nothing is installed.
        return new Promise(() => { });
      };
      Object.defineProperty(container, 'register', { value: register, configurable: false, writable: false });
    }
  } catch (e) { }

  try {
    window.__seoPush = 0;
    const push = history.pushState;
    history.pushState = function () { window.__seoPush++; return push.apply(this, arguments); };
  } catch (e) { }

  try {
    window.__seoLcp = null;
    new PerformanceObserver((list) => {
      const entries = list.getEntries();
      const last = entries[entries.length - 1];
      if (last) window.__seoLcp = last.element || null;
    }).observe({ type: 'largest-contentful-paint', buffered: true });
  } catch (e) { }
})();
