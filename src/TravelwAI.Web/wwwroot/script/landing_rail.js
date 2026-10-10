/* Điều hướng nửa vòng tròn (Landing) - không animation
   - Chữ của mục hiện tại nằm giữa cung tròn (card chữ nằm trong chỗ ngắt của cung)
   - Hiện tối đa 3 mục: mục hiện tại + mục kế bên (đầu/cuối danh sách thì lấy thêm mục thứ 2 cùng phía)
   - Mục trước/sau hiển thị dạng chấm trên cung
   - Cung trắng bị ngắt ở vị trí từng mục: JS gán góc (--tw-bN-c) và nửa độ rộng (--tw-bN-h) cho CSS mask
   - Trạng thái mục đang xem lấy từ class .active do landing_page.js gắn */
(function () {
  "use strict";
  const rail = document.querySelector(".landing-section-rail");
  if (!rail) return;
  const links = Array.from(rail.querySelectorAll("a[href^='#']"));
  const n = links.length;
  if (n < 2) return;

  const STEP = 38;       // độ lệch góc giữa 2 mục liền kề
  const BREAK_CARD_PX = 17; // nửa độ rộng chỗ ngắt cho card chữ (px theo cung)
  const BREAK_DOT_PX = 10;  // nửa độ rộng chỗ ngắt cho chấm (px theo cung)
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

    let slot = 0;
    for (let k = 0; k < 3; k++) {
      rail.style.setProperty("--tw-b" + k + "-c", "0deg");
      rail.style.setProperty("--tw-b" + k + "-h", "0deg");
    }

    links.forEach(function (link, i) {
      const d = i - active;
      let visible = Math.abs(d) <= 1;
      if (active === 0 && d === 2) visible = true;
      if (active === n - 1 && d === -2) visible = true;

      const theta = -d * STEP; // mục kế tiếp nằm phía dưới, mục trước nằm phía trên
      link.style.transform =
        "rotate(" + theta + "deg) translateX(" + (-R) + "px) rotate(" + (-theta) + "deg) translate(-50%, -50%)";

      if (visible && slot < 3) {
        // góc conic: 0deg = hướng lên, thuận chiều kim đồng hồ; hướng sang trái = 270deg, xuống dưới = 180deg
        const center = 270 - d * STEP;
        const half = ((d === 0 ? BREAK_CARD_PX : BREAK_DOT_PX) / R) * 180 / Math.PI;
        rail.style.setProperty("--tw-b" + slot + "-c", center.toFixed(2) + "deg");
        rail.style.setProperty("--tw-b" + slot + "-h", half.toFixed(2) + "deg");
        slot++;
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
