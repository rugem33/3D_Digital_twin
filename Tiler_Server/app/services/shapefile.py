import json
import zipfile
from pathlib import Path
from typing import Any

import shapefile as pyshp
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


def try_parse_float(value: str | None) -> float | None:
    if value is None:
        return None
    text = value.strip()
    if not text:
        return None
    try:
        return float(text)
    except ValueError:
        return None


def apply_constant_height_column(shp_path: Path, height: float, field_name: str = "RT_HEIGHT") -> str:
    """Add or overwrite a numeric DBF height column for mago's -hc option."""
    field_name = field_name[:10]
    base = shp_path.with_suffix("")
    tmp_base = shp_path.with_name(f"{shp_path.stem}__height_tmp")

    last_error: UnicodeDecodeError | None = None
    for encoding in _candidate_dbf_encodings(shp_path):
        try:
            _write_constant_height_column(base, tmp_base, height, field_name, encoding)
            return field_name
        except UnicodeDecodeError as exc:
            last_error = exc

    if last_error is not None:
        raise last_error
    raise RuntimeError(f"Failed to update DBF height column: {shp_path}")


def _write_constant_height_column(
    base: Path,
    tmp_base: Path,
    height: float,
    field_name: str,
    encoding: str,
) -> None:
    reader = pyshp.Reader(str(base), encoding=encoding)
    fields = [list(field) for field in reader.fields[1:]]
    field_names = [str(field[0]).upper() for field in fields]
    target_upper = field_name.upper()

    try:
        target_index = field_names.index(target_upper)
    except ValueError:
        target_index = -1
        fields.append([field_name, "F", 18, 6])

    writer = pyshp.Writer(str(tmp_base), shapeType=reader.shapeType, encoding=encoding)
    try:
        for field in fields:
            writer.field(*field)

        for shape_record in reader.iterShapeRecords():
            record = list(shape_record.record)
            if target_index >= 0:
                record[target_index] = height
            else:
                record.append(height)
            writer.shape(shape_record.shape)
            writer.record(*record)
    finally:
        writer.close()
        reader.close()

    for suffix in (".shp", ".shx", ".dbf"):
        tmp = tmp_base.with_suffix(suffix)
        if tmp.exists():
            tmp.replace(base.with_suffix(suffix))


def _candidate_dbf_encodings(shp_path: Path) -> list[str]:
    cpg_path = shp_path.with_suffix(".cpg")
    candidates: list[str] = []
    if cpg_path.exists():
        cpg = cpg_path.read_text(encoding="ascii", errors="ignore").strip()
        if cpg:
            candidates.append(_normalize_cpg_encoding(cpg))

    for encoding in ("utf-8", "cp949", "euc-kr", "latin1"):
        if encoding not in candidates:
            candidates.append(encoding)
    return candidates


def _normalize_cpg_encoding(value: str) -> str:
    upper = value.strip().upper()
    if upper in {"949", "CP949", "MS949", "WINDOWS-949"}:
        return "cp949"
    if upper in {"51949", "EUC-KR", "EUCKR"}:
        return "euc-kr"
    if upper in {"65001", "UTF8", "UTF-8"}:
        return "utf-8"
    return value.strip()
