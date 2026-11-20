#!/usr/bin/env python3
"""Тест-канал aerio из Python: то же, что ./bench апстрима и Send-Bench в drive.ps1.

    python Windows/bench.py start [EXE]       тихое окно в тестовом мире (по умолчанию — сборка из bin)
    python Windows/bench.py stop              закрыть его штатно, как крестиком
    python Windows/bench.py reset             закрыть и стереть мир
    python Windows/bench.py DO [KEY=VALUE…]   запрос {"do": DO, KEY: VALUE}; VALUE — JSON, иначе строка
    python Windows/bench.py '{"do": "tabs"}'  запрос как есть

Мир — BENCH_WORLD (по умолчанию shell, как у drive.ps1). Команды — в README,
раздел «Тест-канал». Как модуль, для перенесённых тестов из Tests/:

    import bench
    bench.reset(); bench.start()
    tab = bench.ask({"do": "open", "url": "https://example.com"})["id"]
    bench.ask({"do": "wait", "id": tab})
    bench.stop()
"""
import ctypes
import json
import os
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
EXE = HERE / "App" / "bin" / "x64" / "Debug" / "net10.0-windows10.0.19041.0" / "win-x64" / "aerio.exe"
WORLD = os.environ.get("BENCH_WORLD", "shell")


def _world(world):
    # Как Store.World: строчные буквы, цифры и дефис; пустое и «1» — мир test.
    name = re.sub(r"[^a-z0-9-]", "", (world or "").lower())
    return "test" if name in ("", "1") else name


def folder(world=WORLD):
    return Path(os.environ["LOCALAPPDATA"]) / f"aerio ({_world(world)})"


def ask(request, world=WORLD, patience=5.0):
    """Строка JSON в канал, строка обратно. Канала нет (окно ещё встаёт) или он
    занят — повторяем, пока не выйдет patience секунд."""
    pipe = rf"\\.\pipe\aerio ({_world(world)}).bench"
    deadline = time.monotonic() + patience
    while True:
        try:
            channel = open(pipe, "r+b", buffering=0)
            break
        except OSError as error:
            # 2 — канала нет, 231 — все его экземпляры заняты.
            if getattr(error, "winerror", None) not in (2, 231) or time.monotonic() > deadline:
                raise
            time.sleep(0.1)
    with channel:
        channel.write((json.dumps(request, ensure_ascii=False) + "\n").encode())
        answer = b""
        while not answer.endswith(b"\n"):
            chunk = channel.read(1 << 16)
            if not chunk:
                break
            answer += chunk
    return json.loads(answer or b"{}")


def running(world=WORLD):
    try:
        return ask({"do": "probe"}, world, patience=0)
    except OSError:
        return None


def start(exe=EXE, world=WORLD):
    """Окно, которое не берёт фокус и не встаёт поверх окон человека (BROWSER_QUIET)."""
    env = dict(os.environ, BROWSER_PROBE=_world(world), BROWSER_QUIET="1")
    process = subprocess.Popen([str(exe)], env=env)
    # Первый запуск сразу после сборки бывает долгим (замерено: дольше 20 с,
    # следующий — 0,4 с), поэтому ждём до минуты, но не дольше жизни процесса.
    deadline = time.monotonic() + 60
    while process.poll() is None:
        try:
            ask({"do": "tabs"}, world, patience=1)
            return process
        except OSError:
            if time.monotonic() > deadline:
                raise
    raise RuntimeError(f"aerio exited with code {process.returncode}")


def stop(world=WORLD):
    """WM_CLOSE окну, как Stop-Shell: окно сохраняет сессию и настройки при закрытии.
    Не закрылось за 5 с — снимаем процесс."""
    probe = running(world)
    if not probe:
        return
    pid = probe["pid"]
    kernel = ctypes.windll.kernel32
    process = kernel.OpenProcess(0x00100000, False, pid)   # SYNCHRONIZE
    try:
        subprocess.run(["taskkill", "/PID", str(pid)], capture_output=True)   # без /F — это WM_CLOSE
        if kernel.WaitForSingleObject(process, 5000) != 0:
            subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True)
            kernel.WaitForSingleObject(process, 5000)
    finally:
        kernel.CloseHandle(process)


def reset(world=WORLD):
    """Чистый мир: без вкладок, истории и настроек прошлого прогона."""
    stop(world)
    shutil.rmtree(folder(world), ignore_errors=True)


def _value(text):
    try:
        return json.loads(text)
    except ValueError:
        return text


def main(argv):
    if not argv or argv[0] in ("-h", "--help"):
        print(__doc__.strip())
        return 0
    if argv[0] == "start":
        start(argv[1] if len(argv) > 1 else EXE)
        return 0
    if argv[0] in ("stop", "reset"):
        (stop if argv[0] == "stop" else reset)()
        return 0
    request = json.loads(argv[0]) if argv[0].startswith("{") else \
        {"do": argv[0], **{key: _value(value) for key, _, value in (pair.partition("=") for pair in argv[1:])}}
    try:
        answer = ask(request)
    except FileNotFoundError:
        print(f"aerio isn't running in the world {_world(WORLD)}: python Windows/bench.py start", file=sys.stderr)
        return 2
    print(json.dumps(answer, ensure_ascii=False, indent=2))
    return 1 if isinstance(answer, dict) and "error" in answer else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
