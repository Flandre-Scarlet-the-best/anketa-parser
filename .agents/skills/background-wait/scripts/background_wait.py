#!/usr/bin/env python3
"""Запуск команды с уведомлением исходной сессии Codex после завершения."""

from __future__ import annotations

import argparse
import contextlib
import json
import os
import shutil
import subprocess
import sys
import time
import uuid
from collections.abc import Iterator
from pathlib import Path


def write_json(path: Path, data: dict[str, object]) -> None:
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(data, ensure_ascii=False), encoding="utf-8")
    os.replace(temporary, path)


def read_json(path: Path) -> dict[str, object]:
    return json.loads(path.read_text(encoding="utf-8"))


def job_root() -> Path:
    configured = os.environ.get("CODEX_HOME")
    return (
        Path(configured) if configured else Path.home() / ".codex"
    ) / "background-wait"


@contextlib.contextmanager
def notification_lock(directory: Path) -> Iterator[None]:
    import msvcrt

    with (directory / "notification.lock").open("a+b") as lock:
        lock.seek(0)
        msvcrt.locking(lock.fileno(), msvcrt.LK_LOCK, 1)
        try:
            yield
        finally:
            lock.seek(0)
            msvcrt.locking(lock.fileno(), msvcrt.LK_UNLCK, 1)


def start(args: argparse.Namespace) -> int:
    if os.name != "nt":
        raise ValueError("Фоновый запуск поддерживается только в Windows")
    thread = str(uuid.UUID(args.thread or os.environ.get("CODEX_THREAD_ID", "")))
    cwd = Path(args.cwd)
    if not cwd.is_absolute() or not cwd.is_dir():
        raise ValueError("Рабочий каталог должен быть существующим абсолютным путём")
    command = args.command
    if command and command[0] == "--":
        command = command[1:]
    if not command:
        raise ValueError("После -- необходимо указать исполняемый файл и аргументы")
    executable = shutil.which(command[0])
    if not executable or Path(executable).suffix.lower() in {".cmd", ".bat"}:
        raise ValueError(
            "Укажите доступный исполняемый файл, а не cmd/bat или псевдоним"
        )
    codex = shutil.which("codex.cmd") or shutil.which("codex.exe")
    if not codex:
        raise ValueError("Не найден исполняемый файл Codex CLI")

    job_id = str(uuid.uuid4())
    directory = job_root() / job_id
    directory.mkdir(parents=True, exist_ok=False)
    write_json(
        directory / "request.json",
        {"thread": thread, "cwd": str(cwd), "command": command, "codex": codex},
    )
    flags = (
        subprocess.CREATE_NEW_PROCESS_GROUP
        | subprocess.DETACHED_PROCESS
        | subprocess.CREATE_NO_WINDOW
    )
    try:
        subprocess.Popen(  # noqa: S603 - запуск собственного процесса-наблюдателя
            [sys.executable, str(Path(__file__).resolve()), "worker", str(directory)],
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            close_fds=True,
            creationflags=flags,
        )
    except OSError as error:
        write_json(directory / "startup-error.json", {"error": str(error)})
        print(
            json.dumps(
                {"state": "startup_failed", "job_dir": str(directory)},
                ensure_ascii=False,
            )
        )
        return 1

    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        for name in ("started.json", "result.json"):
            path = directory / name
            if path.exists():
                state = "completed" if name == "result.json" else "running"
                print(
                    json.dumps(
                        {"state": state, "job_id": job_id, "job_dir": str(directory)},
                        ensure_ascii=False,
                    )
                )
                return 0
        time.sleep(0.05)
    print(
        json.dumps(
            {"state": "unknown", "job_id": job_id, "job_dir": str(directory)},
            ensure_ascii=False,
        )
    )
    return 1


def worker(directory: Path) -> int:
    job_id = str(uuid.UUID(directory.name))
    if directory.name != job_id:
        raise ValueError("Некорректный идентификатор задания")
    request = read_json(directory / "request.json")
    thread = str(uuid.UUID(str(request["thread"])))
    result: dict[str, object]
    started = time.time()
    try:
        with (
            (directory / "stdout.log").open("wb") as stdout,
            (directory / "stderr.log").open("wb") as stderr,
        ):
            process = subprocess.Popen(  # noqa: S603 - команда явно передана вызывающей стороной
                request["command"],
                cwd=request["cwd"],
                stdin=subprocess.DEVNULL,
                stdout=stdout,
                stderr=stderr,
                close_fds=True,
                creationflags=subprocess.CREATE_NO_WINDOW,
            )
            write_json(directory / "started.json", {"pid": process.pid})
            exit_code = process.wait()
        result = {
            "state": "completed",
            "exit_code": exit_code,
            "duration_seconds": time.time() - started,
        }
    except (OSError, ValueError) as error:
        result = {
            "state": "start_failed",
            "error": str(error),
            "duration_seconds": time.time() - started,
        }
    write_json(directory / "result.json", result)
    with notification_lock(directory):
        if (directory / "cancel.json").exists():
            write_json(directory / "delivery.json", {"state": "suppressed"})
            return 0
        message = (
            f"Завершена фоновая задача {job_id}. Это машинное уведомление. "
            "Проверь сохранённые result.json и журналы; продолжай только актуальную задачу."
        )
        try:
            delivery = subprocess.run(  # noqa: S603 - фиксированные аргументы очереди и UUID
                [
                    request["codex"],
                    "queue",
                    "--resume",
                    "--thread",
                    thread,
                    "--message",
                    message,
                ],
                check=False,
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                timeout=60,
                creationflags=subprocess.CREATE_NO_WINDOW,
            )
            write_json(
                directory / "delivery.json",
                {
                    "state": "queued"
                    if delivery.returncode == 0 and not delivery.stderr.strip()
                    else "uncertain",
                    "exit_code": delivery.returncode,
                    "stdout": delivery.stdout,
                    "stderr": delivery.stderr,
                },
            )
        except (OSError, subprocess.TimeoutExpired) as error:
            write_json(
                directory / "delivery.json", {"state": "failed", "error": str(error)}
            )
    return 0


def cancel(directory: Path) -> int:
    if not (directory / "request.json").is_file():
        raise ValueError("Указанный каталог не содержит задания")
    with notification_lock(directory):
        if (directory / "delivery.json").exists():
            print(
                json.dumps(
                    {"state": "already_finished", "job_dir": str(directory)},
                    ensure_ascii=False,
                )
            )
            return 1
        write_json(directory / "cancel.json", {"requested": True})
    print(
        json.dumps(
            {"state": "suppression_requested", "job_dir": str(directory)},
            ensure_ascii=False,
        )
    )
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="action", required=True)
    start_parser = subparsers.add_parser("start")
    start_parser.add_argument("--cwd", required=True)
    start_parser.add_argument("--thread")
    start_parser.add_argument("command", nargs=argparse.REMAINDER)
    for name in ("worker", "cancel"):
        subparsers.add_parser(name).add_argument("directory", type=Path)
    args = parser.parse_args()
    try:
        if args.action == "start":
            return start(args)
        if args.action == "worker":
            return worker(args.directory)
        return cancel(args.directory)
    except (OSError, ValueError) as error:
        print(f"Ошибка: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
