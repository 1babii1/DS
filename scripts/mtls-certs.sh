#!/usr/bin/env bash
# Makes the certificates for mutual TLS between services (ADR 0044) with openssl, for a local stack or a test. Not for production:
# the authority's key sits next to the certificates and nothing rotates anything.
#
#   scripts/mtls-certs.sh [directory]          # default docker/certs (ignored by git)
#
# Writes ca.pem, and <service>.pem + <service>.key for each service named below. The server certificates carry the service's
# docker-compose / cluster host name as a subject alternative name: a client checks the name as well as the signature.
set -euo pipefail
out="${1:-$(dirname "$0")/../docker/certs}"
mkdir -p "$out"
cd "$out"

openssl ecparam -name prime256v1 -genkey -noout -out ca.key
openssl req -x509 -new -key ca.key -sha256 -days 365 -subj "/CN=platform-ca" -out ca.pem \
  -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign,cRLSign"

# service  host-name-or-empty
issue() {
  local name="$1" host="$2" usage="clientAuth"
  [ -n "$host" ] && usage="serverAuth,clientAuth"
  openssl ecparam -name prime256v1 -genkey -noout -out "$name.sec1.key"
  openssl pkcs8 -topk8 -nocrypt -in "$name.sec1.key" -out "$name.key"
  rm "$name.sec1.key"
  openssl req -new -key "$name.key" -subj "/CN=$name" -out "$name.csr"
  {
    echo "basicConstraints=CA:FALSE"
    echo "keyUsage=digitalSignature"
    echo "extendedKeyUsage=$usage"
    [ -n "$host" ] && echo "subjectAltName=DNS:$host,DNS:localhost,IP:127.0.0.1"
  } > "$name.ext"
  openssl x509 -req -in "$name.csr" -CA ca.pem -CAkey ca.key -CAcreateserial -days 30 -sha256 -extfile "$name.ext" -out "$name.pem" 2>/dev/null
  rm "$name.csr" "$name.ext"
}

issue directory_service directory_service
issue employee_service ""

rm -f ca.srl
chmod 600 ./*.key
ls
