'use strict';
const message = document.querySelector('#message');
const orderId = new URLSearchParams(location.search).get('order');
const form = document.querySelector('#checkout');
if (form) form.addEventListener('submit', async event => {
  event.preventDefault();
  const button = document.querySelector('#buy');
  button.disabled = true;
  message.textContent = 'Opening Stripe…';
  const tier = new FormData(form).get('tier');
  let previous;
  try { previous = JSON.parse(sessionStorage.getItem('checkout') || 'null'); } catch { /* storage unavailable */ }
  try {
    const response = await fetch('/api/checkout', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ tier, orderId: previous?.tier === tier ? previous.orderId : null })
    });
    const data = await response.json();
    if (data.orderId) { const resume = document.querySelector('#resume'); resume.href = '/success.html?order=' + encodeURIComponent(data.orderId); resume.hidden = false; try { sessionStorage.setItem('checkout', JSON.stringify({ tier, orderId: data.orderId })); } catch { /* cookie remains authoritative */ } }
    if (!response.ok) throw new Error(data.error || 'Unable to open checkout. Please retry shortly.');
    location.assign(data.url);
  } catch (error) { message.textContent = error.message; button.disabled = false; }
});
const cancelLink = document.querySelector('#check-order');
if (cancelLink && orderId) cancelLink.href = '/success.html?order=' + encodeURIComponent(orderId);
if (document.querySelector('#result')) {
  let attempts = 0;
  let timer;
  const refresh = document.querySelector('#refresh');
  const check = async () => {
    clearTimeout(timer);
    refresh.hidden = true;
    try {
      if (!orderId) throw new Error('Missing order reference. Open the return link from your checkout.');
      const response = await fetch('/api/orders/' + encodeURIComponent(orderId));
      if (response.status === 404) throw new Error('Order unavailable in this browser. Use the browser you paid with; its private access cookie is required.');
      if (!response.ok) throw new Error('Could not check your order. Please try again.');
      const order = await response.json();
      if (order.status === 'paid' && order.ticket) {
        document.querySelector('#heading').textContent = 'Your ticket is ready.';
        message.textContent = 'Payment confirmed. Save this ticket now.';
        document.querySelector('#tier').textContent = order.tier.toUpperCase() + ' admission';
        document.querySelector('#code').textContent = order.ticket;
        document.querySelector('#ticket').hidden = false;
        try { sessionStorage.removeItem('checkout'); } catch { /* optional */ }
        return;
      }
      if (order.status === 'failed' || order.status === 'expired') {
        document.querySelector('#heading').textContent = order.status === 'failed' ? 'Payment failed.' : 'Checkout expired.';
        message.textContent = 'No ticket has been issued. You can start a new checkout.';
        try { sessionStorage.removeItem('checkout'); } catch { /* optional */ }
        refresh.hidden = false;
        return;
      }
      document.querySelector('#heading').textContent = 'Payment confirmation pending.';
      message.textContent = 'Some payments take longer. Keep this page or bookmark its address in this browser. Do not pay again while your payment is processing.';
      if (++attempts < 30) timer = setTimeout(check, 4000);
      else refresh.hidden = false;
    } catch (error) { message.textContent = error.message; refresh.hidden = false; }
  };
  refresh.addEventListener('click', () => { attempts = 0; check(); });
  document.querySelector('#print').addEventListener('click', () => window.print());
  check();
}
