#!/usr/bin/env python3
"""
DEM → quantized-mesh 타일 변환 도우미

흐름:
    원본 DEM (.tif / .hgt / .img)
        ↓ 1단계: GDAL로 WGS84 GeoTIFF 변환 (nodata 보존)
        ↓ 2단계: ctb-tile (Docker)로 quantized-mesh 타일 생성
        ↓ 3단계: Tools/tiles/ 에 저장
        → terrain_server.py 가 바로 서빙 가능

사용법:
    python Tools/process_dem.py --input path/to/dem.tif
    python Tools/process_dem.py --input path/to/dem.tif --bounds 124 33 132 39
    python Tools/process_dem.py --input path/to/dem.tif --max-zoom 14
    python Tools/process_dem.py --info   # 데이터 소스 안내
"""

import argparse
import json
import math
import struct
import subprocess
import sys
import shutil
from pathlib import Path

TILES_DIR  = Path(__file__).parent / "tiles"
WORK_DIR   = Path(__file__).parent / "_work"
DOCKER_IMG = "tumgis/ctb-quantized-mesh"
NODATA_VAL = -9999.0          # ctb-tile / gdalwarp 공통 nodata
LOW_ZOOM_FIX_THRESHOLD = 4
FLAT_ZOOM_LEVELS = {7}


def clear_tiles_dir():
    """Keep the tiles directory itself, but remove its generated contents."""
    TILES_DIR.mkdir(exist_ok=True)
    for child in TILES_DIR.iterdir():
        if child.is_dir():
            shutil.rmtree(child)
        else:
            child.unlink()


def tile_bounds(z: int, x: int, y_tms: int):
    x_count = 2 ** (z + 1)
    y_count = 2 ** z
    lon_min = x / x_count * 360.0 - 180.0
    lon_max = (x + 1) / x_count * 360.0 - 180.0
    lat_min = y_tms / y_count * 180.0 - 90.0
    lat_max = (y_tms + 1) / y_count * 180.0 - 90.0
    return lon_min, lat_min, lon_max, lat_max


def to_ecef(lon_deg: float, lat_deg: float, h: float = 0.0):
    a = 6378137.0
    e2 = 0.00669437999014
    lon = math.radians(lon_deg)
    lat = math.radians(lat_deg)
    n = a / math.sqrt(1 - e2 * math.sin(lat) ** 2)
    return (
        (n + h) * math.cos(lat) * math.cos(lon),
        (n + h) * math.cos(lat) * math.sin(lon),
        (n * (1 - e2) + h) * math.sin(lat),
    )


def zigzag(delta: int) -> int:
    return delta * 2 if delta >= 0 else -delta * 2 - 1


def delta_zigzag(values: list[int]) -> list[int]:
    out, prev = [], 0
    for value in values:
        out.append(zigzag(value - prev))
        prev = value
    return out


def encode_high_water_mark(indices: list[int]) -> list[int]:
    encoded = []
    highest = 0
    for index in indices:
        encoded.append(highest - index)
        if index == highest:
            highest += 1
    return encoded


def make_flat_tile(z: int, x: int, y: int, h_max: float = 0.0) -> bytes:
    """Create a minimal quantized-mesh tile with a valid ECEF center."""
    lon_min, lat_min, lon_max, lat_max = tile_bounds(z, x, y)
    cx, cy, cz = to_ecef((lon_min + lon_max) / 2, (lat_min + lat_max) / 2)
    kx, ky, kz = to_ecef(lon_min, lat_min)
    a = 6378137.0
    radius = max(math.dist((cx, cy, cz), (kx, ky, kz)), 1.0)

    header = struct.pack("<ddd", cx, cy, cz)
    header += struct.pack("<ff", 0.0, float(h_max))
    header += struct.pack("<dddd", cx, cy, cz, radius)
    header += struct.pack("<ddd", cx / a, cy / a, cz / a)

    u = [0, 32767, 0, 32767]
    v = [0, 0, 32767, 32767]
    h = [0, 0, 0, 0]
    vertex_data = struct.pack("<I", 4)
    vertex_data += struct.pack("<4H", *delta_zigzag(u))
    vertex_data += struct.pack("<4H", *delta_zigzag(v))
    vertex_data += struct.pack("<4H", *delta_zigzag(h))

    triangle_indices = encode_high_water_mark([0, 1, 2, 1, 3, 2])
    index_data = struct.pack("<I", 2) + struct.pack("<6H", *triangle_indices)

    def edge(a_: int, b_: int):
        return struct.pack("<I", 2) + struct.pack("<2H", a_, b_)

    edge_data = edge(0, 2) + edge(0, 1) + edge(1, 3) + edge(2, 3)
    return header + vertex_data + index_data + edge_data


