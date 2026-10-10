/* Điều hướng nửa vòng tròn (Landing) - không animation
   - Chữ của mục hiện tại nằm giữa cung tròn (cung chừa khoảng hở cho card chữ)
   - Hiện tối đa 3 mục: mục hiện tại + mục kế bên (đầu/cuối danh sách thì lấy thêm mục thứ 2 cùng phía)
   - Nhãn mục trước/sau nằm ngoài cung; JS tính độ lệch --tw-label-dx để card không đè lên dải cung
   - Trạng thái mục đang xem lấy từ class .active do landing_page.js gắn */
(function () {
  "use strict";
  const rail = document.querySelector(".landing-section-rail");
  if (!rail) return;
  const links = Array.from(rail.querySelectorAll("a[href^='#']"));
  const n = links.length;
  if (n < 2) return;

  const STEP = 38;       // độ lệch góc giữa 2 mục liền kề
  const CARD_HALF_H = 15; // nửa chiều cao card nhãn (px)
  const CARD_PAD_X = 12;  // đệm ngang của card quanh chữ (px)
  const MARGIN = 6;       // khoảng cách giữa card nhãn và mép ngoài dải cung (px)
  let lastKey = "";

  links.forEach(function (link) {
    const label = link.querySelector("span");
    if (label && !link.title) link.title = label.textContent.trim();
  });

  function cssNumber(name, fallback) {
    const v = parseFloat(getComputedStyle(rail).getPropertyValue(name));
    return Number.isFinite(v) && v > 0 ? v : fallback;
  }

  function layout() {
    let active = links.findIndex(function (l) { return l.classList.contains("active"); });
    if (active < 0) active = 0;
    const R = cssNumber("--tw-rail-r", 72);
    const W = cssNumber("--tw-rail-w", 22);
    const key = active + ":" + R + ":" + W;
    if (key === lastKey) return;
    lastKey = key;

    const outer = R + W / 2;
    links.forEach(function (link, i) {
      const d = i - active;
      let visible = Math.abs(d) <= 1;
      if (active === 0 && d === 2) visible = true;
      if (active === n - 1 && d === -2) visible = true;

      const theta = -d * STEP; // mục kế tiếp nằm phía dưới, mục trước nằm phía trên
      const rad = d * STEP * Math.PI / 180;
      link.style.transform =
        "rotate(" + theta + "deg) translateX(" + (-R) + "px) rotate(" + (-theta) + "deg) translate(-50%, -50%)";

      if (d === 0) {
        link.style.removeProperty("--tw-label-dx");
      } else {
        const x0 = R * Math.cos(rad);
        const yFar = Math.max(0, Math.abs(R * Math.sin(rad)) - CARD_HALF_H);
        const xEdge = Math.sqrt(Math.max(0, outer * outer - yFar * yFar));
        const dx = Math.max(MARGIN + CARD_PAD_X, xEdge + MARGIN - x0 + CARD_PAD_X);
        link.style.setProperty("--tw-label-dx", dx.toFixed(1) + "px");
      }

      link.classList.toggle("is-visible", visible);
      link.classList.toggle("is-current", d === 0);
      link.setAttribute("aria-hidden", visible ? "false" : "true");
      link.tabIndex = visible ? 0 : -1;
    });
  }

  layout();
  requestAnimationFrame(function () { rail.classList.add("is-ready"); });

  new MutationObserver(layout).observe(rail, {
    subtree: true,
    attributes: true,
    attributeFilter: ["class"]
  });
  window.addEventListener("resize", layout, { passive: true });
})();
