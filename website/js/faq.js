/* Reserves room for the FAQ: its height with the tallest answer open. Measured with each answer
   open in turn (animation off), then the reader's open answer is restored. */
(function () {
  const faq = document.querySelector('.faq');
  if (!faq) return;
  const items = Array.from(faq.querySelectorAll('details'));
  function reserve() {
    const open = items.find((d) => d.open);
    faq.classList.add('measuring');
    faq.style.minHeight = '0px';
    let tallest = 0;
    items.forEach((d) => { d.open = true; tallest = Math.max(tallest, faq.getBoundingClientRect().height); d.open = false; });
    if (open) open.open = true;
    faq.style.minHeight = Math.ceil(tallest) + 'px';
    // Next frame, so restoring the open answer is not animated.
    requestAnimationFrame(() => faq.classList.remove('measuring'));
  }
  reserve();
  window.addEventListener('resize', reserve);
  if (document.fonts) document.fonts.ready.then(reserve);
})();