# ────────────────────────────────────────────────────────────
# GeoTIFF 메타 판독 (GDAL 없이 순수 Python)
# ────────────────────────────────────────────────────────────

def _read_tiff_tags(tif_path: Path) -> dict:
    """GeoTIFF IFD에서 필요한 태그만 읽어 dict 반환."""
    with open(tif_path, "rb") as f:
        hdr = f.read(8)
    endian = "<" if hdr[:2] == b"II" else ">"
    E = endian

    tags = {}
    with open(tif_path, "rb") as f:
        ifd_offset = struct.unpack_from(E + "I", f.read(8), 4)[0]
        f.seek(ifd_offset)
        count = struct.unpack_from(E + "H", f.read(2))[0]

        for _ in range(count):
            raw = f.read(12)
            tag, typ, cnt = struct.unpack_from(E + "HHI", raw)
            val_bytes = raw[8:12]

            def read_val():
                if typ == 3 and cnt == 1:    # SHORT
                    return struct.unpack_from(E + "H", val_bytes)[0]
                if typ == 4 and cnt == 1:    # LONG
                    return struct.unpack_from(E + "I", val_bytes)[0]
                if typ == 11 and cnt == 1:   # FLOAT
                    return struct.unpack_from(E + "f", val_bytes)[0]
                if typ == 12 and cnt == 1:   # DOUBLE
                    off = struct.unpack_from(E + "I", val_bytes)[0]
                    with open(tif_path, "rb") as g:
                        g.seek(off)
                        return struct.unpack_from(E + "d", g.read(8))[0]
                if typ in (3, 4) and cnt > 1:  # SHORT[] / LONG[]
                    off = struct.unpack_from(E + "I", val_bytes)[0]
                    fmt = E + ("H" if typ == 3 else "I") * cnt
                    with open(tif_path, "rb") as g:
                        g.seek(off)
                        return struct.unpack(fmt, g.read(struct.calcsize(fmt)))
                if typ == 12 and cnt > 1:    # DOUBLE[]
                    off = struct.unpack_from(E + "I", val_bytes)[0]
                    with open(tif_path, "rb") as g:
                        g.seek(off)
                        return struct.unpack(E + "d" * cnt, g.read(8 * cnt))
                return None

            tags[tag] = read_val()

    return tags


def inspect_geotiff(tif_path: Path) -> dict:
    """
    GeoTIFF에서 width, height, nodata, strip_offsets, pixel_scale,
    tiepoint, sample_format, bits_per_sample 추출.
    """
    tags = _read_tiff_tags(tif_path)
    info = {
        "width":         tags.get(256),
        "height":        tags.get(257),
        "bits":          tags.get(258),
        "sample_fmt":    tags.get(339, 1),  # 1=uint, 2=int, 3=float
        "strip_offsets": tags.get(273),
        "pixel_scale":   tags.get(33550),   # ModelPixelScaleTag (dx, dy, dz)
        "tiepoint":      tags.get(33922),   # ModelTiepointTag (i,j,k, x,y,z)
        "nodata_tag":    tags.get(42113),   # GDAL_NODATA (ASCII tag)
    }

    # 픽셀 타입 문자열
    fmt_map = {1: "uint", 2: "int", 3: "float"}
    info["pixel_type"] = fmt_map.get(info["sample_fmt"], "unknown")
    return info


