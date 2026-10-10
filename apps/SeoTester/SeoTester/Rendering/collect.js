// SeoTester's collector (M10): what the render checks read of one viewport, as plain JSON. Bounded everywhere: a
// hostile page can make it slow or lie, never make it return something unbounded.
() => {
  const doc = document, root = doc.documentElement, body = doc.body;
  const sameSite = (href) => {
    try {
      const u = new URL(href, location.href);
      if ((u.protocol !== 'http:' && u.protocol !== 'https:') || u.hostname !== location.hostname) return null;
      return (u.origin + u.pathname + u.search).slice(0, 500);
    } catch (e) { return null; }
  };
  const links = [];
  const seen = new Set();
  for (const a of doc.querySelectorAll('a[href]')) {
    const href = sameSite(a.getAttribute('href'));
    if (href && !seen.has(href)) { seen.add(href); links.push(href); if (links.length >= 500) break; }
  }
  const text = body ? (body.innerText || '') : '';
  const words = (text.slice(0, 2000000).match(/\S+/g) || []).length;

  let chars = 0, small = 0, visited = 0;
  if (body) {
    const walker = doc.createTreeWalker(body, NodeFilter.SHOW_TEXT);
    while (walker.nextNode() && visited < 20000) {
      visited++;
      const value = (walker.currentNode.nodeValue || '').trim();
      const el = walker.currentNode.parentElement;
      if (!value || !el || el.closest('script,style,noscript,template')) continue;
      const style = getComputedStyle(el);
      if (style.display === 'none' || style.visibility === 'hidden') continue;
      const rect = el.getBoundingClientRect();
      if (rect.width === 0 || rect.height === 0) continue;
      chars += value.length;
      if (parseFloat(style.fontSize) < 12) small += value.length;
    }
  }

  const images = [];
  for (const img of Array.from(doc.images).slice(0, 200)) {
    const rect = img.getBoundingClientRect();
    images.push({
      source: String(img.currentSrc || img.src || '').slice(0, 300),
      lazy: (img.getAttribute('loading') || '').toLowerCase() === 'lazy',
      aboveFold: rect.top < innerHeight && rect.bottom > 0,
    });
  }

  const lcp = window.__seoLcp && window.__seoLcp.isConnected !== false ? window.__seoLcp : null;
  return {
    // The device's width, not innerWidth: an emulated phone zooms out to fit overflowing content, widening the layout
    // viewport (M10: a 900 px block at 375 px was not seen as overflow when compared with innerWidth).
    viewportWidth: Math.min(innerWidth, screen.width || innerWidth),
    scrollWidth: root ? root.scrollWidth : 0,
    title: String(doc.title || '').slice(0, 300),
    h1Count: doc.querySelectorAll('h1').length,
    words: words,
    links: links,
    textCharacters: chars,
    smallTextCharacters: small,
    images: images,
    lcpTag: lcp ? String(lcp.tagName || '') : null,
    lcpSource: lcp && lcp.tagName === 'IMG' ? String(lcp.currentSrc || lcp.src || '').slice(0, 300) : null,
    lcpLazy: !!(lcp && lcp.tagName === 'IMG' && (lcp.getAttribute('loading') || '').toLowerCase() === 'lazy'),
  };
}
