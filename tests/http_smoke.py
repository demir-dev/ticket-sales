"""HTTP boundary tests; no real Stripe keys, network calls or payments."""
import hashlib
import hmac
import json
import os
from pathlib import Path
import sqlite3
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

root = Path(__file__).resolve().parents[1]
app_directory = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else root / 'src'
app_dll = app_directory / 'TicketSales.dll' if len(sys.argv) > 1 else root / 'src/bin/Release/net10.0/TicketSales.dll'
with socket.socket() as sock:
    sock.bind(('127.0.0.1', 0))
    port = sock.getsockname()[1]
base = f'http://127.0.0.1:{port}'
with tempfile.TemporaryDirectory() as temp:
    env = dict(os.environ, ASPNETCORE_URLS=base, PUBLIC_URL=base,
               STRIPE_SECRET_KEY='sk_test_smoke_placeholder', STRIPE_WEBHOOK_SECRET='whsec_smoke',
               STRIPE_PRICE_STANDARD='price_standard', STRIPE_PRICE_PREMIUM='price_premium',
               STRIPE_PRICE_VIP='price_vip', STRIPE_LIVE_MODE='false', STRIPE_AUTOMATIC_TAX='false',
               DATABASE_PATH=str(Path(temp) / 'test.db'))
    log = open(Path(temp) / 'server.log', 'w+')
    process = subprocess.Popen(['dotnet', str(app_dll)],
                               cwd=app_directory, env=env, stdout=log, stderr=log)
    def request(path, body=None, headers=None):
        req = urllib.request.Request(base + path, data=body, headers=headers or {})
        try:
            with urllib.request.urlopen(req, timeout=5) as response:
                return response.status, response.read(), response.headers
        except urllib.error.HTTPError as error:
            return error.code, error.read(), error.headers
    try:
        for _ in range(100):
            if process.poll() is not None:
                log.seek(0)
                raise RuntimeError(log.read())
            try:
                if request('/health')[0] == 200:
                    break
            except OSError:
                time.sleep(.1)
        else:
            raise RuntimeError('Server failed to start')
        code, body, headers = request('/')
        assert code == 200 and b'Choose your ticket' in body
        assert "frame-ancestors 'none'" in headers['Content-Security-Policy']
        assert headers['Referrer-Policy'] == 'no-referrer'
        assert request('/success.html?order=forged')[0] == 200
        assert request('/api/orders/forged')[0] == 404
        token = 'a' * 64
        with sqlite3.connect(str(Path(temp) / 'test.db')) as db:
            db.execute("INSERT INTO orders(id,access_hash,tier,price_id,status) VALUES(?,?,?,?,?)",
                       ('test_order', hashlib.sha256(token.encode()).hexdigest(), 'standard', 'price_standard', 'paid'))
            db.execute("INSERT INTO tickets(order_id,code) VALUES(?,?)", ('test_order', 'test_private_code'))
        assert request('/api/orders/test_order')[0] == 404
        assert request('/api/orders/test_order', headers={'Cookie': 'order_test_order=wrong'})[0] == 404
        code, body, _ = request('/api/orders/test_order', headers={'Cookie': 'order_test_order=' + token})
        assert code == 200 and json.loads(body)['ticket'] == 'test_private_code'
        assert b'accessHash' not in body and b'priceId' not in body
        data = json.dumps({'tier': 'standard'}).encode()
        assert request('/api/checkout', data, {'Content-Type': 'application/json', 'Origin': 'https://evil.example'})[0] == 403
        assert request('/api/checkout', b'{"tier":"price_attacker"}', {'Content-Type': 'application/json', 'Origin': base})[0] == 400
        assert request('/stripe/webhook', b'{}')[0] == 400
        event = json.dumps({'id': 'evt_smoke', 'type': 'checkout.session.completed', 'livemode': False,
                            'data': {'object': {'id': 'cs_unrelated', 'client_reference_id': 'unknown'}}}).encode()
        now = str(int(time.time()))
        signature = hmac.new(b'whsec_smoke', now.encode() + b'.' + event, hashlib.sha256).hexdigest()
        headers = {'Stripe-Signature': f't={now},v1={signature}', 'Content-Type': 'application/json'}
        assert request('/stripe/webhook', event, headers)[0] == 200
        assert request('/stripe/webhook', event + b' ', headers)[0] == 400
        assert request('/api/orders/unknown')[0] == 404
        print('PASS: HTTP UI, headers, forged return, origin, price allowlist, signature and unrelated-event checks')
    finally:
        process.terminate()
        process.wait(timeout=10)
        log.close()