def detect_nodata_and_bounds(tif_path: Path) -> tuple:
    """
    GeoTIFF를 직접 읽어 (nodata_value, lon_min, lat_min, lon_max, lat_max) 반환.
    유효 픽셀 범위를 스캔해 실제 데이터 bounds 도 반환.
    """
    info = inspect_geotiff(tif_path)
    W, H = info["width"], info["height"]

    # 좌표 변환 파라미터
    scale   = info["pixel_scale"]    # (dx, dy, dz)
    tie     = info["tiepoint"]       # (0,0,0, lon, lat, 0)
    if scale is None or tie is None:
        return (NODATA_VAL, None, None, None, None)

    dx, dy   = scale[0], scale[1]
    lon0, lat0 = tie[3], tie[4]     # 원점 (좌상단) 좌표

    def pixel_to_geo(col, row):
        return lon0 + col * dx, lat0 - row * dy

    full_lon_min, full_lat_max = pixel_to_geo(0, 0)
    full_lon_max, full_lat_min = pixel_to_geo(W, H)

    # nodata 태그에서 값 읽기
    nodata_tag = info.get("nodata_tag")
    if nodata_tag is not None and isinstance(nodata_tag, str):
        try:
            nodata = float(nodata_tag.strip())
        except ValueError:
            nodata = NODATA_VAL
    else:
        nodata = NODATA_VAL

    # float32 vs int16 읽기
    bits = info["bits"] or 32
    sfmt = info["sample_fmt"]
    px_fmt = "f" if (bits == 32 and sfmt == 3) else ("e" if bits == 16 and sfmt == 3 else "h")
    px_size = bits // 8

    # strip_offsets
    strip_offsets = info["strip_offsets"]
    if strip_offsets is None:
        return (nodata, full_lon_min, full_lat_min, full_lon_max, full_lat_max)
    if isinstance(strip_offsets, int):
        strip_offsets = (strip_offsets,)

    print(f"[분석] DEM 크기: {W}×{H}  bits={bits}  nodata={nodata}")
    print(f"[분석] 전체 범위: lon [{full_lon_min:.3f}~{full_lon_max:.3f}]  "
          f"lat [{full_lat_min:.3f}~{full_lat_max:.3f}]")
    print("[분석] 유효 데이터 범위 스캔 중...")

    valid_col_min = W
    valid_col_max = 0
    valid_row_min = H
    valid_row_max = 0
    found = False

    with open(tif_path, "rb") as f:
        step = max(1, H // 200)   # 최대 200 row 샘플
        for row_i in range(0, H, step):
            strip_i = min(row_i, len(strip_offsets) - 1)
            f.seek(strip_offsets[strip_i])
            row_bytes = f.read(W * px_size)
            if len(row_bytes) < W * px_size:
                continue
            vals = struct.unpack("<" + px_fmt * W, row_bytes)
            valid_cols = [
                c for c, v in enumerate(vals)
                if v != nodata and not (v != v) and -500 < v < 9000
            ]
            if valid_cols:
                found = True
                valid_col_min = min(valid_col_min, valid_cols[0])
                valid_col_max = max(valid_col_max, valid_cols[-1])
                valid_row_min = min(valid_row_min, row_i)
                valid_row_max = max(valid_row_max, row_i)

    if not found:
        print("[경고] 유효 고도 데이터를 찾지 못했습니다. 원본 DEM을 확인하세요.")
        return (nodata, full_lon_min, full_lat_min, full_lon_max, full_lat_max)

    lon_min, lat_max = pixel_to_geo(valid_col_min, valid_row_min)
    lon_max, lat_min = pixel_to_geo(valid_col_max, valid_row_max)

    # 약간의 여백 추가
    margin = max(dx * 2, dy * 2)
    lon_min -= margin; lat_min -= margin
    lon_max += margin; lat_max += margin

    coverage_km_lon = (lon_max - lon_min) * 88.8
    coverage_km_lat = (lat_max - lat_min) * 111.0
    print(f"[분석] 유효 데이터: lon [{lon_min:.3f}~{lon_max:.3f}]  "
          f"lat [{lat_min:.3f}~{lat_max:.3f}]")
    print(f"[분석] 커버리지: {coverage_km_lon:.0f}km × {coverage_km_lat:.0f}km")

    if coverage_km_lon < 100 or coverage_km_lat < 100:
        print("[경고] 커버리지가 100km 미만입니다. 한국 전체 지형을 원하면")
        print("       Tools/download_dem.py 를 먼저 실행해 전국 DEM을 다운로드하세요.")

    return (nodata, lon_min, lat_min, lon_max, lat_max)


# ────────────────────────────────────────────────────────────
# 유틸리티
# ────────────────────────────────────────────────────────────

def run(cmd: str, desc: str = ""):
    if desc:
        print(f"\n[RUN] {desc}")
    print(f"      {cmd}")
    result = subprocess.run(cmd, shell=True)
    if result.returncode != 0:
        print(f"[ERROR] 명령 실패 (코드 {result.returncode}): {cmd}")
        sys.exit(1)


def check_tool(name: str) -> bool:
    return shutil.which(name) is not None


def check_docker_image():
    result = subprocess.run(
        f"docker image inspect {DOCKER_IMG}",
        shell=True, capture_output=True
    )
    return result.returncode == 0


# ────────────────────────────────────────────────────────────
# 단계별 처리
# ────────────────────────────────────────────────────────────

def step1_convert_to_wgs84(input_path: Path, bounds=None, nodata=NODATA_VAL) -> Path:
    """1단계: 입력 DEM → WGS84 GeoTIFF (nodata 명시 보존)."""
    WORK_DIR.mkdir(exist_ok=True)
    output = WORK_DIR / "wgs84.tif"

    clip_opt = ""
    if bounds:
        lon_min, lat_min, lon_max, lat_max = bounds
        clip_opt = f"-te {lon_min} {lat_min} {lon_max} {lat_max}"

    print("\n[1단계] WGS84 변환 중...")

    result = subprocess.run(
        f'gdalinfo "{input_path}"',
        shell=True, capture_output=True, text=True
    )
    is_wgs84 = "GEOGCS" in result.stdout and "WGS 84" in result.stdout

    if is_wgs84 and not bounds:
        print("      이미 WGS84입니다. 변환 생략.")
        return input_path

    run(
        f'gdalwarp -overwrite -t_srs EPSG:4326 -r bilinear '
        f'-srcnodata {nodata} -dstnodata {nodata} '
        f'{clip_opt} -of GTiff -co COMPRESS=DEFLATE '
        f'"{input_path}" "{output}"',
        "좌표계 변환 (gdalwarp)"
    )
    return output


def step2_generate_tiles(wgs84_tif: Path, max_zoom: int, nodata=NODATA_VAL,
                         valid_bounds=None):
    """2단계: ctb-tile (Docker)로 quantized-mesh 타일 생성."""
    TILES_DIR.mkdir(exist_ok=True)

    print("\n[2단계] quantized-mesh 타일 생성 중...")
    print(f"      최대 줌 레벨: {max_zoom}")
    print(f"      nodata 값   : {nodata}")
    if valid_bounds:
        print(f"      입력 bounds : {[round(v,3) for v in valid_bounds]}")

    work_dir_docker  = str(WORK_DIR.resolve()).replace("\\", "/")
    tiles_dir_docker = str(TILES_DIR.resolve()).replace("\\", "/")
    tif_name = wgs84_tif.name

    # -C : CesiumJS 호환 루트 타일 강제 생성 (스커트 아님)
    # -N : Per-vertex normals (조명 품질 향상)
    # nodata는 GeoTIFF 메타데이터에서 자동으로 읽힘 (-nodata 플래그 불필요)
    # zoom: -s (start=high) -e (end=low) 형식
    run(
        f'docker run --rm '
        f'-v "{work_dir_docker}:/work" '
        f'-v "{tiles_dir_docker}:/tiles" '
        f'{DOCKER_IMG} '
        f'ctb-tile -f Mesh '
        f'-s {max_zoom} -e 0 '
        f'-o /tiles /work/{tif_name}',
        "ctb-tile 실행"
    )


def step3_generate_layer_json(max_zoom: int, bounds):
    """3단계: layer.json 생성 (terrain_server.py 가 tiles URL 교체)."""
    lon_min, lat_min, lon_max, lat_max = bounds

    data = {
        "tilejson":    "2.1.0",
        "format":      "quantized-mesh-1.0",
        "version":     "1.0.0",
        "scheme":      "tms",
        "tiles":       ["{HOST}/{z}/{x}/{y}.terrain"],
        "bounds":      [lon_min, lat_min, lon_max, lat_max],
        "minzoom":     0,
        "maxzoom":     max_zoom,
        "description": "RoadTools 자체 지형 서버",
        "attribution": "RoadTools",
    }

    out = TILES_DIR / "layer.json"
    out.write_text(json.dumps(data, indent=2, ensure_ascii=False), encoding="utf-8")
    print(f"\n[3단계] layer.json 생성: {out}")
    print(f"         bounds: {[round(v,3) for v in bounds]}")


def get_dem_max_height(tif_path: Path) -> float:
    """Return DEM maximum height for low-zoom terrain fallback tiles."""
    result = subprocess.run(
        f'gdalinfo -stats -json "{tif_path}"',
        shell=True, capture_output=True, text=True,
    )
    if result.returncode == 0:
        try:
            info = json.loads(result.stdout)
            bands = info.get("bands", [])
            if bands and bands[0].get("maximum") is not None:
                return float(bands[0]["maximum"])
        except (TypeError, ValueError, json.JSONDecodeError):
            pass
    print("[경고] DEM 최대 고도 감지 실패. 저줌 보정 기본값 9000m 사용.")
    return 9000.0


def fix_low_zoom_tiles(dem_max_height: float) -> int:
    """Replace low-zoom ctb-tile output with stable flat quantized-mesh tiles."""
    replaced = 0
    if not TILES_DIR.exists():
        return replaced

    for z_dir in sorted(TILES_DIR.iterdir(), key=lambda d: int(d.name) if d.name.isdigit() else 999):
        if not z_dir.is_dir() or not z_dir.name.isdigit():
            continue
        z = int(z_dir.name)
        if z not in FLAT_ZOOM_LEVELS and z > LOW_ZOOM_FIX_THRESHOLD:
            continue

        for x_dir in z_dir.iterdir():
            if not x_dir.is_dir() or not x_dir.name.isdigit():
                continue
            x = int(x_dir.name)
            for tile_file in x_dir.glob("*.terrain"):
                try:
                    y = int(tile_file.stem)
                except ValueError:
                    continue
                tile_file.write_bytes(make_flat_tile(z, x, y, h_max=dem_max_height))
                replaced += 1

    return replaced


def step4_verify_tiles():
    """4단계: 생성된 타일에서 실제 고도 데이터 검증."""
    import gzip as gz

    print("\n[4단계] 타일 고도 데이터 검증 중...")
    terrain_files = list(TILES_DIR.rglob("*.terrain"))
    if not terrain_files:
        print("[경고] .terrain 파일이 없습니다.")
        return

    # 고해상도 줌 레벨 타일 샘플 검사
    max_z = max(int(p.parts[-3]) for p in terrain_files if p.parts[-3].isdigit())
    high_zoom = [p for p in terrain_files if p.parts[-3] == str(max_z)]
    sample = high_zoom[:min(20, len(high_zoom))]

    non_flat = 0
    max_elev_seen = 0.0
    for p in sample:
        raw = p.read_bytes()
        data = gz.decompress(raw) if raw[:2] == b"\x1f\x8b" else raw
        if len(data) < 32:
            continue
        mh, Mh = struct.unpack_from("<ff", data, 24)
        h_range = Mh - mh
        if h_range > 1.0:
            non_flat += 1
            max_elev_seen = max(max_elev_seen, Mh)

    total = len(sample)
    print(f"   줌={max_z}  샘플={total}개  고도 있는 타일={non_flat}개  "
          f"최대고도≈{max_elev_seen:.0f}m")
    if non_flat == 0:
        print("[경고] 고도 데이터가 없습니다. 원인:")
        print("       1) 원본 DEM 커버리지 부족 → download_dem.py 실행")
        print("       2) nodata 값 불일치      → --nodata 옵션으로 직접 지정")
    else:
        pct = non_flat / total * 100
        print(f"[OK] {pct:.0f}% 타일에 고도 데이터 있음")


# ────────────────────────────────────────────────────────────
# 데이터 소스 안내
# ────────────────────────────────────────────────────────────

DATA_SOURCES = """
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  무료 DEM 데이터 소스
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

  [한국 전국 — 자동 다운로드]
  python Tools/download_dem.py            # Copernicus 30m 자동 다운로드
  python Tools/download_dem.py --korea    # 한국 영역 한정

  [한국 고정밀]
  국토지리정보원 (NGII) — 5m / 1m DEM
    https://map.ngii.go.kr/ms/pblictn/ntn.do
    ※ 회원가입 후 '수치표고모델' 검색

  [전 세계 30m]
  Copernicus DEM 30m (ESA/AWS) — 무료, 무인증
    → Tools/download_dem.py 로 자동 취득

  SRTM 30m (NASA) — USGS EarthExplorer (로그인 필요)
    https://earthexplorer.usgs.gov/

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  필수 도구 설치
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

  1. Docker Desktop  https://www.docker.com/products/docker-desktop/
  2. GDAL (Windows)  conda install -c conda-forge gdal
                     또는 OSGeo4W  https://trac.osgeo.org/osgeo4w/

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  사용 예시
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

  # 1) 전국 DEM 자동 다운로드 + 타일 생성 (원스텝)
  python Tools/download_dem.py --process --max-zoom 12

  # 2) 이미 DEM이 있을 때
  python Tools/process_dem.py --input path/to/dem.tif --max-zoom 12

  # 3) 커버리지 제한 (한국 남부만, 빠름)
  python Tools/process_dem.py --input dem.tif --bounds 126 34 130 37 --max-zoom 14

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
"""


# ────────────────────────────────────────────────────────────
# 메인
# ────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(
        description="DEM → quantized-mesh 타일 변환",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument("--input",    type=Path, help="입력 DEM 파일 경로 (.tif/.hgt/.img)")
    parser.add_argument("--bounds",   type=float, nargs=4,
                        metavar=("LON_MIN", "LAT_MIN", "LON_MAX", "LAT_MAX"),
                        help="처리 영역 (미지정 시 DEM에서 자동 감지)")
    parser.add_argument("--nodata",   type=float, default=None,
                        help=f"노데이터 값 (미지정 시 DEM에서 자동 감지, 기본 {NODATA_VAL})")
    parser.add_argument("--max-zoom", type=int, default=12,
                        help="최대 줌 레벨 (기본 12)")
    parser.add_argument("--info",     action="store_true",
                        help="데이터 소스 및 설치 안내 출력")
    args = parser.parse_args()

    if args.info or not args.input:
        print(DATA_SOURCES)
        if not args.input:
            return

    input_path = args.input.resolve()
    if not input_path.exists():
        print(f"[ERROR] 입력 파일 없음: {input_path}")
        sys.exit(1)

    # 도구 확인
    print("\n[도구 확인]")
    gdal_ok   = check_tool("gdalwarp")
    docker_ok = check_tool("docker")
    print(f"  GDAL   : {'OK' if gdal_ok   else 'MISSING (--info 참조)'}")
    print(f"  Docker : {'OK' if docker_ok else 'MISSING (--info 참조)'}")

    if not docker_ok:
        print("\nDocker가 필요합니다. --info 옵션으로 설치 안내를 확인하세요.")
        sys.exit(1)

    # nodata / bounds 자동 감지
    if input_path.suffix.lower() in (".tif", ".tiff"):
        detected_nodata, d_lon_min, d_lat_min, d_lon_max, d_lat_max = \
            detect_nodata_and_bounds(input_path)
    else:
        detected_nodata = NODATA_VAL
        d_lon_min = d_lat_min = d_lon_max = d_lat_max = None

    nodata = args.nodata if args.nodata is not None else detected_nodata
    source_bounds = None
    if all(v is not None for v in [d_lon_min, d_lat_min, d_lon_max, d_lat_max]):
        source_bounds = [d_lon_min, d_lat_min, d_lon_max, d_lat_max]
        print(f"[자동 감지] source bounds = {[round(v,3) for v in source_bounds]}")

    # 자동 감지 bounds는 원본 CRS 기준일 수 있으므로 gdalwarp -te에는 사용하지 않는다.
    # 사용자가 명시한 bounds만 EPSG:4326 clipping 범위로 처리한다.
    clip_bounds = args.bounds

    # ctb-tile Docker 이미지 확인
    if not check_docker_image():
        print(f"\n[!] Docker 이미지 없음. 다운로드 중: {DOCKER_IMG}")
        run(f"docker pull {DOCKER_IMG}", "ctb-tile 이미지 다운로드")

    # 기존 tiles 정리 (잘못 생성된 타일 제거)
    print(f"\n[정리] 기존 tiles 폴더 내용 삭제 중...")
    clear_tiles_dir()

    # 변환 실행
    if gdal_ok:
        wgs84 = step1_convert_to_wgs84(input_path, clip_bounds, nodata)
    else:
        print("[경고] GDAL 없음 — WGS84 변환 생략. 입력 파일을 직접 사용합니다.")
        wgs84 = input_path

    terrain_bounds = clip_bounds
    if wgs84.suffix.lower() in (".tif", ".tiff"):
        _, w_lon_min, w_lat_min, w_lon_max, w_lat_max = detect_nodata_and_bounds(wgs84)
        if all(v is not None for v in [w_lon_min, w_lat_min, w_lon_max, w_lat_max]):
            terrain_bounds = [w_lon_min, w_lat_min, w_lon_max, w_lat_max]

    step2_generate_tiles(wgs84, args.max_zoom, nodata, terrain_bounds)

    dem_max_height = get_dem_max_height(wgs84)
    print(f"\n[저줌 보정] DEM 최대 고도: {dem_max_height:.1f}m")
    replaced = fix_low_zoom_tiles(dem_max_height)
    print(f"[저줌 보정] 교체된 타일: {replaced}개 (z<={LOW_ZOOM_FIX_THRESHOLD}, z={sorted(FLAT_ZOOM_LEVELS)})")

    step3_generate_layer_json(args.max_zoom, terrain_bounds or [-180, -90, 180, 90])
    step4_verify_tiles()

    tile_count = sum(1 for _ in TILES_DIR.rglob("*.terrain"))
    print(f"""
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  변환 완료
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  출력 타일 : {tile_count}개
  최대 줌   : {args.max_zoom}
  nodata    : {nodata}

  다음 단계:
    python Tools/terrain_server.py
    → Unity TerrainSourceSwitcher > Terrain Url
      http://localhost:5001/layer.json
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
""")


if __name__ == "__main__":
    main()
