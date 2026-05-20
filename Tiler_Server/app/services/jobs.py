import json
import re
import subprocess
import tempfile
import threading
import time
from pathlib import Path
from typing import Any

from app import config
from app.services.archive import zip_directory
from app.services.tiler import TilerOptions, build_mago_command, find_tileset


PRE_RE = re.compile(r"\[Pre\].*?\[(\d+)/(\d+)\]")
POST_RE = re.compile(r"\[Post\]\[(\d+)/(\d+)\]")


def start_conversion_job(
    job_id: str,
    input_dir: Path,
    output_dir: Path,
    options: TilerOptions,
    terrain_path: Path | None,
    attributes: dict[str, Any],
    public_base_url: str = "",
) -> None:
    _write_status(
        job_id,
        output_dir,
        {
            "status": "running",
            "stage": "received",
            "progress": 0,
            "progressText": "Upload received",
            "attributes": attributes,
            "statusUrl": _public_url(f"/api/jobs/{job_id}/status", public_base_url),
            "logsUrl": _public_url(f"/api/jobs/{job_id}/logs", public_base_url),
            "eventsUrl": _public_url(f"/api/jobs/{job_id}/events", public_base_url),
            "stdoutLogUrl": _public_url(f"/outputs/{job_id}/mago_stdout.log", public_base_url),
            "stderrLogUrl": _public_url(f"/outputs/{job_id}/mago_stderr.log", public_base_url),
        },
    )
    thread = threading.Thread(
        target=_run_conversion_job,
        args=(job_id, input_dir, output_dir, options, terrain_path, attributes, public_base_url),
        daemon=True,
    )
    thread.start()


