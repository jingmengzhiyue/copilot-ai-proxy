"""Run the built proxy against loopback-only synthetic providers and inspect its logs.

Usage: python tests/runtime-log-probe.py
No project .env or inherited provider credentials are used.
"""
import http.server
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.request


ROOT = Path(__file__).resolve().parents[1]
state = {"fail_first": False, "calls": []}


class Upstream(http.server.BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def do_GET(self):
        time.sleep(3)
        self.respond(200, json.dumps({"data": [{"id": "probe"}, {"id": "failure"}]}))

    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        state["calls"].append((self.path, body.get("model")))
        if body["model"] == "slow":
            time.sleep(30.1)
        if body["model"] == "failure" or (state["fail_first"] and self.path.startswith("/first/")):
            self.respond(503, '{"error":{"message":"SENSITIVE-UPSTREAM-TEXT"}}')
        elif body.get("stream"):
            events = [
                {"choices": [{"delta": {"content": "hello"}, "finish_reason": None}]},
                {"choices": [{"delta": {}, "finish_reason": "stop"}]},
            ]
            self.respond(200, "".join("data: " + json.dumps(e) + "\n\n" for e in events) + "data: [DONE]\n\n", "text/event-stream")
        else:
            self.respond(200, json.dumps({"id": "probe-id", "model": body["model"], "choices": [
                {"message": {"role": "assistant", "content": "hello"}, "finish_reason": "stop"}]}))

    def respond(self, status, body, content_type="application/json"):
        data = body.encode()
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)


def request(port, path, body=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(f"http://127.0.0.1:{port}{path}", data=data,
                                 headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=45) as response:
            return response.status, response.read(), response.headers
    except urllib.error.HTTPError as error:
        return error.code, error.read(), error.headers


def main():
    # `dotnet run` loads this profile before the application's .env file.
    # A port here would silently override the documented local endpoint.
    profiles = json.loads((ROOT / "Properties" / "launchSettings.json").read_text())["profiles"]
    assert all("PROXY_PORT" not in profile.get("environmentVariables", {})
               and "applicationUrl" not in profile for profile in profiles.values())
    upstream = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Upstream)
    threading.Thread(target=upstream.serve_forever, daemon=True).start()
    with socket.socket() as reservation:
        reservation.bind(("127.0.0.1", 0))
        port = reservation.getsockname()[1]
    with tempfile.TemporaryDirectory(prefix="proxy-runtime-probe-") as temporary:
        directory = Path(temporary)
        for suffix in ("dll", "deps.json", "runtimeconfig.json"):
            shutil.copy2(ROOT / "bin" / "Release" / "net10.0" / f"ai-proxy-hub.{suffix}", directory)
        config = directory / "config" / "model-selection"
        config.mkdir(parents=True)
        for provider, priority in (("deepseek", 1), ("openai", 2)):
            (config / f"{provider}.json").write_text(json.dumps({"provider": provider, "models": [
                {"match": model, "enabled": True, "priority": priority, "context_length": 8192}
                for model in ("probe", "failure", "slow")]}))
        environment = {key: value for key, value in os.environ.items()
                       if not key.upper().startswith(("PROVIDER_", "DEEPSEEK_", "PROXY_", "BENCHMARK_", "ASPNETCORE_", "DOTNET_ENVIRONMENT"))}
        base = f"http://127.0.0.1:{upstream.server_port}"
        environment.update(PROXY_PORT=str(port), DEEPSEEK_MODEL="probe",
                           PROVIDER_DEEPSEEK_API_KEY="SYNTHETIC-SECRET-KEY", PROVIDER_DEEPSEEK_BASE_URL=base + "/first",
                           PROVIDER_OPENAI_API_KEY="SYNTHETIC-SECRET-KEY", PROVIDER_OPENAI_BASE_URL=base + "/second")
        # A conflicting file value must not override the process-provided port.
        (directory / ".env").write_text("PROXY_PORT=1\n")
        lines = []
        started = time.monotonic()
        process = subprocess.Popen(["dotnet", str(directory / "ai-proxy-hub.dll")], cwd=directory,
                                   env=environment, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                   text=True, encoding="utf-8", errors="replace")
        reader = threading.Thread(target=lambda: lines.extend(process.stdout), daemon=True)
        reader.start()
        try:
            for _ in range(150):
                try:
                    if request(port, "/health")[0] == 200:
                        break
                except (OSError, urllib.error.URLError):
                    time.sleep(0.05)
            else:
                raise AssertionError("Proxy did not start")
            startup_ms = int((time.monotonic() - started) * 1000)
            assert startup_ms < 3000, f"Startup waited on discovery: {startup_ms}ms"
            ids = []
            # Configured model requests must also work while discovery is pending.
            for path in ("/api/chat", "/v1/chat/completions"):
                chat_started = time.monotonic()
                status, _, headers = request(port, path, {"model": "probe@deepseek", "stream": False, "messages": []})
                assert status == 200
                assert time.monotonic() - chat_started < 2, "Model request waited on discovery"
                ids.append(headers["X-Request-ID"])
            # Wait for both delayed catalogs so fallback tests use a complete candidate chain.
            time.sleep(6.2)
            cases = [
                ("/v1/chat/completions", "probe@deepseek", False, 200),
                ("/v1/chat/completions", "unknown", False, 200),
                ("/v1/chat/completions", "failure@deepseek", False, 503),
                ("/api/chat", "probe@deepseek", True, 200),
                ("/api/chat", "probe@deepseek", False, 200),
                ("/v1/chat/completions", "probe@missing", False, 404),
            ]
            for path, model, stream, expected in cases:
                status, _, headers = request(port, path, {"model": model, "stream": stream,
                                                         "messages": [{"role": "user", "content": "SENSITIVE-PROMPT"}]})
                assert status == expected, (path, model, status)
                ids.append(headers["X-Request-ID"])
            state["fail_first"] = True
            status, _, headers = request(port, "/v1/chat/completions", {"model": "probe", "stream": False, "messages": []})
            assert status == 200
            ids.append(headers["X-Request-ID"])
            state["fail_first"] = False
            status, _, headers = request(port, "/v1/chat/completions", {"model": "slow@deepseek", "stream": False, "messages": []})
            assert status == 200
            ids.append(headers["X-Request-ID"])
            time.sleep(0.2)
        finally:
            process.terminate()
            process.wait(timeout=10)
            reader.join(timeout=2)
            upstream.shutdown()
        logs = "".join(lines)
        summaries = [line.strip() for line in lines if "Proxy.Requests" in line]
        assert len(summaries) == len(ids), (len(summaries), len(ids), logs)
        for request_id in ids:
            assert sum("Id=" + request_id + " " in line for line in summaries) == 1
        assert "Provider=openai" in summaries[-2] and "Attempts=2" in summaries[-2]
        assert "warn:" in summaries[-1] and "Outcome=completed" in summaries[-1] and "Requested=slow@deepseek" in summaries[-1]
        assert all(secret not in logs for secret in ("SYNTHETIC-SECRET", "SENSITIVE-PROMPT", "SENSITIVE-UPSTREAM-TEXT"))
        assert not any(noise in logs for noise in ("Request starting", "Executing endpoint", "Executed endpoint", "Request finished", "[Routing]"))
        print(f"PASS startup_ms={startup_ms} model_requests={len(ids)} summaries={len(summaries)}")
        print("\n".join(summaries))


if __name__ == "__main__":
    main()
