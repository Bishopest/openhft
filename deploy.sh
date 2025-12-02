#!/bin/bash

set -e # 오류 발생 시 즉시 스크립트 중단

# --- 1. 스크립트 인수 확인 ---
if [ -z "$1" ]; then
  echo "❌ Error: Deployment target environment is not specified."
  echo "Usage: ./deploy.sh <environment_name>"
  echo "Example: ./deploy.sh staging"
  exit 1
fi

TARGET_ENV=$1
ENV_FILE="scripts/env/${TARGET_ENV}.env"

if [ ! -f "$ENV_FILE" ]; then
  echo "❌ Error: Environment file not found at ${ENV_FILE}"
  exit 1
fi

# --- 2. 환경 설정 파일 로드 ---
echo " sourcing environment variables from ${ENV_FILE}"
source "$ENV_FILE"

# --- 3. 프로젝트 정보 (이 부분은 보통 고정) ---
PROJECT_NAME="OpenHFT.Oms"
PROJECT_CSPROJ_PATH="src/OpenHFT.Oms/OpenHFT.Oms.csproj"
TARGET_FRAMEWORK="net8.0"

SOLUTION_ROOT_DIR=$(pwd)
LOCAL_PUBLISH_DIR_RELATIVE="src/OpenHFT.Oms/bin/Release/${TARGET_FRAMEWORK}/publish/"
LOCAL_PUBLISH_DIR_ABSOLUTE="${SOLUTION_ROOT_DIR}/${LOCAL_PUBLISH_DIR_RELATIVE}"
LOCAL_DATA_SOURCE_DIR="${SOLUTION_ROOT_DIR}/data"

echo "🚀 Starting deployment to '$TARGET_ENV'..."
echo "   - Host: $EC2_HOST"
echo "   - Remote Dir: $REMOTE_APP_DIR"
echo "   - OMS Identifier: $OMS_IDENTIFIER"

# --- 4. 로컬 빌드 및 게시 ---
echo "📦 Building and publishing the application..."
dotnet publish "$PROJECT_CSPROJ_PATH" -c Release

# --- 5. 데이터 파일 복사 ---
echo "📑 Copying data files..."
mkdir -p "${LOCAL_PUBLISH_DIR_ABSOLUTE}/data"
cp "${LOCAL_DATA_SOURCE_DIR}/instruments.csv" "${LOCAL_PUBLISH_DIR_ABSOLUTE}/data/"
cp "${LOCAL_DATA_SOURCE_DIR}/book_info.json" "${LOCAL_PUBLISH_DIR_ABSOLUTE}/data/"

# --- 6. 배포용 config.json 수정 ---
echo "🔧 Modifying config.json for '$TARGET_ENV' environment..."
CONFIG_FILE_PATH="${LOCAL_PUBLISH_DIR_ABSOLUTE}/config.json"

# jq 설치 확인
if ! command -v jq &> /dev/null; then
    echo "❌ Error: 'jq' is not installed. Please install it (e.g., brew install jq) to parse JSON."
    exit 1
fi

# Function to handle sed compatibility
# modify_config() {
#   local key=$1
#   local value=$2
#   local file=$3

#   # sed 명령어는 JSON 구조를 완벽하게 파싱하지 못하므로, 단순 치환에만 적합합니다.
#   # "key": "any value" 형태를 찾아서 "key": "new value"로 바꿉니다.
#   local pattern="s|\"${key}\": \".*\"|\"${key}\": \"${value}\"|g"

#   if [[ "$OSTYPE" == "darwin"* ]]; then
#     sed -i '' "$pattern" "$file"
#   else
#     sed -i "$pattern" "$file"
#   fi
# }

# dataFolder와 omsIdentifier 값을 동적으로 수정
# modify_config "dataFolder" "data" "$CONFIG_FILE_PATH"
# modify_config "omsIdentifier" "$OMS_IDENTIFIER" "$CONFIG_FILE_PATH"

# jq를 사용하여 omsIdentifier, dataFolder, subscriptions를 한 번에 업데이트
# --arg: 일반 문자열 변수 주입
# --argjson: JSON 객체/배열 변수 주입
# echo "   -> dataFolder set to 'data'"
# echo "   -> omsIdentifier set to '$OMS_IDENTIFIER'"

echo "   -> Updating configuration using jq..."

tmp=$(mktemp)
jq --arg oms "$OMS_IDENTIFIER" \
   --argjson subs "$SUBSCRIPTIONS_JSON" \
   '.omsIdentifier = $oms | .dataFolder = "data" | .subscriptions = $subs' \
   "$CONFIG_FILE_PATH" > "$tmp" && mv "$tmp" "$CONFIG_FILE_PATH"

echo "   -> config.json updated successfully."
echo "      - omsIdentifier: $OMS_IDENTIFIER"
echo "      - subscriptions updated from env."


# --- 7. EC2 인스턴스로 아티팩트 복사 ---
echo "📡 Uploading artifacts to $EC2_HOST..."
rsync -avz --delete -e "ssh -i $PEM_KEY_PATH" "$LOCAL_PUBLISH_DIR_ABSOLUTE/" "${EC2_USER}@${EC2_HOST}:${REMOTE_APP_DIR}"

echo "✅ Deployment successful!"
echo "➡️ You can now SSH into the instance to start/restart the application:"
echo "ssh -i $PEM_KEY_PATH ${EC2_USER}@${EC2_HOST}"
