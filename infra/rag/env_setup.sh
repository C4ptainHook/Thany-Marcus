#! /bin/bash

set -e
echo "Starting setup ⚙️"

apt-get update
apt-get install -y python3.12-venv python3-pip git

PROJECT_DIR="/root/marcus"
mkdir -p ${PROJECT_DIR}

echo "Project directory created at ${PROJECT_DIR} ☑️"

VENV_DIR="${PROJECT_DIR}/venv"
if [ ! -d "${VENV_DIR}" ]; then
  python3 -m venv ${VENV_DIR}
fi

REQUIREMENTS_FILE="${PROJECT_DIR}/requirements.txt"
cat > ${REQUIREMENTS_FILE} << EOF
pymilvus
google-generativeai
numpy
EOF

source ${VENV_DIR}/bin/activate
pip install -r ${REQUIREMENTS_FILE}
deactivate

echo "Required packages installed. ☑️"

echo "Setup complete! 🎉"