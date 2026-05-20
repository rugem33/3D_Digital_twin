#!/usr/bin/env python3
"""
RoadTools 자체 호스팅 quantized-mesh 지형 서버 (프로덕션)

사용법:
    pip install flask
    python Tools/terrain_server.py

타일 폴더 구조 (ctb-tile 출력):
    Tools/tiles/{z}/{x}/{y}.terrain
    Tools/tiles/layer.json  (있으면 자동 사용, 없으면 자동 생성)

타일이 없는 영역은 고도 0m 평탄 타일로 자동 폴백.

Unity 연결:
    TerrainSourceSwitcher > Mode = CustomUrl
    TerrainSourceSwitcher > Terrain Url = http://localhost:5001/layer.json

    모바일 기기에서 테스트 시:
    PC IP 주소로 교체 (예: http://192.168.1.100:5001/layer.json)
"""

import struct
import math
import json
from pathlib import Path
from flask import Flask, Response, request

app  = Flask(__name__)
PORT = 5001

# 타일 파일 저장 위치 (process_dem.py 출력과 동일)
TILES_DIR = Path(__file__).parent / "tiles"


# ────────────────────────────────────────────────────────────
# 좌표 유틸리티
# ────────────────────────────────────────────────────────────

def tile_bounds(z: int, x: int, y_tms: int):
    """Geographic TMS 타일 좌표 → (lon_min, lat_min, lon_max, lat_max)"""
    x_count = 2 ** (z + 1)
    y_count = 2 ** z
    lon_min = (x       / x_count) * 360.0 - 180.0
    lon_max = ((x + 1) / x_count) * 360.0 - 180.0
    lat_min = (y_tms       / y_count) * 180.0 - 90.0
    lat_max = ((y_tms + 1) / y_count) * 180.0 - 90.0
    return lon_min, lat_min, lon_max, lat_max


def to_ecef(lon_deg: float, lat_deg: float, h: float = 0.0):
    """WGS84 경위도 → ECEF"""
    a  = 6378137.0
    f  = 1.0 / 298.257223563
    e2 = 2 * f - f * f
    lon = math.radians(lon_deg)
    lat = math.radians(lat_deg)
    N   = a / math.sqrt(1 - e2 * math.sin(lat) ** 2)
    return (
        (N + h) * math.cos(lat) * math.cos(lon),
        (N + h) * math.cos(lat) * math.sin(lon),
        (N * (1 - e2) + h) * math.sin(lat),
    )


# ────────────────────────────────────────────────────────────
# quantized-mesh 평탄 타일 생성 (폴백용)
# ────────────────────────────────────────────────────────────

def _zigzag(delta: int) -> int:
    return delta * 2 if delta >= 0 else -delta * 2 - 1

def _delta_zigzag(values: list) -> list:
    out, prev = [], 0
    for v in values:
        out.append(_zigzag(v - prev))
        prev = v
    return out

def make_flat_tile(z: int, x: int, y: int) -> bytes:
    """고도 0m 평탄 quantized-mesh 타일"""
    lon_min, lat_min, lon_max, lat_max = tile_bounds(z, x, y)
    lon_c = (lon_min + lon_max) / 2
    lat_c = (lat_min + lat_max) / 2

    cx, cy, cz = to_ecef(lon_c, lat_c)
    kx, ky, kz = to_ecef(lon_min, lat_min)
    radius = max(math.dist((cx, cy, cz), (kx, ky, kz)), 1.0)
    a = 6378137.0

    hdr  = struct.pack('<ddd',  cx, cy, cz)
    hdr += struct.pack('<ff',   0.0, 0.0)
    hdr += struct.pack('<dddd', cx, cy, cz, radius)
    hdr += struct.pack('<ddd',  cx / a, cy / a, cz / a)

    u = [0, 32767, 0, 32767]
    v = [0, 0, 32767, 32767]
    h = [0, 0, 0, 0]
    vdata  = struct.pack('<I', 4)
    vdata += struct.pack('<4H', *_delta_zigzag(u))
    vdata += struct.pack('<4H', *_delta_zigzag(v))
    vdata += struct.pack('<4H', *_delta_zigzag(h))

    idata  = struct.pack('<I', 2)
    idata += struct.pack('<6H', 0, 1, 2, 1, 3, 2)

    def edge(a, b):
        return struct.pack('<I', 2) + struct.pack('<2I', a, b)

    edata = edge(0, 2) + edge(0, 1) + edge(1, 3) + edge(2, 3)
    return hdr + vdata + idata + edata


# ────────────────────────────────────────────────────────────
# 타일 폴더 분석
# ────────────────────────────────────────────────────────────

