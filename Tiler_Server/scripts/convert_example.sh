#!/usr/bin/env bash
set -euo pipefail

curl -X POST "http://localhost:8000/convert" \
  -F "shp_zip=@sample.zip" \
  -F 'attributes={"name":"sample","height":10}' \
  -F "height_column=height" \
  -F "coordinate_code=5186" \
  -F "skirt_height=10.0"
