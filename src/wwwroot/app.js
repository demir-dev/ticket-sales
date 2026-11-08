'use strict';
const message = document.querySelector('#message');
const orderId = new URLSearchParams(location.search).get('order');
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
      if (order.status === 'paid' && order.tickets?.length === order.quantity) {
        document.querySelector('#heading').textContent = order.quantity === 1 ? 'Your ticket is ready.' : 'Your tickets are ready.';
        message.textContent = 'Payment confirmed. Save all ' + order.quantity + ' ticket' + (order.quantity === 1 ? '' : 's') + ' now.';
        document.querySelector('#tier').textContent = order.tier.toUpperCase() + ' admission';
        const list = document.querySelector('#codes');
        list.replaceChildren();
        order.tickets.forEach((ticket, index) => {
          const item = document.createElement('li');
          item.textContent = 'Ticket ' + (index + 1);
          const code = document.createElement('code');
          code.textContent = ticket;
          item.append(code);
          list.append(item);
        });
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