def _scan_tiles():
    """tiles/ 폴더에서 줌 레벨 범위와 타일 수 반환"""
    if not TILES_DIR.exists():
        return 0, 0, 0

    zoom_levels = set()
    count = 0
    for f in TILES_DIR.rglob("*.terrain"):
        try:
            zoom_levels.add(int(f.parts[-3]))
            count += 1
        except (ValueError, IndexError):
            pass

    if not zoom_levels:
        return 0, 0, 0
    return min(zoom_levels), max(zoom_levels), count


# ────────────────────────────────────────────────────────────
# Flask 엔드포인트
# ────────────────────────────────────────────────────────────

@app.after_request
def cors(resp):
    resp.headers["Access-Control-Allow-Origin"]  = "*"
    resp.headers["Access-Control-Allow-Headers"] = "*"
    return resp


@app.route("/layer.json")
def layer_json():
    # tiles/layer.json 이 있으면 그걸 우선 사용 (ctb-tile 생성본)
    ctb_meta = TILES_DIR / "layer.json"
    if ctb_meta.exists():
        data = json.loads(ctb_meta.read_text(encoding="utf-8"))
        host = request.host_url.rstrip("/")
        # tiles URL만 현재 서버 주소로 교체
        data["tiles"] = [f"{host}/{{z}}/{{x}}/{{y}}.terrain"]
        print("[META] ctb layer.json 제공")
        return Response(json.dumps(data, indent=2), content_type="application/json")

    # 없으면 자동 생성
    min_z, max_z, _ = _scan_tiles()
    host = request.host_url.rstrip("/")
    data = {
        "tilejson":    "2.1.0",
        "format":      "quantized-mesh-1.0",
        "version":     "1.0.0",
        "scheme":      "tms",
        "tiles":       [f"{host}/{{z}}/{{x}}/{{y}}.terrain"],
        "bounds":      [-180.0, -90.0, 180.0, 90.0],
        "minzoom":     min_z,
        "maxzoom":     max(max_z, 12),
        "description": "RoadTools 자체 지형 서버",
        "attribution": "RoadTools",
    }
    print("[META] 자동 생성 layer.json 제공")
    return Response(json.dumps(data, indent=2), content_type="application/json")


@app.route("/<int:z>/<int:x>/<int:y>.terrain")
def tile(z, x, y):
    tile_path = TILES_DIR / str(z) / str(x) / f"{y}.terrain"

    if tile_path.exists():
        raw = tile_path.read_bytes()
        is_gzip = raw[:2] == b'\x1f\x8b'
        headers = {"Content-Encoding": "gzip"} if is_gzip else {}
        print(f"[FILE] z={z} x={x} y={y} {'(gzip)' if is_gzip else ''}")
        return Response(
            raw,
            content_type="application/vnd.quantized-mesh",
            headers=headers,
        )

    # 타일 없음 → 평탄 폴백
    lon_min, lat_min, lon_max, lat_max = tile_bounds(z, x, y)
    print(f"[FLAT] z={z} x={x} y={y}  "
          f"lon[{lon_min:.1f}~{lon_max:.1f}] lat[{lat_min:.1f}~{lat_max:.1f}]")
    return Response(
        make_flat_tile(z, x, y),
        content_type="application/vnd.quantized-mesh",
    )


@app.route("/status")
def status():
    min_z, max_z, count = _scan_tiles()
    return Response(json.dumps({
        "status":     "ok",
        "tiles_dir":  str(TILES_DIR),
        "tile_count": count,
        "min_zoom":   min_z,
        "max_zoom":   max_z,
        "layer_json": (TILES_DIR / "layer.json").exists(),
    }, indent=2), content_type="application/json")


@app.route("/health")
def health():
    return Response('{"status":"ok"}', content_type="application/json")


# ────────────────────────────────────────────────────────────
# 진입점
# ────────────────────────────────────────────────────────────

if __name__ == "__main__":
    TILES_DIR.mkdir(exist_ok=True)
    min_z, max_z, count = _scan_tiles()

    print("=" * 60)
    print("  RoadTools 자체 지형 서버")
    print("=" * 60)
    print(f"  layer.json  : http://localhost:{PORT}/layer.json")
    print(f"  상태 확인   : http://localhost:{PORT}/status")
    print(f"  타일 폴더   : {TILES_DIR.resolve()}")
    print(f"  저장 타일   : {count}개  (줌 {min_z}~{max_z})")
    print()
    if count == 0:
        print("  [!] tiles/ 폴더에 타일이 없습니다.")
        print("      모든 요청에 고도 0m 평탄 타일로 응답합니다.")
        print("      process_dem.py 를 먼저 실행하세요.")
    print("=" * 60)
    app.run(host="0.0.0.0", port=PORT, debug=False)
