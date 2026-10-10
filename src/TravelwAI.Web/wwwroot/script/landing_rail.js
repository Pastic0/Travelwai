/* Điều hướng nửa vòng tròn (Landing) - không animation
   - Chữ của mục hiện tại nằm giữa cung tròn (card chữ nằm trên dải cung liền)
   - Hiện tối đa 3 mục: mục hiện tại + mục kế bên (đầu/cuối danh sách thì lấy thêm mục thứ 2 cùng phía)
   - Mục trước/sau hiển thị dạng chấm trên cung
   - Trạng thái mục đang xem lấy từ class .active do landing_page.js gắn */
(function () {
  "use strict";
  const rail = document.querySelector(".landing-section-rail");
  if (!rail) return;
  const links = Array.from(rail.querySelectorAll("a[href^='#']"));
  const n = links.length;
  if (n < 2) return;

  const STEP = 38;       // độ lệch góc giữa 2 mục liền kề
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
    const key = active + ":" + R;
    if (key === lastKey) return;
    lastKey = key;

    links.forEach(function (link, i) {
      const d = i - active;
      let visible = Math.abs(d) <= 1;
      if (active === 0 && d === 2) visible = true;
      if (active === n - 1 && d === -2) visible = true;

      const theta = -d * STEP; // mục kế tiếp nằm phía dưới, mục trước nằm phía trên
      link.style.transform =
        "rotate(" + theta + "deg) translateX(" + (-R) + "px) rotate(" + (-theta) + "deg) translate(-50%, -50%)";

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
