import json
import re
import subprocess
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
            "statusUrl": _public_url(f"/api/jobs/{job_id}/status"),
            "logsUrl": _public_url(f"/api/jobs/{job_id}/logs"),
            "eventsUrl": _public_url(f"/api/jobs/{job_id}/events"),
            "stdoutLogUrl": _public_url(f"/outputs/{job_id}/mago_stdout.log"),
            "stderrLogUrl": _public_url(f"/outputs/{job_id}/mago_stderr.log"),
        },
    )
    thread = threading.Thread(
        target=_run_conversion_job,
        args=(job_id, input_dir, output_dir, options, terrain_path, attributes),
        daemon=True,
    )
    thread.start()


def read_job_status(job_id: str) -> dict[str, Any] | None:
    status_path = _status_path(job_id)
    if not status_path.exists():
        return None
    return json.loads(status_path.read_text(encoding="utf-8"))


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
                "stdoutLogUrl": _public_url(f"/outputs/{job_id}/{stdout_path.name}"),
                "stderrLogUrl": _public_url(f"/outputs/{job_id}/{stderr_path.name}"),
                "statusUrl": _public_url(f"/api/jobs/{job_id}/status"),
                "logsUrl": _public_url(f"/api/jobs/{job_id}/logs"),
                "eventsUrl": _public_url(f"/api/jobs/{job_id}/events"),
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
                "tilesetUrl": _public_url(f"/outputs/{job_id}/{tileset_path.relative_to(output_dir)}"),
                "tileset_url": _public_url(f"/outputs/{job_id}/{tileset_path.relative_to(output_dir)}"),
                "zipUrl": _public_url(f"/outputs/{job_id}/{zip_path.name}"),
                "zip_url": _public_url(f"/outputs/{job_id}/{zip_path.name}"),
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
    if merge and status_path.exists():
        data = json.loads(status_path.read_text(encoding="utf-8"))
    data.update(
        {
            "jobId": job_id,
            "job_id": job_id,
            "updatedAt": time.time(),
            **payload,
        }
    )
    tmp_path = output_dir / "status.json.tmp"
    tmp_path.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
    tmp_path.replace(status_path)


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


def _public_url(path: str | Path) -> str:
    clean_path = "/" + str(path).lstrip("/")
    if config.PUBLIC_BASE_URL:
        return f"{config.PUBLIC_BASE_URL}{clean_path}"
    return clean_path
