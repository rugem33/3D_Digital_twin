import json
import os
import time
import uuid
from pathlib import Path

from flask import Flask, Response, jsonify, request, send_from_directory, stream_with_context

from app import config
from app.services.archive import reset_dir, zip_directory
from app.services.jobs import read_job_logs, read_job_status, start_conversion_job
from app.services.shapefile import find_shapefile, find_terrain_file, read_attributes, save_uploads
from app.services.tiler import TilerOptions, run_mago_tiler


ALLOWED_OUTPUT_TYPES = {"b3dm", "i3dm", "pnts"}


def create_app() -> Flask:
    app = Flask(__name__)
    app.config["MAX_CONTENT_LENGTH"] = int(os.getenv("MAX_UPLOAD_MB", "512")) * 1024 * 1024
    ensure_directories()

    @app.after_request
    def cors(resp: Response) -> Response:
        resp.headers["Access-Control-Allow-Origin"] = "*"
        resp.headers["Access-Control-Allow-Headers"] = "*"
        resp.headers["Access-Control-Allow-Methods"] = "GET,POST,OPTIONS"
        return resp

    @app.get("/health")
    def health():
        return jsonify({"status": "ok"})

    @app.get("/status")
    def status():
        return jsonify(
            {
                "status": "ok",
                "mago_tiler_jar": str(config.MAGO_TILER_JAR),
                "mago_tiler_exists": config.MAGO_TILER_JAR.exists(),
                "upload_dir": str(config.UPLOAD_DIR),
                "output_dir": str(config.OUTPUT_DIR),
            }
        )

    @app.post("/api/convert")
    def convert_async():
        job_id = uuid.uuid4().hex
        job_upload_dir = config.UPLOAD_DIR / job_id
        job_output_dir = config.OUTPUT_DIR / job_id
        reset_dir(job_upload_dir)
        reset_dir(job_output_dir)

        try:
            conversion = prepare_conversion_request(job_id, job_upload_dir, job_output_dir)
            start_conversion_job(
                job_id,
                job_upload_dir,
                job_output_dir,
                conversion["options"],
                conversion["terrain_path"],
                conversion["attributes"],
                request.host_url.rstrip("/"),
            )
            return jsonify(
                {
                    "jobId": job_id,
                    "job_id": job_id,
                    "status": "running",
                    "statusUrl": public_url(f"/api/jobs/{job_id}/status"),
                    "logsUrl": public_url(f"/api/jobs/{job_id}/logs"),
                    "stdoutLogUrl": public_url(f"/outputs/{job_id}/mago_stdout.log"),
                    "stderrLogUrl": public_url(f"/outputs/{job_id}/mago_stderr.log"),
                }
            ), 202
        except Exception as exc:
            error_path = job_output_dir / "error.json"
            error_path.write_text(
                json.dumps({"job_id": job_id, "error": str(exc)}, ensure_ascii=False, indent=2),
                encoding="utf-8",
            )
            return jsonify({"jobId": job_id, "job_id": job_id, "status": "failed", "error": str(exc)}), 400

    @app.post("/convert")
    def convert_sync():
        job_id = uuid.uuid4().hex
        job_upload_dir = config.UPLOAD_DIR / job_id
        job_output_dir = config.OUTPUT_DIR / job_id
        reset_dir(job_upload_dir)
        reset_dir(job_output_dir)

        try:
            conversion = prepare_conversion_request(job_id, job_upload_dir, job_output_dir)
            terrain_path = conversion["terrain_path"]
            options = conversion["options"]
            attributes = conversion["attributes"]

            result = run_mago_tiler(job_upload_dir, job_output_dir, options, terrain_path)
            stdout_path = job_output_dir / "mago_stdout.log"
            stderr_path = job_output_dir / "mago_stderr.log"
            stdout_path.write_text(result.stdout, encoding="utf-8")
            stderr_path.write_text(result.stderr, encoding="utf-8")

            zip_path = zip_directory(job_output_dir, job_output_dir / "b3dm_result.zip")
            tileset_url = public_url(f"/outputs/{job_id}/{result.tileset_path.relative_to(job_output_dir)}")
            zip_url = public_url(f"/outputs/{job_id}/{zip_path.name}")
            stdout_url = public_url(f"/outputs/{job_id}/{stdout_path.name}")
            stderr_url = public_url(f"/outputs/{job_id}/{stderr_path.name}")

            return jsonify(
                {
                    "job_id": job_id,
                    "status": "completed",
                    "tileset_url": tileset_url,
                    "tilesetUrl": tileset_url,
                    "zip_url": zip_url,
                    "zipUrl": zip_url,
                    "stdout_log_url": stdout_url,
                    "stdoutLogUrl": stdout_url,
                    "stderr_log_url": stderr_url,
                    "stderrLogUrl": stderr_url,
                    "attributes": attributes,
                    "command": result.command,
                }
            )
        except Exception as exc:
            error_path = job_output_dir / "error.json"
            error_path.write_text(
                json.dumps({"job_id": job_id, "error": str(exc)}, ensure_ascii=False, indent=2),
                encoding="utf-8",
            )
            return jsonify({"job_id": job_id, "status": "failed", "error": str(exc)}), 400

    @app.get("/api/jobs/<job_id>/status")
    def job_status(job_id: str):
        status = read_job_status(job_id)
        if status is None:
            return jsonify({"error": "job not found", "jobId": job_id, "job_id": job_id}), 404
        return jsonify(status)

    @app.get("/api/jobs/<job_id>/logs")
    def job_logs(job_id: str):
        max_chars = int(request.args.get("maxChars", "12000"))
        logs = read_job_logs(job_id, max_chars=max_chars)
        if logs is None:
            return jsonify({"error": "job not found", "jobId": job_id, "job_id": job_id}), 404
        return jsonify(logs)

    @app.get("/api/jobs/<job_id>/events")
    def job_events(job_id: str):
        @stream_with_context
        def event_stream():
            while True:
                status = read_job_status(job_id)
                if status is None:
                    yield f"event: error\ndata: {json.dumps({'error': 'job not found'})}\n\n"
                    break
                yield f"event: status\ndata: {json.dumps(status, ensure_ascii=False)}\n\n"
                if status.get("status") in {"completed", "failed"}:
                    break
                time.sleep(1)

        return Response(event_stream(), content_type="text/event-stream")

    @app.get("/outputs/<job_id>/<path:filename>")
    def outputs(job_id: str, filename: str):
        output_dir = (config.OUTPUT_DIR / job_id).resolve()
        if config.OUTPUT_DIR.resolve() not in output_dir.parents and output_dir != config.OUTPUT_DIR.resolve():
            return jsonify({"error": "invalid job id"}), 400
        return send_from_directory(output_dir, filename)

    return app


