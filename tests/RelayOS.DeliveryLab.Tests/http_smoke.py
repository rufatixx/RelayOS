"""Run against an isolated lab process after a Release build. Python stdlib only."""

import http.client
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time


ROOT = Path(__file__).resolve().parents[2]
SAMPLE = ROOT / "samples" / "RelayOS.DeliveryLab"
DLL = SAMPLE / "bin" / "Release" / "net10.0" / "RelayOS.DeliveryLab.dll"
HEADERS = {"Content-Type": "application/json", "X-RelayOS-Lab": "1"}


def request(method, path, body=None, headers=None):
    connection = http.client.HTTPConnection("localhost", 5088, timeout=5)
    try:
        connection.request(method, path, body=body, headers=headers or {})
        response = connection.getresponse()
        return response.status, dict(response.getheaders()), response.read()
    finally:
        connection.close()


def step(state, action, message=None, expected=200):
    status, _, payload = request("POST", "/api/step", json.dumps({
        "sessionId": state["sessionId"], "action": action, "message": message
    }), HEADERS)
    assert status == expected, (action, status, payload)
    return json.loads(payload)


if os.name == "nt":
    raise SystemExit("This optional HTTP smoke test needs POSIX signals. Use Linux/macOS or run the .NET tests on Windows.")

if not DLL.is_file():
    raise SystemExit("Build tests/RelayOS.DeliveryLab.Tests in Release first.")
try:
    request("GET", "/api/state")
except OSError:
    pass
else:
    raise SystemExit("Port 5088 is already in use. Stop the existing lab before testing.")

with tempfile.TemporaryDirectory(prefix="relayos-http-test-") as temporary:
    environment = {**os.environ, "TMPDIR": temporary, "TEMP": temporary, "TMP": temporary}
    with tempfile.TemporaryFile(mode="w+") as log:
        process = subprocess.Popen(["dotnet", str(DLL)], cwd=SAMPLE, env=environment,
                                   stdout=log, stderr=subprocess.STDOUT)
        try:
            for attempt in range(100):
                if process.poll() is not None:
                    log.seek(0)
                    raise AssertionError(f"Lab exited early:\n{log.read()}")
                try:
                    status, headers, body = request("GET", "/api/state")
                    if status == 200:
                        break
                except OSError:
                    pass
                time.sleep(0.1)
            else:
                raise AssertionError("Lab did not become ready within 10 seconds.")

            state = json.loads(body)
            assert state["status"] == "empty"
            assert headers["Cache-Control"] == "no-store"
            assert "frame-ancestors 'none'" in headers["Content-Security-Policy"]
            for asset, marker in [("/", b"Delivery Lab"), ("/app.js", b"textContent"),
                                  ("/style.css", b"@media")]:
                status, _, body = request("GET", asset)
                assert status == 200 and marker in body, asset

            body = json.dumps({"sessionId": state["sessionId"], "action": "offline"})
            assert request("POST", "/api/step", body, {"Content-Type": "application/json"})[0] == 403
            assert request("POST", "/api/step", body, {**HEADERS, "Origin": "https://example.com"})[0] == 403
            assert request("GET", "/api/state", headers={"Host": "evil.example:5088"})[0] == 403
            assert request("POST", "/api/step", "{", HEADERS)[0] == 400
            assert request("POST", "/api/step", "x" * 9000, HEADERS)[0] == 413
            assert request("POST", "/api/step", body, {**HEADERS, "Content-Type": "text/plain"})[0] == 403
            step(state, "unknown", expected=400)
            step(state, "queue", "x" * 1025, expected=400)

            note = '<img src=x onerror="alert(1)"> Field reading: 18.6 °C 🌱'
            queued = step(state, "queue", note)
            assert queued["station"]["pending"] == 1
            step(state, "queue", "second note", expected=409)
            assert step(state, "offline")["deliveryCount"] == 0
            assert step(state, "pickup")["courier"]["pending"] == 1
            assert step(state, "reopen")["courierReopenCount"] == 1
            delivered = step(state, "deliver")
            assert delivered["deliveredMessage"] == note
            assert delivered["deliveryCount"] == 1
            assert step(state, "deliver")["duplicateCount"] == 1
            assert json.loads(request("GET", "/api/state")[2])["deliveredMessage"] == note

            fresh = step(state, "reset")
            step(state, "pickup", expected=409)
            step(fresh, "queue", "Expires before delivery")
            step(fresh, "pickup")
            expired = step(fresh, "advance")
            assert expired["status"] == "expired"
            assert expired["station"]["pending"] == expired["courier"]["pending"] == 0
            assert step(fresh, "deliver")["deliveryCount"] == 0
        finally:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
                raise AssertionError("Lab did not shut down gracefully.")

    queue_root = Path(temporary) / "relayos-delivery-lab"
    assert not queue_root.exists() or not list(queue_root.iterdir()), "Queue cleanup failed"

print("PASS: HTTP assets, loopback host/origin/header/body guards, delivery, reload, stale/reset, expiry, shutdown cleanup")
