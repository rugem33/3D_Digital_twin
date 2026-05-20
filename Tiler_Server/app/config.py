import os
from pathlib import Path


BASE_DIR = Path(os.getenv("APP_BASE_DIR", "/app")).resolve()
DATA_DIR = Path(os.getenv("DATA_DIR", "/data")).resolve()
UPLOAD_DIR = Path(os.getenv("UPLOAD_DIR", DATA_DIR / "uploads")).resolve()
OUTPUT_DIR = Path(os.getenv("OUTPUT_DIR", DATA_DIR / "outputs")).resolve()

MAGO_TILER_JAR = Path(
    os.getenv("MAGO_TILER_JAR", "/opt/mago/mago-3d-tiler-1.15.4.jar")
).resolve()

DEFAULT_INPUT_TYPE = os.getenv("DEFAULT_INPUT_TYPE", "shp")
DEFAULT_OUTPUT_TYPE = os.getenv("DEFAULT_OUTPUT_TYPE", "b3dm")
DEFAULT_COORDINATE_CODE = os.getenv("DEFAULT_COORDINATE_CODE", "5186")
DEFAULT_HEIGHT_COLUMN = os.getenv("DEFAULT_HEIGHT_COLUMN", "height")
DEFAULT_SKIRT_HEIGHT = os.getenv("DEFAULT_SKIRT_HEIGHT", "10.0")

CONVERT_TIMEOUT_SECONDS = int(os.getenv("CONVERT_TIMEOUT_SECONDS", "1800"))
PUBLIC_BASE_URL = os.getenv("PUBLIC_BASE_URL", "").rstrip("/")

# JVM 힙 설정 — 미설정 시 GC 반복으로 전처리 속도 저하
JAVA_XMS = os.getenv("JAVA_XMS", "512m")   # 초기 힙
JAVA_XMX = os.getenv("JAVA_XMX", "4g")     # 최대 힙 (서버 가용 메모리의 70% 이하 권장)

TERRAIN_DIR = Path(os.getenv("TERRAIN_DIR", str(DATA_DIR / "terrain_tiles"))).resolve()
DEFAULT_TERRAIN_MAX_ZOOM = int(os.getenv("DEFAULT_TERRAIN_MAX_ZOOM", "12"))

ALLOWED_EXTENSIONS = {
    ".shp",
    ".shx",
    ".dbf",
    ".prj",
    ".cpg",
    ".qix",
    ".sbn",
    ".sbx",
    ".zip",
    ".tif",
    ".tiff",
    ".img",
    ".hgt",
    ".json",
    ".geojson",
}
