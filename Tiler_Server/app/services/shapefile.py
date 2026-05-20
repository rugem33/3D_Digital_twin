import json
import zipfile
from pathlib import Path
from typing import Any

from werkzeug.datastructures import FileStorage
from werkzeug.utils import secure_filename

from app.config import ALLOWED_EXTENSIONS


def _safe_member_path(target_dir: Path, member_name: str) -> Path:
    resolved = (target_dir / member_name).resolve()
    if target_dir.resolve() not in resolved.parents and resolved != target_dir.resolve():
        raise ValueError(f"Unsafe zip member path: {member_name}")
    return resolved


def save_uploads(files: list[FileStorage], upload_dir: Path) -> list[Path]:
    saved: list[Path] = []
    upload_dir.mkdir(parents=True, exist_ok=True)

    for upload in files:
        if not upload or not upload.filename:
            continue
        filename = secure_filename(upload.filename)
        suffix = Path(filename).suffix.lower()
        if suffix not in ALLOWED_EXTENSIONS:
            raise ValueError(f"Unsupported file extension: {suffix}")

        out_path = upload_dir / filename
        upload.save(out_path)
        saved.append(out_path)

        if suffix == ".zip":
            extract_zip(out_path, upload_dir)

    return saved


def extract_zip(zip_path: Path, target_dir: Path) -> None:
    with zipfile.ZipFile(zip_path) as zf:
        for info in zf.infolist():
            if info.is_dir():
                continue
            suffix = Path(info.filename).suffix.lower()
            if suffix not in ALLOWED_EXTENSIONS:
                continue
            out_path = _safe_member_path(target_dir, info.filename)
            out_path.parent.mkdir(parents=True, exist_ok=True)
            with zf.open(info) as src, out_path.open("wb") as dst:
                dst.write(src.read())


def find_shapefile(input_dir: Path) -> Path:
    shp_files = sorted(input_dir.rglob("*.shp"))
    if not shp_files:
        raise ValueError("No .shp file found. Upload .shp/.shx/.dbf/.prj files or a zip.")
    if len(shp_files) > 1:
        names = ", ".join(str(p.relative_to(input_dir)) for p in shp_files)
        raise ValueError(f"Multiple .shp files found. Upload one shapefile per request: {names}")
    return shp_files[0]


def find_terrain_file(input_dir: Path) -> Path | None:
    terrain_files = sorted(input_dir.rglob("*.tif")) + sorted(input_dir.rglob("*.tiff"))
    if not terrain_files:
        return None
    if len(terrain_files) > 1:
        names = ", ".join(str(p.relative_to(input_dir)) for p in terrain_files)
        raise ValueError(f"Multiple terrain tif files found. Upload one terrain tif per request: {names}")
    return terrain_files[0]


def read_attributes(form_value: str | None, json_file: FileStorage | None, upload_dir: Path) -> dict[str, Any]:
    if json_file and json_file.filename:
        filename = secure_filename(json_file.filename)
        target = upload_dir / filename
        json_file.save(target)
        return json.loads(target.read_text(encoding="utf-8"))

    if form_value:
        return json.loads(form_value)

    return {}
