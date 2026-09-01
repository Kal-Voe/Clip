using System;
using System.IO;

namespace Clip.Shell;

/// <summary>
/// Builds the player page shown for video and audio files.
///
/// The controls are drawn here rather than using the browser's own. Chromium no longer exposes
/// its media-control internals to page CSS, so its overflow menu could not be resized and its
/// speed list needed scrolling inside a preview pane.
/// </summary>
internal static class MediaPreviewPage
{
    /// <param name="mediaUrl">
    /// URL the player loads from. This is a virtual-host address rather than a file:// path,
    /// because a generated page has no file-system origin and the browser would refuse it.
    /// </param>
    public static string Build(
        string filePath,
        string mediaUrl,
        bool isVideo,
        string backgroundHex,
        string textHex,
        bool detached = false,
        double startTime = 0)
    {
        var mime = MimeFor(Path.GetExtension(filePath));
        var element = isVideo ? "video" : "audio";

        // The fullscreen button sits on the video itself, so it has no meaning for audio, and it
        // is redundant in the detached mini window.
        var fullscreenButton = isVideo && !detached
            ? """<button id="fs" class="fs" title="Full screen">⛶</button>"""
            : string.Empty;

        // Picture-in-picture is for watching something while doing other work, so it needs a
        // picture — an audio file has none. The mini window is already picture-in-picture and
        // offers "back" instead.
        var pipItem = isVideo && !detached
            ? """<button id="pip"><span>Picture in picture</span></button>"""
            : string.Empty;

        // Video hides its controls until the pointer is over it; audio has no picture to reveal,
        // so its bar stays put.
        var wrapClass = (detached ? "wrap detached" : "wrap") + (isVideo ? " video-mode" : "");

        return $$"""
            <!doctype html>
            <html>
            <head>
            <meta charset="utf-8">
            <style>
              :root { color-scheme: dark; }
              html, body {
                margin: 0;
                height: 100%;
                background: {{backgroundHex}};
                color: {{textHex}};
                font-family: "Segoe UI Variable Text", "Segoe UI", sans-serif;
                overflow: hidden;
              }
              .wrap {
                height: 100%;
                display: flex;
                flex-direction: column;
                align-items: center;
                justify-content: center;
                gap: 10px;
                padding: 12px;
                box-sizing: border-box;
              }

              /* The stage is exactly the video box, so the fullscreen button can be pinned to the
                 picture's own bottom-right corner rather than to the pane. */
              .stage { position: relative; display: flex; min-height: 0; flex: 1; width: 100%; }
              .stage.audio { flex: none; height: 0; }
              video { width: 100%; height: 100%; object-fit: contain; border-radius: 6px; background: #000; }
              audio { display: none; }

              .fs {
                position: absolute;
                right: 10px;
                top: 10px;
                background: rgba(0,0,0,.6);
                border: 1px solid rgba(255,255,255,.25);
                color: #fff;
                border-radius: 5px;
                padding: 3px 7px;
                font-size: 13px;
                line-height: 1;
                cursor: pointer;
              }
              .fs:hover { background: rgba(0,0,0,.8); }

              .bar {
                position: relative;
                display: flex;
                align-items: center;
                gap: 10px;
                width: 100%;
                padding: 6px 12px;
                box-sizing: border-box;
                background: rgba(0,0,0,.55);
                border: 1px solid rgba(255,255,255,.13);
                border-radius: 8px;
                font: 12px/1.4 "Segoe UI Variable Text", "Segoe UI", sans-serif;
                color: #fff;
              }
              .bar button {
                background: transparent;
                border: 0;
                color: #fff;
                cursor: pointer;
                padding: 2px 6px;
                border-radius: 4px;
                font: inherit;
                line-height: 1;
              }
              .bar button:hover { background: rgba(255,255,255,.14); }
              #play { font-size: 13px; }
              #more { font-size: 15px; }
              .time { font-variant-numeric: tabular-nums; opacity: .9; white-space: nowrap; }

              /* A stretchy item will not shrink past its own contents unless told it may, so at
                 small window sizes the scrubber stopped shrinking and pushed the menu button off
                 the end instead. Below a certain width the clock goes rather than the scrubber:
                 the scrubber is the control worth keeping. */
              #seek {
                flex: 1 1 0;
                min-width: 0;
                accent-color: #8ab4ff;
                height: 3px;
                cursor: pointer;
                -webkit-appearance: none;
                background: rgba(255,255,255,.28);
                border-radius: 2px;
              }
              .bar > button { flex: none; }
              .wrap.detached .bar { container-type: inline-size; }
              @container (max-width: 260px) { .time { display: none; } }
              #seek::-webkit-slider-thumb {
                -webkit-appearance: none;
                width: 9px;
                height: 9px;
                border-radius: 50%;
                background: #8ab4ff;
                cursor: pointer;
              }
              .wrap:fullscreen #seek::-webkit-slider-thumb { width: 14px; height: 14px; }

              .menu {
                display: none;
                position: absolute;
                right: 8px;
                bottom: 34px;
                background: rgba(32,32,34,.98);
                border: 1px solid rgba(255,255,255,.16);
                border-radius: 6px;
                padding: 3px;
                box-shadow: 0 6px 18px rgba(0,0,0,.55);
                min-width: 132px;
              }
              .menu.open { display: block; }

              /* Audio has no picture, so its bar sits in the middle of the pane rather than along
                 the bottom, and a menu anchored to the bar has only half the pane to open into
                 whichever way it goes — not enough for eight speeds, so the list lost its ends.
                 Centred on the pane it has the full height either side of the middle, which is
                 exactly enough, and it sits level with the bar it belongs to. */
              .wrap:not(.video-mode):not(.detached) .menu {
                position: fixed;
                right: 8px;
                top: 50%;
                bottom: auto;
                transform: translateY(-50%);
                /* Only bites on a pane too short for the list however it is placed, which the
                   preview pane is not. Scrolling a couple of speeds beats losing them outright. */
                max-height: calc(100% - 16px);
                overflow-y: auto;
              }
              .menu button {
                display: flex;
                justify-content: space-between;
                gap: 14px;
                width: 100%;
                text-align: left;
                /* Tight rows so every speed option fits without scrolling. */
                padding: 4px 9px;
                white-space: nowrap;
              }
              .menu .chev { opacity: .6; }
              .menu .back { border-bottom: 1px solid rgba(255,255,255,.12); margin-bottom: 2px; padding-bottom: 5px; }
              .menu button.on { color: #8ab4ff; font-weight: 600; }
              .panel { display: none; }
              .panel.show { display: block; }

              .missing { font-size: 13px; opacity: .7; text-align: center; padding: 0 24px; }

              /* Corner buttons. Hidden normally, shown when the player is fullscreen or has been
                 moved into a picture-in-picture window. */
              .corner {
                position: absolute;
                top: 10px;
                display: none;
                background: rgba(0,0,0,.62);
                border: 1px solid rgba(255,255,255,.25);
                color: #fff;
                border-radius: 5px;
                padding: 3px 8px;
                font-size: 13px;
                line-height: 1;
                cursor: pointer;
                z-index: 10;
              }
              .corner:hover { background: rgba(0,0,0,.85); }
              #back { left: 10px; }
              #close { right: 10px; }
              /* Video gives its whole area to the picture and floats the controls over it, in the
                 preview pane and fullscreen just as in the mini window. */
              .wrap.video-mode { padding: 0; gap: 0; }
              .wrap.video-mode video { border-radius: 0; }
              .wrap.video-mode .stage { flex: 1; }
              .wrap.video-mode .bar {
                position: absolute;
                left: 0;
                right: 0;
                bottom: 0;
                width: 100%;
                border: 0;
                border-radius: 0;
                background: linear-gradient(to top, rgba(0,0,0,.82), rgba(0,0,0,.35) 70%, transparent);
              }
              .wrap.video-mode .bar,
              .wrap.video-mode .fs {
                opacity: 0;
                transition: opacity .14s ease;
                pointer-events: none;
              }
              /* Plain hover drives this, so the controls work even if the idle-hide script does
                 not run. The script only adds the "still pointer" hide on top. */
              .wrap.video-mode:hover .bar,
              .wrap.video-mode:hover .fs,
              .wrap.video-mode.hot .bar,
              .wrap.video-mode.hot .fs {
                opacity: 1;
                pointer-events: auto;
              }
              .wrap.video-mode.idle .bar,
              .wrap.video-mode.idle .fs {
                opacity: 0;
                pointer-events: none;
              }
              .wrap.video-mode.hot { cursor: default; }
              .wrap.video-mode:not(.hot) { cursor: none; }

              /* Over a window border, everything under the pointer defers to the resize cursor. */
              .wrap.on-edge * { cursor: inherit !important; }

              /* The mini window is just the video. Controls ride on top of it and only appear
                 while the pointer is over the window, so there is no surrounding frame. */
              /* The mini window's controls are anchored to this, not to the page. The page only
                 learns it has been resized once the browser gets round to it, so anything measured
                 against the page is a frame or more out of date while a drag is under way — which
                 is why the close button used to slide off the edge and jump back. The host tells us
                 the size the moment it changes, so this is always right. */
              .wrap.detached {
                padding: 0;
                gap: 0;
                background: #000;
                position: fixed;
                top: 0;
                left: 0;
                /* Whichever of the two is smaller. The host's figure is the newer one and stops the
                   controls trailing a shrinking window, but it is sent before the browser resizes,
                   so trusting it alone would put them past the edge for a frame on the way up. The
                   page's own figure is late but never too big. Taking the smaller is never wrong in
                   either direction, which is what keeps the controls inside the window at all times. */
                width: min(100vw, var(--host-w, 100vw));
                height: min(100vh, var(--host-h, 100vh));
              }
              .wrap.detached video { border-radius: 0; }
              .wrap.detached .stage { flex: 1; }
              .wrap.detached .bar {
                position: absolute;
                left: 0;
                right: 0;
                bottom: 0;
                width: 100%;
                border: 0;
                border-radius: 0;
                background: linear-gradient(to top, rgba(0,0,0,.82), rgba(0,0,0,.35) 70%, transparent);
                padding: 6px 10px 7px;
                gap: 8px;
                font-size: 11px;
              }
              .wrap.detached #play { font-size: 12px; }
              .wrap.detached #more { font-size: 13px; }
              .wrap.detached #seek { height: 3px; }
              .wrap.detached .bar button { padding: 1px 5px; }
              .wrap.detached .menu { bottom: 30px; font-size: 11px; min-width: 122px; }
              .wrap.detached .menu button { padding: 3px 8px; }

              /* Fade the whole overlay with the pointer. */
              .wrap.detached .bar,
              .wrap.detached #back,
              .wrap.detached #close {
                opacity: 0;
                transition: opacity .14s ease;
                pointer-events: none;
              }
              .wrap.detached.hot .bar,
              .wrap.detached.hot #back,
              .wrap.detached.hot #close {
                opacity: 1;
                pointer-events: auto;
              }
              .wrap.detached #back, .wrap.detached #close { display: block; }
              .wrap:fullscreen #close { display: block; }
              .wrap:fullscreen { background: #000; padding: 0; gap: 0; }
              .wrap:fullscreen .fs { display: none; }

              /* Fullscreen is viewed from further away, so the controls scale up to roughly what
                 a video site uses rather than staying at preview-pane size. */
              .wrap:fullscreen .bar {
                border-radius: 0;
                border-left: 0;
                border-right: 0;
                border-bottom: 0;
                gap: 20px;
                padding: 18px 28px;
                font-size: 17px;
              }
              .wrap:fullscreen #play { font-size: 24px; }
              .wrap:fullscreen #more { font-size: 26px; }
              .wrap:fullscreen #seek { height: 7px; }
              .wrap:fullscreen .menu { bottom: 56px; right: 16px; min-width: 178px; font-size: 14px; }
              .wrap:fullscreen .menu button { padding: 7px 13px; }
              .wrap:fullscreen .corner { top: 22px; font-size: 22px; padding: 8px 15px; }
              .wrap:fullscreen #close { right: 18px; }
            </style>
            </head>
            <body>
              <div class="{{wrapClass}}" id="wrap">
                <button class="corner" id="back" title="Back to Clip">↖</button>
                <button class="corner" id="close" title="Close">✕</button>
                <div class="stage {{(isVideo ? "" : "audio")}}">
                  <{{element}} id="player" src="{{mediaUrl}}" type="{{mime}}" preload="metadata"></{{element}}>
                  {{fullscreenButton}}
                </div>

                <div class="bar">
                  <button id="play" title="Play/pause">▶</button>
                  <span class="time" id="time">0:00 / 0:00</span>
                  <input type="range" id="seek" value="0" min="0" max="1000" step="1">
                  <button id="more" title="More">⋮</button>

                  <div class="menu" id="menu">
                    <div class="panel show" id="mainPanel">
                      <button id="dl"><span>Download</span></button>
                      {{pipItem}}
                      <button id="speedOpen"><span>Playback speed</span><span class="chev">›</span></button>
                    </div>
                    <div class="panel" id="speedPanel">
                      <button class="back" id="speedBack"><span>‹&nbsp; Playback speed</span></button>
                      <button data-rate="0.25"><span>0.25</span></button>
                      <button data-rate="0.5"><span>0.5</span></button>
                      <button data-rate="0.75"><span>0.75</span></button>
                      <button data-rate="1" class="on"><span>Normal</span></button>
                      <button data-rate="1.25"><span>1.25</span></button>
                      <button data-rate="1.5"><span>1.5</span></button>
                      <button data-rate="1.75"><span>1.75</span></button>
                      <button data-rate="2"><span>2</span></button>
                    </div>
                  </div>
                </div>
              </div>

              <script>
                const p = document.getElementById('player');
                const play = document.getElementById('play');
                const seek = document.getElementById('seek');
                const time = document.getElementById('time');
                const more = document.getElementById('more');
                const menu = document.getElementById('menu');
                const mainPanel = document.getElementById('mainPanel');
                const speedPanel = document.getElementById('speedPanel');

                const fmt = s => {
                  if (!isFinite(s)) return '0:00';
                  const m = Math.floor(s / 60), r = Math.floor(s % 60);
                  return m + ':' + String(r).padStart(2, '0');
                };
                const sync = () => {
                  time.textContent = fmt(p.currentTime) + ' / ' + fmt(p.duration);
                  if (p.duration) seek.value = (p.currentTime / p.duration) * 1000;
                };

                play.addEventListener('click', () => p.paused ? p.play() : p.pause());
                p.addEventListener('play', () => play.textContent = '❚❚');
                p.addEventListener('pause', () => play.textContent = '▶');
                p.addEventListener('timeupdate', sync);
                p.addEventListener('loadedmetadata', sync);
                seek.addEventListener('input', () => { if (p.duration) p.currentTime = (seek.value / 1000) * p.duration; });

                const showPanel = speed => {
                  mainPanel.classList.toggle('show', !speed);
                  speedPanel.classList.toggle('show', speed);
                };
                const closeMenu = () => { menu.classList.remove('open'); showPanel(false); };

                more.addEventListener('click', e => {
                  e.stopPropagation();
                  if (menu.classList.contains('open')) { closeMenu(); return; }
                  showPanel(false);
                  menu.classList.add('open');
                });
                menu.addEventListener('click', e => e.stopPropagation());
                document.addEventListener('click', closeMenu);

                document.getElementById('speedOpen').addEventListener('click', () => showPanel(true));
                document.getElementById('speedBack').addEventListener('click', () => showPanel(false));

                speedPanel.querySelectorAll('button[data-rate]').forEach(b => b.addEventListener('click', () => {
                  p.playbackRate = parseFloat(b.dataset.rate);
                  speedPanel.querySelectorAll('button[data-rate]').forEach(o => o.classList.toggle('on', o === b));
                  closeMenu();
                }));

                const wrap = document.getElementById('wrap');
                const detached = wrap.classList.contains('detached');
                const isVideoMode = wrap.classList.contains('video-mode');

                // Clip hosts the mini window itself. The browser's own picture-in-picture draws a
                // fixed control set we cannot touch — that is where the stray Settings button and
                // the dead "back to tab" came from — and WebView2 does not support the newer API
                // that would let a page supply its own. So the host is asked to open a small
                // always-on-top Clip window running this same player instead.
                const send = (action) => {
                  try { window.chrome.webview.postMessage(JSON.stringify({ action, time: p.currentTime })); } catch {}
                };

                const pipButton = document.getElementById('pip');
                if (pipButton) pipButton.addEventListener('click', () => { closeMenu(); send('pip'); });

                document.getElementById('back').addEventListener('click', () => send('back'));
                document.getElementById('close').addEventListener('click', () => {
                  if (detached) { send('close'); return; }
                  if (document.fullscreenElement) document.exitFullscreen();
                });

                // Clicking the picture toggles playback, the way a video site does. Buttons and
                // the scrubber are excluded, and in the mini window a click that turned into a
                // window drag does not count.
                const isControl = t => t.closest('button, input, .bar, .menu');

                if (isVideoMode) {
                  // Reveal the controls on movement and hide them again once the pointer has been
                  // still for a moment, so fullscreen playback is unobstructed.
                  let idleTimer;
                  const wake = () => {
                    wrap.classList.add('hot');
                    wrap.classList.remove('idle');
                    clearTimeout(idleTimer);
                    idleTimer = setTimeout(() => {
                      if (!menu.classList.contains('open')) wrap.classList.add('idle');
                    }, 2200);
                  };
                  document.addEventListener('mousemove', wake);
                  document.addEventListener('mouseenter', wake);
                  document.addEventListener('mouseleave', () => {
                    clearTimeout(idleTimer);
                    wrap.classList.remove('hot');
                    wrap.classList.add('idle');
                    closeMenu();
                  });

                  // Show them once on open, then let the same idle timer take them away. Waking
                  // only on pointer movement meant a video selected from the list came up bare:
                  // the pointer is over the row when the preview loads and never moves, so nothing
                  // announced that this thing plays, or where expand and the menu were.
                  wake();
                }

                // How close to the window border counts as grabbing it to resize. Both the gesture
                // and the cursor that advertises it read this, so they cannot drift apart.
                const EDGE = 7;

                // Which border the pointer is over, if any. A real window border wins over whatever
                // it happens to overlap, and here it has to: the control bar runs the full width of
                // the bottom of the window, so treating it as a control first would leave the
                // bottom edge and both bottom corners impossible to grab.
                const edgeAt = e => {
                  const w = document.documentElement.clientWidth;
                  const h = document.documentElement.clientHeight;
                  const l = e.clientX <= EDGE, r = e.clientX >= w - EDGE;
                  const t = e.clientY <= EDGE, b = e.clientY >= h - EDGE;

                  if (t) return l ? 'topleft' : r ? 'topright' : 'top';
                  if (b) return l ? 'bottomleft' : r ? 'bottomright' : 'bottom';
                  if (l) return 'left';
                  if (r) return 'right';
                  return '';
                };

                if (detached) {
                  // The browser control covers the whole window, so mouse input never reaches it
                  // and neither dragging nor the resize borders work. The page reports those
                  // gestures instead and the host performs them. The drag only starts once the
                  // pointer actually moves, so a plain click still reaches play/pause.
                  let pending = null;

                  document.addEventListener('mousedown', e => {
                    if (e.button !== 0) return;

                    const edge = edgeAt(e);
                    if (edge) {
                      e.preventDefault();
                      try { window.chrome.webview.postMessage(JSON.stringify({ action: 'resize', edge })); } catch {}
                      return;
                    }

                    if (isControl(e.target)) return;

                    e.preventDefault();
                    pending = { x: e.clientX, y: e.clientY };
                  });

                  document.addEventListener('mousemove', e => {
                    if (!pending) return;
                    if (Math.abs(e.clientX - pending.x) < 3 && Math.abs(e.clientY - pending.y) < 3) return;
                    pending = null;
                    try { window.chrome.webview.postMessage(JSON.stringify({ action: 'drag' })); } catch {}
                  });

                  document.addEventListener('mouseup', e => {
                    if (!pending || isControl(e.target)) { pending = null; return; }
                    pending = null;
                    p.paused ? p.play().catch(() => {}) : p.pause();
                  });
                } else {
                  document.addEventListener('click', e => {
                    if (isControl(e.target)) return;
                    p.paused ? p.play().catch(() => {}) : p.pause();
                  });
                }

                if (detached) {
                  // Show the matching double-headed arrow so the edges read as grabbable, the same
                  // as any window border. This has to go on the wrap: it is what the idle-hide
                  // rules set "cursor: none" on, and a cursor on the body would never be seen
                  // through it. Setting it inline wins over those rules; clearing it hands them
                  // back.
                  const CURSORS = {
                    topleft: 'nwse-resize', bottomright: 'nwse-resize',
                    topright: 'nesw-resize', bottomleft: 'nesw-resize',
                    left: 'ew-resize', right: 'ew-resize',
                    top: 'ns-resize', bottom: 'ns-resize',
                  };

                  document.addEventListener('mousemove', e => {
                    const edge = edgeAt(e);
                    wrap.style.cursor = CURSORS[edge] || '';
                    // The buttons and the scrubber set their own cursor, which would otherwise win
                    // over the border they sit on top of along the bottom edge.
                    wrap.classList.toggle('on-edge', !!edge);
                  });

                  // Tell the host what size the page has actually reached, every time it changes and
                  // once per frame while it is changing. The host cannot otherwise know whether the
                  // page has caught up with the window, and if it lets the window run ahead the
                  // window's own background shows through along the edges being dragged.
                  // The host sends the window's size the instant it changes, ahead of the browser
                  // noticing. Everything anchored to the wrap therefore lands in the right place on
                  // the first frame instead of being dragged there over the next few.
                  window.chrome.webview.addEventListener('message', e => {
                    const size = e.data && e.data.fit;
                    if (!size) return;
                    const root = document.documentElement.style;
                    root.setProperty('--host-w', size.w + 'px');
                    root.setProperty('--host-h', size.h + 'px');
                  });

                  let settled = 0;
                  const reportSize = () => {
                    const w = Math.round(window.innerWidth * window.devicePixelRatio);
                    if (w === settled) return;
                    settled = w;
                    try { window.chrome.webview.postMessage(JSON.stringify({ action: 'painted', w })); } catch {}
                  };
                  window.addEventListener('resize', () => requestAnimationFrame(reportSize));
                  requestAnimationFrame(reportSize);

                  // Tell the host the real aspect so the window can keep it while resizing.
                  p.addEventListener('loadedmetadata', () => {
                    if (!p.videoWidth) return;
                    try {
                      window.chrome.webview.postMessage(JSON.stringify({
                        action: 'ratio', w: p.videoWidth, h: p.videoHeight,
                      }));
                    } catch {}
                  }, { once: true });
                }

                // Resume where the preview left off when moving into the mini window.
                const startAt = {{startTime.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}};
                if (startAt > 0) {
                  p.addEventListener('loadedmetadata', () => { p.currentTime = startAt; }, { once: true });
                  if (detached) p.addEventListener('canplay', () => p.play().catch(() => {}), { once: true });
                }

                document.getElementById('dl').addEventListener('click', () => {
                  closeMenu();
                  const a = document.createElement('a');
                  a.href = p.currentSrc;
                  a.download = '';
                  a.click();
                });

                const fs = document.getElementById('fs');
                if (fs) fs.addEventListener('click', () => {
                  // Fullscreen the whole player, not the video element. Fullscreening the video
                  // alone leaves these controls behind and falls back to Chromium's.
                  if (document.fullscreenElement) document.exitFullscreen();
                  else wrap.requestFullscreen();
                });

                document.addEventListener('keydown', e => {
                  if (e.code === 'Escape') closeMenu();
                  if (e.code === 'Space') { e.preventDefault(); p.paused ? p.play() : p.pause(); }
                });

                p.addEventListener('error', () => {
                  document.querySelector('.wrap').innerHTML =
                    '<div class="missing">This format cannot be played here. Use Open to play it in your default app.</div>';
                });
              </script>
            </body>
            </html>
            """;
    }

    private static string MimeFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".webm" => "video/webm",
        ".ogv" => "video/ogg",
        ".mov" => "video/quicktime",
        ".mkv" => "video/x-matroska",
        ".avi" => "video/x-msvideo",
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".wav" => "audio/wav",
        ".ogg" or ".oga" => "audio/ogg",
        ".flac" => "audio/flac",
        ".aac" => "audio/aac",
        ".wma" => "audio/x-ms-wma",
        _ => "application/octet-stream",
    };
}
