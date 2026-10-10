/* Landing dạng từng phần một:
   - Vào trang ở "Tổng quan"; mỗi 5 giây tự chuyển sang phần kế tiếp (dừng ở phần cuối)
   - Chỉ hiện 1 phần, các phần khác ẩn
   - Máy tính: lăn chuột / phím mũi tên / PageUp, PageDown; điện thoại: vuốt lên/xuống */
(function () {
  "use strict";
  var INTERVAL = 5000;      // 5 giây
  var LOCK_MS = 750;        // khoá chuyển trang trong lúc đang trượt
  var WHEEL_MIN = 12;       // ngưỡng lăn chuột
  var SWIPE_MIN = 50;       // ngưỡng vuốt (px)

  function init() {
    var main = document.querySelector("main");
    var sections = main ? Array.from(main.querySelectorAll(":scope > section[id]")) : [];
    if (sections.length < 2) return;

    // ---- Bọc từng section thành 1 slide ----
    var slides = sections.map(function (section) {
      var wrap = document.createElement("div");
      wrap.className = "ls-slide";
      main.insertBefore(wrap, section);
      wrap.appendChild(section);
      return wrap;
    });
    var footer = document.querySelector(".landing-footer");
    if (footer) slides[slides.length - 1].appendChild(footer);

    var ids = sections.map(function (s) { return s.id; });
    var header = document.querySelector(".landing-header");
    var progress = document.createElement("div");
    progress.className = "ls-progress";
    progress.style.setProperty("--ls-interval", INTERVAL + "ms");
    if (header) header.appendChild(progress);

    var dots = document.createElement("nav");
    dots.className = "ls-dots";
    dots.setAttribute("aria-label", "Chuyển phần");
    ids.forEach(function (id, i) {
      var b = document.createElement("button");
      b.type = "button";
      b.setAttribute("aria-label", "Phần " + (i + 1));
      b.addEventListener("click", function () { go(i, true); });
      dots.appendChild(b);
    });
    document.body.appendChild(dots);

    var navLinks = Array.from(document.querySelectorAll('.landing-nav a[href^="#"], .landing-section-rail a[href^="#"]'));
    var current = -1;
    var locked = false;
    var lockTimer = null;
    var autoTimer = null;

    function render() {
      slides.forEach(function (slide, i) {
        slide.classList.toggle("is-current", i === current);
        slide.classList.toggle("is-before", i < current);
        slide.setAttribute("aria-hidden", i === current ? "false" : "true");
        if (i === current) { slide.removeAttribute("inert"); } else { slide.setAttribute("inert", ""); }
      });
      navLinks.forEach(function (a) {
        a.classList.toggle("active", a.getAttribute("href") === "#" + ids[current]);
      });
      Array.from(dots.children).forEach(function (b, i) { b.classList.toggle("active", i === current); });
    }

    function stopAuto() {
      window.clearTimeout(autoTimer);
      autoTimer = null;
      progress.classList.remove("run");
    }

    function startAuto() {
      stopAuto();
      if (document.hidden || current >= slides.length - 1) return;
      void progress.offsetWidth;
      progress.classList.add("run");
      autoTimer = window.setTimeout(function () { go(current + 1, false); }, INTERVAL);
    }

    function go(index, manual) {
      index = Math.max(0, Math.min(slides.length - 1, index));
      if (index === current) { if (manual) startAuto(); return; }
      current = index;
      slides[current].scrollTop = 0;
      render();
      locked = true;
      window.clearTimeout(lockTimer);
      lockTimer = window.setTimeout(function () { locked = false; }, LOCK_MS);
      startAuto();
    }

    function step(dir) {
      if (locked) return;
      var next = current + dir;
      if (next < 0 || next >= slides.length) return;
      go(next, true);
    }

    // slide hiện tại còn nội dung để cuộn theo hướng dir không?
    function canScroll(dir) {
      var el = slides[current];
      if (el.scrollHeight <= el.clientHeight + 2) return false;
      return dir > 0 ? el.scrollTop + el.clientHeight < el.scrollHeight - 2 : el.scrollTop > 2;
    }

    // ---- Chuột ----
    var wheelAcc = 0, wheelReset = null;
    window.addEventListener("wheel", function (e) {
      if (e.ctrlKey || e.metaKey) return;            // Ctrl + lăn = zoom bản đồ
      if (e.defaultPrevented) return;                // bản đồ đang zoom đã xử lý
      if (e.target.closest && e.target.closest("input, textarea")) return;
      var dir = e.deltaY > 0 ? 1 : e.deltaY < 0 ? -1 : 0;
      if (!dir) return;
      if (canScroll(dir)) return;                    // để slide dài tự cuộn
      e.preventDefault();
      if (locked) { wheelAcc = 0; return; }
      wheelAcc += e.deltaY;
      window.clearTimeout(wheelReset);
      wheelReset = window.setTimeout(function () { wheelAcc = 0; }, 160);
      if (Math.abs(wheelAcc) >= WHEEL_MIN) { wheelAcc = 0; step(dir); }
    }, { passive: false });

    // ---- Bàn phím ----
    window.addEventListener("keydown", function (e) {
      if (e.target.closest && e.target.closest("input, textarea, select, [contenteditable]")) return;
      if (e.altKey || e.ctrlKey || e.metaKey) return;
      var dir = 0;
      if (e.key === "ArrowDown" || e.key === "PageDown") dir = 1;
      else if (e.key === "ArrowUp" || e.key === "PageUp") dir = -1;
      else if (e.key === "Home") { e.preventDefault(); go(0, true); return; }
      else if (e.key === "End") { e.preventDefault(); go(slides.length - 1, true); return; }
      if (!dir || canScroll(dir)) return;
      e.preventDefault();
      step(dir);
    });

    // ---- Cảm ứng: vuốt ----
    var t0 = null;
    document.addEventListener("touchstart", function (e) {
      if (e.touches.length !== 1) { t0 = null; return; }
      var t = e.touches[0];
      var svg = e.target.closest && e.target.closest("#landingVietnamMap svg");
      t0 = {
        x: t.clientX, y: t.clientY,
        mapZoomed: !!(svg && svg.classList.contains("is-zoomed")),
        edgeUp: !canScroll(1), edgeDown: !canScroll(-1)
      };
      stopAuto();
    }, { passive: true });
    document.addEventListener("touchmove", function (e) { if (e.touches.length > 1) t0 = null; }, { passive: true });
    document.addEventListener("touchend", function (e) {
      var start = t0; t0 = null;
      if (!start) { startAuto(); return; }
      var t = e.changedTouches[0];
      var dx = t.clientX - start.x, dy = t.clientY - start.y;
      if (!start.mapZoomed && Math.abs(dy) >= SWIPE_MIN && Math.abs(dy) > Math.abs(dx) * 1.2) {
        var dir = dy < 0 ? 1 : -1;   // vuốt lên -> phần tiếp theo
        var atEdgeNow = !canScroll(dir);
        var atEdgeStart = dir > 0 ? start.edgeUp : start.edgeDown;
        if (atEdgeNow && atEdgeStart) { step(dir); return; }
      }
      startAuto();
    }, { passive: true });
    document.addEventListener("touchcancel", function () { t0 = null; startAuto(); }, { passive: true });

    // ---- Bấm menu / link #anchor ----
    document.addEventListener("click", function (e) {
      var a = e.target.closest && e.target.closest('a[href^="#"]');
      if (!a) return;
      var id = (a.getAttribute("href") || "").slice(1);
      var i = ids.indexOf(id);
      if (i < 0) return;
      e.preventDefault();
      e.stopImmediatePropagation();   // chặn code cuộn cũ trong landing_page.js
      go(i, true);
    }, true);

    // ---- Tạm dừng khi ẩn tab / đang gõ email ----
    document.addEventListener("visibilitychange", function () { if (document.hidden) stopAuto(); else startAuto(); });
    document.addEventListener("focusin", function (e) { if (e.target.closest && e.target.closest("input, textarea")) stopAuto(); });
    document.addEventListener("focusout", function (e) { if (e.target.closest && e.target.closest("input, textarea")) startAuto(); });

    document.body.classList.add("landing-slides");
    go(0, false);
    locked = false;
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", init);
  else init();
})();