def prepare_conversion_request(job_id: str, job_upload_dir: Path, job_output_dir: Path) -> dict:
    upload_files = []
    upload_files.extend(request.files.getlist("files"))
    upload_files.extend(request.files.getlist("files[]"))
    if "shp_zip" in request.files:
        upload_files.append(request.files["shp_zip"])
    if "shpZip" in request.files:
        upload_files.append(request.files["shpZip"])
    if "terrain_tif" in request.files:
        upload_files.append(request.files["terrain_tif"])
    if "demFile" in request.files:
        upload_files.append(request.files["demFile"])

    save_uploads(upload_files, job_upload_dir)
    shp_path = find_shapefile(job_upload_dir)
    terrain_path = find_terrain_file(job_upload_dir)

    attributes = read_attributes(
        request.form.get("attributes"),
        request.files.get("attributes_file"),
        job_upload_dir,
    )
    output_type = form_value("outputType", "output_type", default=config.DEFAULT_OUTPUT_TYPE).lower()
    if output_type not in ALLOWED_OUTPUT_TYPES:
        raise ValueError(f"Invalid outputType: {output_type}. Allowed values: {sorted(ALLOWED_OUTPUT_TYPES)}")

    options = TilerOptions(
        coordinate_code=form_value("coordinateSystem", "coordinate_code", default=config.DEFAULT_COORDINATE_CODE),
        height_column=form_value("heightColumn", "height_column", default=config.DEFAULT_HEIGHT_COLUMN),
        skirt_height=form_value("scaleHeight", "skirt_height", default=config.DEFAULT_SKIRT_HEIGHT),
        input_type=config.DEFAULT_INPUT_TYPE,
        output_type=output_type,
        curvature_correction=form_bool("curvatureCorrection", "curvature_correction", default=True),
    )

    metadata_path = job_upload_dir / "request_attributes.json"
    metadata_path.write_text(
        json.dumps(
            {
                "job_id": job_id,
                "shapefile": str(shp_path.relative_to(job_upload_dir)),
                "terrain_tif": str(terrain_path.relative_to(job_upload_dir)) if terrain_path else None,
                "attributes": attributes,
                "tiler_options": options.__dict__,
            },
            ensure_ascii=False,
            indent=2,
        ),
        encoding="utf-8",
    )

    return {
        "shp_path": shp_path,
        "terrain_path": terrain_path,
        "attributes": attributes,
        "options": options,
    }


def ensure_directories() -> None:
    config.UPLOAD_DIR.mkdir(parents=True, exist_ok=True)
    config.OUTPUT_DIR.mkdir(parents=True, exist_ok=True)


def form_value(*names: str, default: str) -> str:
    for name in names:
        value = request.form.get(name)
        if value is not None and value != "":
            return value
    return default


def form_bool(*names: str, default: bool) -> bool:
    value = form_value(*names, default=str(default))
    return value.strip().lower() in {"1", "true", "yes", "y", "on"}


def public_url(path: str | Path) -> str:
    clean_path = "/" + str(path).lstrip("/")
    if config.PUBLIC_BASE_URL:
        return f"{config.PUBLIC_BASE_URL}{clean_path}"
    return f"{request.host_url.rstrip('/')}{clean_path}"


app = create_app()


if __name__ == "__main__":
    port = int(os.getenv("PORT", "8000"))
    app.run(host="0.0.0.0", port=port)
