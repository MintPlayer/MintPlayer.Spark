namespace MintPlayer.Spark.Embed;

/// <summary>
/// The one script an organiser adds to their own site (commerce PRD X23, GB8), served at <c>/spark/embed/loader.js</c>:
/// <code>
/// &lt;script src="https://tickets.example/spark/embed/loader.js" async&gt;&lt;/script&gt;
/// &lt;spark-embed src="/embed/events/summer-fest" title="Tickets"&gt;&lt;a href="https://tickets.example/events/summer-fest"&gt;Buy tickets&lt;/a&gt;&lt;/spark-embed&gt;
/// </code>
/// It defines the <c>&lt;spark-embed&gt;</c> web component: an iframe onto the platform's embed route (the children are
/// the no-script fallback it replaces), resized by the embed's <c>spark-embed:resize</c> messages, and navigated
/// top-level on <c>spark-embed:navigate</c> (the hand-over fallback when a popup is blocked). Messages are accepted only
/// from that iframe's window and the platform's origin; a navigation only to the platform's origin.
/// </summary>
/// <remarks>Plain JavaScript without a build step, so the organiser's page needs nothing else. The protocol is mirrored in
/// <c>@mintplayer/ng-spark/embed</c> (<c>SPARK_EMBED_MESSAGES</c>).</remarks>
internal static class SparkEmbedLoaderScript
{
    internal const string Source = """
        (function () {
          'use strict';
          if (!window.customElements || window.customElements.get('spark-embed')) return;
          var script = document.currentScript;
          var platform = new URL(script && script.src ? script.src : location.href).origin;

          class SparkEmbed extends HTMLElement {}

          SparkEmbed.prototype.connectedCallback = function () {
            if (this._frame) return;
            var src;
            try { src = new URL(this.getAttribute('src') || '/', platform); } catch (e) { return; }
            if (src.origin !== platform) { console.error('spark-embed: src must be on ' + platform); return; }
            var frame = document.createElement('iframe');
            frame.src = src.href;
            frame.title = this.getAttribute('title') || 'Embedded content';
            frame.loading = 'lazy';
            frame.style.width = '100%';
            frame.style.border = '0';
            frame.style.display = 'block';
            frame.style.height = (parseInt(this.getAttribute('height'), 10) || 480) + 'px';
            this._onMessage = function (event) {
              if (event.source !== frame.contentWindow || event.origin !== platform) return;
              var message = event.data;
              if (!message || typeof message !== 'object') return;
              if (message.type === 'spark-embed:resize' && typeof message.height === 'number' && isFinite(message.height)) {
                frame.style.height = Math.max(0, Math.ceil(message.height)) + 'px';
              } else if (message.type === 'spark-embed:navigate' && typeof message.url === 'string') {
                var target;
                try { target = new URL(message.url, platform); } catch (e) { return; }
                if (target.origin === platform) window.location.assign(target.href);
              }
            };
            window.addEventListener('message', this._onMessage);
            this.replaceChildren(frame);
            this._frame = frame;
            this.dispatchEvent(new CustomEvent('spark-embed:ready', { bubbles: true }));
          };

          SparkEmbed.prototype.disconnectedCallback = function () {
            if (this._onMessage) window.removeEventListener('message', this._onMessage);
            if (this._frame) this._frame.remove();
            this._frame = null;
            this._onMessage = null;
          };

          window.customElements.define('spark-embed', SparkEmbed);
        })();
        """;
}