def read_job_status(job_id: str) -> dict[str, Any] | None:
    status_path = _status_path(job_id)
    # exists() 후 read_text() 사이 TOCTOU 경쟁 조건 방지 — EAFP 패턴 사용
    try:
        return json.loads(status_path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        return None
    except (json.JSONDecodeError, OSError):
        # 다른 스레드가 파일을 쓰는 도중 읽힌 경우 → 일시적 오류, None 반환
        return None


def read_job_logs(job_id: str, max_chars: int = 12000) -> dict[str, Any] | None:
    output_dir = _job_output_dir(job_id)
    if not output_dir.exists():
        return None
    return {
        "job_id": job_id,
        "stdout": _tail_text(output_dir / "mago_stdout.log", max_chars),
        "stderr": _tail_text(output_dir / "mago_stderr.log", max_chars),
    }


def _run_conversion_job(
    job_id: str,
    input_dir: Path,
    output_dir: Path,
    options: TilerOptions,
    terrain_path: Path | None,
    attributes: dict[str, Any],
    public_base_url: str = "",
) -> None:
    stdout_path = output_dir / "mago_stdout.log"
    stderr_path = output_dir / "mago_stderr.log"

    try:
        if not config.MAGO_TILER_JAR.exists():
            raise FileNotFoundError(f"mago3d tiler jar not found: {config.MAGO_TILER_JAR}")

        command = build_mago_command(input_dir, output_dir, options, terrain_path)
        _write_status(
            job_id,
            output_dir,
            {
                "status": "running",
                "stage": "queued",
                "progress": 0,
                "progressText": "Job queued",
                "command": command,
                "attributes": attributes,
                "stdoutLogUrl": _public_url(f"/outputs/{job_id}/{stdout_path.name}", public_base_url),
                "stderrLogUrl": _public_url(f"/outputs/{job_id}/{stderr_path.name}", public_base_url),
                "statusUrl": _public_url(f"/api/jobs/{job_id}/status", public_base_url),
                "logsUrl": _public_url(f"/api/jobs/{job_id}/logs", public_base_url),
                "eventsUrl": _public_url(f"/api/jobs/{job_id}/events", public_base_url),
            },
        )

        with stdout_path.open("w", encoding="utf-8") as stdout_file, stderr_path.open(
            "w", encoding="utf-8"
        ) as stderr_file:
            process = subprocess.Popen(
                command,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                bufsize=1,
            )

            def consume(stream, sink, stream_name: str) -> None:
                assert stream is not None
                for line in stream:
                    sink.write(line)
                    sink.flush()
                    if stream_name == "stdout":
                        _update_progress_from_line(job_id, output_dir, line)

            stdout_thread = threading.Thread(
                target=consume, args=(process.stdout, stdout_file, "stdout"), daemon=True
            )
            stderr_thread = threading.Thread(
                target=consume, args=(process.stderr, stderr_file, "stderr"), daemon=True
            )
            stdout_thread.start()
            stderr_thread.start()

            try:
                return_code = process.wait(timeout=config.CONVERT_TIMEOUT_SECONDS)
            except subprocess.TimeoutExpired:
                process.kill()
                return_code = process.wait(timeout=10)
                raise RuntimeError(
                    f"mago3d tiler timed out after {config.CONVERT_TIMEOUT_SECONDS} seconds"
                )
            stdout_thread.join(timeout=5)
            stderr_thread.join(timeout=5)

        if return_code != 0:
            raise RuntimeError(f"mago3d tiler failed with exit code {return_code}")

        tileset_path = find_tileset(output_dir)
        zip_path = zip_directory(output_dir, output_dir / "b3dm_result.zip")
        _write_status(
            job_id,
            output_dir,
            {
                "status": "completed",
                "stage": "completed",
                "progress": 100,
                "progressText": "Conversion completed",
                "tilesetUrl": _public_url(f"/outputs/{job_id}/{tileset_path.relative_to(output_dir)}", public_base_url),
                "tileset_url": _public_url(f"/outputs/{job_id}/{tileset_path.relative_to(output_dir)}", public_base_url),
                "zipUrl": _public_url(f"/outputs/{job_id}/{zip_path.name}", public_base_url),
                "zip_url": _public_url(f"/outputs/{job_id}/{zip_path.name}", public_base_url),
            },
            merge=True,
        )
    except Exception as exc:
        _write_status(
            job_id,
            output_dir,
            {
                "status": "failed",
                "stage": "failed",
                "progressText": str(exc),
                "error": str(exc),
            },
            merge=True,
        )


def _update_progress_from_line(job_id: str, output_dir: Path, line: str) -> None:
    update: dict[str, Any] | None = None

    pre = PRE_RE.search(line)
    if pre:
        current, total = int(pre.group(1)), int(pre.group(2))
        progress = 10 + int((current / max(total, 1)) * 40)
        update = {
            "stage": "preprocess",
            "progress": min(progress, 50),
            "progressText": line.strip(),
        }

    if "[Tile] Start" in line:
        update = {"stage": "tiling", "progress": 55, "progressText": line.strip()}
    elif "[Tile] Writing" in line or "[Tile][Tileset]" in line:
        update = {"stage": "tiling", "progress": 65, "progressText": line.strip()}

    post = POST_RE.search(line)
    if post:
        current, total = int(post.group(1)), int(post.group(2))
        progress = 70 + int((current / max(total, 1)) * 25)
        update = {
            "stage": "postprocess",
            "progress": min(progress, 95),
            "progressText": line.strip(),
        }

    if "[Process Summary]" in line:
        update = {"stage": "finalizing", "progress": 98, "progressText": line.strip()}

    if update:
        _write_status(job_id, output_dir, update, merge=True)


def _write_status(
    job_id: str,
    output_dir: Path,
    payload: dict[str, Any],
    merge: bool = False,
) -> None:
    output_dir.mkdir(parents=True, exist_ok=True)
    status_path = output_dir / "status.json"

    data: dict[str, Any] = {}
    if merge:
        # EAFP: 읽기 실패 시 빈 dict 로 시작 (파일이 없거나 다른 스레드가 교체 중일 수 있음)
        try:
            data = json.loads(status_path.read_text(encoding="utf-8"))
        except (FileNotFoundError, json.JSONDecodeError, OSError):
            pass

    data.update(
        {
            "jobId": job_id,
            "job_id": job_id,
            "updatedAt": time.time(),
            **payload,
        }
    )

    # 고정 tmp 파일명 대신 유니크 tmp 파일 사용 → 다중 스레드 동시 쓰기 경쟁 방지
    fd, tmp_name = tempfile.mkstemp(dir=output_dir, suffix=".tmp")
    try:
        with open(fd, "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=2)
        Path(tmp_name).replace(status_path)  # POSIX atomic rename
    except Exception:
        try:
            Path(tmp_name).unlink(missing_ok=True)
        except OSError:
            pass
        raise


def _tail_text(path: Path, max_chars: int) -> str:
    if not path.exists():
        return ""
    text = path.read_text(encoding="utf-8", errors="replace")
    if len(text) <= max_chars:
        return text
    return text[-max_chars:]


def _status_path(job_id: str) -> Path:
    return _job_output_dir(job_id) / "status.json"


def _job_output_dir(job_id: str) -> Path:
    return config.OUTPUT_DIR / job_id


def _public_url(path: str | Path, public_base_url: str = "") -> str:
    clean_path = "/" + str(path).lstrip("/")
    if config.PUBLIC_BASE_URL:
        return f"{config.PUBLIC_BASE_URL}{clean_path}"
    if public_base_url:
        return f"{public_base_url.rstrip('/')}{clean_path}"
    return clean_path
