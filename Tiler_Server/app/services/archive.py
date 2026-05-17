import shutil
import zipfile
from pathlib import Path


def zip_directory(source_dir: Path, zip_path: Path) -> Path:
    """Create a zip archive from source_dir."""
    if zip_path.exists():
        zip_path.unlink()

    with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED) as zf:
        for file_path in sorted(source_dir.rglob("*")):
            if file_path.is_file() and file_path != zip_path:
                zf.write(file_path, file_path.relative_to(source_dir))
    return zip_path


def reset_dir(path: Path) -> None:
    if path.exists():
        shutil.rmtree(path)
    path.mkdir(parents=True, exist_ok=True)
