# Tiler Server

Unity에서 SHP 파일과 속성값을 보내면 서버가 `mago-3d-tiler`를 실행해 3D Tiles 산출물(`b3dm`, `tileset.json`)을 만들고 URL을 반환하는 Python/Flask 서버입니다. Docker 환경을 기준으로 구성했습니다.

## 구조

```text
.
├── app/                    # Flask API 서버
│   ├── main.py             # /convert, /outputs, /health
│   └── services/           # 업로드, 압축, mago tiler 실행 로직
├── tools/                  # DEM/terrain 관련 기존 도구
│   ├── download_dem.py
│   ├── process_dem.py
│   ├── terrain_server.py
│   └── terrain_test_server.py
├── data/
│   ├── uploads/            # 요청별 업로드 저장
│   ├── outputs/            # 요청별 b3dm/tileset 결과 저장
│   └── terrain_tiles/      # DEM terrain tile 확장용
├── vendor/                 # mago-3d-tiler jar 배치
├── Dockerfile
└── docker-compose.yml
```

## 준비

`mago-3d-tiler-1.15.4.jar` 파일은 프로젝트에 포함되어 있으며 아래 위치에 둡니다.

```bash
vendor/mago-3d-tiler-1.15.4.jar
```

Docker 이미지 빌드 시 이 jar가 `/opt/mago/mago-3d-tiler-1.15.4.jar`로 복사됩니다.

## 실행

```bash
docker compose up --build
```

상태 확인:

```bash
curl http://localhost:8000/health
curl http://localhost:8000/status
```

## 변환 API

`POST /api/convert`

기존 호환용으로 `POST /convert`도 동일하게 동작합니다.

요청은 `multipart/form-data`입니다.

| 필드 | 설명 |
| --- | --- |
| `files[]` | `.shp/.dbf/.shx/.prj` 개별 업로드 또는 shapefile zip |
| `demFile` | 선택 사항. mago `-te` 옵션에 들어갈 GeoTIFF terrain/DEM 파일 |
| `outputType` | `b3dm`, `i3dm`, `pnts` 중 하나 |
| `curvatureCorrection` | `true`이면 mago `-cc` 적용 |
| `coordinateSystem` | EPSG 코드, 예: `5186` |
| `heightColumn` | mago 옵션 `-hc`, 기본 `height` |
| `scaleHeight` | mago 옵션 `-sh`, 기본 `10.0` |
| `shp_zip` | 기존 호환 필드. `.shp/.shx/.dbf/.prj`가 들어있는 zip |
| `files` | 기존 호환 필드. zip 대신 여러 shapefile 구성 파일을 반복 업로드할 때 사용 |
| `terrain_tif` | 기존 호환 필드. GeoTIFF terrain 파일 |
| `attributes` | Unity에서 넘기는 속성 JSON 문자열 |
| `attributes_file` | 속성 JSON 파일 |

예시:

```bash
curl -X POST "http://localhost:8000/api/convert" \
  -F "files[]=@building.zip" \
  -F "demFile=@output_file.tif" \
  -F "outputType=b3dm" \
  -F "curvatureCorrection=true" \
  -F "coordinateSystem=5186" \
  -F "heightColumn=height" \
  -F "scaleHeight=10.0" \
  -F 'attributes={"project":"demo","height":12.5}' \
```

응답:

```json
{
  "job_id": "요청 ID",
  "jobId": "요청 ID",
  "status": "running",
  "statusUrl": "http://localhost:8000/api/jobs/<job_id>/status",
  "logsUrl": "http://localhost:8000/api/jobs/<job_id>/logs",
  "stdoutLogUrl": "http://localhost:8000/outputs/<job_id>/mago_stdout.log",
  "stderrLogUrl": "http://localhost:8000/outputs/<job_id>/mago_stderr.log"
}
```

Unity는 `statusUrl`을 1초 간격으로 polling 하다가 `status=completed`가 되면
응답의 `tilesetUrl` 또는 `zipUrl`을 받아 사용하면 됩니다.

진행 상태 조회:

```bash
curl "http://localhost:8000/api/jobs/<job_id>/status"
```

응답 예시:

```json
{
  "jobId": "요청 ID",
  "status": "running",
  "stage": "postprocess",
  "progress": 83,
  "progressText": "[Post][1200/1956] post-process in progress : ..."
}
```

로그 조회:

```bash
curl "http://localhost:8000/api/jobs/<job_id>/logs?maxChars=12000"
```

웹 클라이언트에서는 SSE 스트림도 사용할 수 있습니다.

```bash
curl "http://localhost:8000/api/jobs/<job_id>/events"
```

호환용 동기 API `POST /convert`는 기존처럼 변환 완료 후 한 번에 결과를 반환합니다.

## mago3d-tiler 실행 명령

서버는 내부적으로 아래 형태의 명령을 실행합니다.

```bash
java -jar /opt/mago/mago-3d-tiler-1.15.4.jar \
  -i /data/uploads/<job_id> \
  -o /data/outputs/<job_id> \
  -it shp \
  -ot b3dm \
  -cc \
  -c 5186 \
  -te /data/uploads/<job_id>/output_file.tif \
  -hc height \
  -sh 10.0
```

## DEM/Terrain 도구

제공된 DEM 관련 파일은 `tools/`에 포함했습니다.

```bash
python tools/download_dem.py --process --max-zoom 12
python tools/process_dem.py --input path/to/dem.tif --max-zoom 12
python tools/terrain_server.py
```

이 도구들은 quantized-mesh terrain 서버가 필요할 때 별도 확장 파트로 사용할 수 있습니다.
