/*!
 * Veil bot-verification widget — self-hosted, no third-party service.
 *
 * Embed:
 *   <div class="veil-widget" data-sitekey="SITEKEY"></div>
 *   <script src="/_veil/widget.js" async></script>
 *
 * On success it injects <input type="hidden" name="veil-response" value="TOKEN">
 * into the surrounding <form>; the origin backend confirms the token via
 *   POST /_veil/siteverify  { "secret": "...", "response": "TOKEN" }
 * The hard bot cost is the Proof-of-Work (solved in a Web Worker); an optional
 * behavioural signal (Tier 2) is gathered from the pointer interaction.
 */
(function () {
  "use strict";

  var CHALLENGE_URL = "/_veil/widget/challenge";
  var VERIFY_URL = "/_veil/widget/verify";
  var FIELD_NAME = "veil-response";
  var MIN_EVENTS = 6;

  var STR = {
    idle: "İnsan olduğumu doğrula",
    checking: "Doğrulanıyor…",
    done: "Doğrulandı",
    retry: "Doğrulanamadı — tıklayın",
    brand: "Veil",
  };

  // ── Behavioural telemetry (shared across widgets on the page) ────────
  // Captured passively from pointer/touch movement; scored server-side only
  // for Tier 2. A scripted client that never moves produces no events.
  function nowT() {
    return window.performance && performance.now ? performance.now() : Date.now();
  }
  var b = {
    count: 0, pathLength: 0, first: null, last: null, lastX: null, lastY: null,
    startX: 0, startY: 0, endX: 0, endY: 0, lastT: null, dts: [], startedAt: nowT(),
  };
  function onMove(x, y) {
    var t = nowT();
    if (b.first === null) { b.first = t; b.startX = x; b.startY = y; }
    if (b.lastX !== null) {
      var dx = x - b.lastX, dy = y - b.lastY;
      b.pathLength += Math.sqrt(dx * dx + dy * dy);
    }
    if (b.lastT !== null) { b.dts.push(t - b.lastT); }
    b.lastT = t; b.lastX = x; b.lastY = y; b.endX = x; b.endY = y; b.last = t; b.count++;
  }
  window.addEventListener("pointermove", function (e) { onMove(e.clientX, e.clientY); }, { passive: true });
  window.addEventListener("touchmove", function (e) {
    if (e.touches && e.touches[0]) { onMove(e.touches[0].clientX, e.touches[0].clientY); }
  }, { passive: true });

  function snapshot() {
    var jitter = 0;
    if (b.dts.length > 1) {
      var sum = 0, i;
      for (i = 0; i < b.dts.length; i++) { sum += b.dts[i]; }
      var mean = sum / b.dts.length, varsum = 0;
      for (i = 0; i < b.dts.length; i++) { var d = b.dts[i] - mean; varsum += d * d; }
      jitter = Math.sqrt(varsum / b.dts.length);
    }
    var sdx = b.endX - b.startX, sdy = b.endY - b.startY;
    return {
      event_count: b.count,
      path_length: b.pathLength,
      straight_line: Math.sqrt(sdx * sdx + sdy * sdy),
      duration_ms: Math.round((b.last || b.first || 0) - (b.first || 0)),
      time_to_first_ms: Math.round((b.first || b.startedAt) - b.startedAt),
      timing_jitter_ms: jitter,
    };
  }

  // ── Networking ──────────────────────────────────────────────────────
  function post(url, body, cb) {
    var xhr = new XMLHttpRequest();
    xhr.open("POST", url, true);
    xhr.setRequestHeader("Content-Type", "application/json");
    xhr.onload = function () {
      if (xhr.status < 200 || xhr.status >= 300) { cb("http_" + xhr.status); return; }
      try { cb(null, JSON.parse(xhr.responseText)); } catch (e) { cb("bad_json"); }
    };
    xhr.onerror = function () { cb("network"); };
    xhr.send(JSON.stringify(body));
  }

  // ── PoW solver (inline Web Worker; same algorithm as the edge) ───────
  function solvePow(nonceHex, difficulty, done) {
    var workerCode = function () {
      self.onmessage = async function (e) {
        var nonce = e.data.nonce, difficulty = e.data.difficulty, batchSize = 50000;
        var nonceBytes = new Uint8Array(nonce.length / 2);
        for (var i = 0; i < nonce.length; i += 2) { nonceBytes[i / 2] = parseInt(nonce.substr(i, 2), 16); }
        var counter = 0, found = false;
        while (!found) {
          for (var j = 0; j < batchSize; j++) {
            var buf = new ArrayBuffer(nonceBytes.length + 8);
            var view = new Uint8Array(buf);
            view.set(nonceBytes, 0);
            var c = counter;
            for (var k = 7; k >= 0; k--) { view[nonceBytes.length + k] = c & 0xff; c = Math.floor(c / 256); }
            var hash = await crypto.subtle.digest("SHA-256", buf);
            var hashBytes = new Uint8Array(hash);
            var zeros = 0;
            for (var z = 0; z < hashBytes.length; z++) {
              if (hashBytes[z] === 0) { zeros += 8; }
              else { var byte = hashBytes[z]; while ((byte & 0x80) === 0 && zeros < 256) { zeros++; byte <<= 1; } break; }
            }
            if (zeros >= difficulty) {
              var hex = "", cv = counter;
              for (var h = 0; h < 16; h++) { hex = "0123456789abcdef"[cv & 0xf] + hex; cv = Math.floor(cv / 16); }
              self.postMessage({ counter: hex });
              found = true; break;
            }
            counter++;
          }
        }
      };
    };
    var blob = new Blob(["(" + workerCode.toString() + ")()"], { type: "application/javascript" });
    var worker = new Worker(URL.createObjectURL(blob));
    worker.onmessage = function (e) { worker.terminate(); done(e.data.counter); };
    worker.onerror = function () { worker.terminate(); done(null); };
    worker.postMessage({ nonce: nonceHex, difficulty: difficulty });
  }

  // ── Challenge → verify → token ──────────────────────────────────────
  function fetchChallenge(sitekey, cb) {
    post(CHALLENGE_URL, { sitekey: sitekey }, function (err, res) {
      if (err || !res || !res.nonce) { cb("challenge_failed"); return; }
      cb(null, res);
    });
  }
  function solveAndVerify(sitekey, res, cb) {
    solvePow(res.nonce, res.difficulty, function (counterHex) {
      if (!counterHex) { cb("solve_failed"); return; }
      var payload = { sitekey: sitekey, nonce: res.nonce, counter: counterHex };
      if (res.tier === 2) { payload.behavior = snapshot(); }
      post(VERIFY_URL, payload, function (err2, res2) {
        if (err2 || !res2 || !res2.token) { cb("verify_failed"); return; }
        cb(null, res2.token);
      });
    });
  }

  // ── Rendering ───────────────────────────────────────────────────────
  function injectStyles() {
    if (document.getElementById("veil-widget-styles")) { return; }
    var css =
      ".veil-widget{--vw-bg:#fff;--vw-fg:#18181b;--vw-muted:#71717a;--vw-border:rgba(0,0,0,.12);--vw-accent:#3b82f6;--vw-success:#22c55e;" +
      "font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;display:inline-block}" +
      ".veil-widget.veil-widget--dark{--vw-bg:#18181b;--vw-fg:#f4f4f5;--vw-muted:#a1a1aa;--vw-border:rgba(255,255,255,.12)}" +
      "@media (prefers-color-scheme:dark){.veil-widget.veil-widget--auto{--vw-bg:#18181b;--vw-fg:#f4f4f5;--vw-muted:#a1a1aa;--vw-border:rgba(255,255,255,.12)}}" +
      ".veil-box{display:flex;align-items:center;gap:.7rem;min-width:280px;padding:.75rem .9rem;background:var(--vw-bg);color:var(--vw-fg);" +
      "border:1px solid var(--vw-border);border-radius:10px;font-size:.9rem;box-shadow:0 1px 2px rgba(0,0,0,.04)}" +
      ".veil-check{flex:0 0 auto;width:22px;height:22px;border-radius:6px;border:2px solid var(--vw-muted);position:relative;cursor:pointer;background:none;padding:0;transition:border-color .2s,background .2s}" +
      ".veil-box.veil-checking .veil-check{border-color:var(--vw-accent);border-right-color:transparent;border-radius:50%;animation:veil-spin .7s linear infinite;cursor:default}" +
      ".veil-box.veil-done .veil-check{border-color:var(--vw-success);background:var(--vw-success);cursor:default}" +
      ".veil-box.veil-done .veil-check::after{content:'';position:absolute;left:6px;top:2px;width:5px;height:10px;border:solid var(--vw-bg);border-width:0 2px 2px 0;transform:rotate(45deg)}" +
      ".veil-box.veil-error .veil-check{border-color:#ef4444}" +
      ".veil-label{flex:1 1 auto;color:var(--vw-fg)}.veil-box.veil-done .veil-label{color:var(--vw-success)}.veil-box.veil-error .veil-label{color:#ef4444}" +
      ".veil-brand{flex:0 0 auto;display:flex;align-items:center;gap:.3rem;color:var(--vw-muted);font-size:.72rem;font-weight:600;letter-spacing:.02em}" +
      ".veil-brand svg{width:13px;height:13px}" +
      "@keyframes veil-spin{to{transform:rotate(360deg)}}";
    var style = document.createElement("style");
    style.id = "veil-widget-styles";
    style.textContent = css;
    document.head.appendChild(style);
  }

  var LOGO =
    '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">' +
    '<path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"></path></svg>';

  function setState(box, state) {
    box.className = "veil-box" + (state ? " veil-" + state : "");
  }

  function injectToken(widgetEl, token) {
    var form = widgetEl.closest ? widgetEl.closest("form") : null;
    var host = form || widgetEl;
    var input = host.querySelector('input[name="' + FIELD_NAME + '"]');
    if (!input) {
      input = document.createElement("input");
      input.type = "hidden";
      input.name = FIELD_NAME;
      host.appendChild(input);
    }
    input.value = token;
  }

  function setupWidget(el) {
    var sitekey = el.getAttribute("data-sitekey") || "";
    var theme = el.getAttribute("data-theme") || "auto";
    el.classList.add("veil-widget--" + (theme === "light" || theme === "dark" ? theme : "auto"));
    el.innerHTML =
      '<div class="veil-box"><button type="button" class="veil-check" aria-label="' + STR.idle + '"></button>' +
      '<span class="veil-label">' + STR.checking + '</span>' +
      '<span class="veil-brand">' + LOGO + STR.brand + "</span></div>";

    var box = el.querySelector(".veil-box");
    var label = el.querySelector(".veil-label");
    var check = el.querySelector(".veil-check");
    var state = "checking"; // auto-start (see below)
    var pending = null;     // a Tier 2 challenge awaiting an explicit click

    function succeed(token) {
      state = "done";
      setState(box, "done");
      label.textContent = STR.done;
      injectToken(el, token);
      var cb = el.getAttribute("data-callback");
      if (cb && typeof window[cb] === "function") {
        try { window[cb](token); } catch (e) { /* callback errors are the host's */ }
      }
    }

    // Reveal the clickable checkbox as a fallback (manual retry).
    function offerManual(labelText) {
      state = "idle";
      setState(box, "");
      label.textContent = labelText;
    }

    function verify(res, sitekey_) {
      solveAndVerify(sitekey_, res, function (err, token) {
        if (err || !token) { offerManual(STR.retry); return; }
        succeed(token);
      });
    }

    // `fromClick` marks an explicit human interaction: Tier 2 only proceeds then,
    // since it needs the pointer telemetry a click naturally supplies.
    function attempt(fromClick) {
      if (state === "done") { return; }
      state = "checking";
      setState(box, "checking");
      label.textContent = STR.checking;

      if (pending && fromClick) { verify(pending, sitekey); return; }

      fetchChallenge(sitekey, function (err, res) {
        if (err || !res) { offerManual(STR.retry); return; }
        if (res.tier === 2 && !fromClick) {
          // High-risk: don't auto-verify (no interaction yet) — ask for a click.
          pending = res;
          offerManual(STR.idle);
          return;
        }
        verify(res, sitekey);
      });
    }

    check.addEventListener("click", function () {
      if (state === "checking" || state === "done") { return; }
      attempt(true);
    });

    // Auto-attempt on render: Tier 1 passes invisibly (frictionless); Tier 2 or
    // any failure falls back to the visible checkbox above.
    attempt(false);
  }

  function init() {
    injectStyles();
    var widgets = document.querySelectorAll(".veil-widget");
    for (var i = 0; i < widgets.length; i++) { setupWidget(widgets[i]); }
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init);
  } else {
    init();
  }
})();
