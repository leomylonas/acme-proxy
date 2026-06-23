#!/usr/bin/env bash
#
# Runs the live Let's Encrypt *staging* end-to-end test against a real HestiaCP-managed
# domain. Credentials are read from src/AcmeProxy/appsettings.user.json (git-ignored) so you
# don't have to re-enter them — the live test itself is configured from environment variables,
# which this script derives from that file.
#
# Usage:  tests/AcmeProxy.E2ETests/run-live-e2e.sh
#
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
settings="$repo_root/src/AcmeProxy/appsettings.user.json"

if ! command -v jq >/dev/null 2>&1; then
	echo "error: 'jq' is required to read $settings" >&2
	exit 1
fi

if [[ ! -f "$settings" ]]; then
	echo "error: $settings not found — add your HestiaCP credentials there first." >&2
	exit 1
fi

get() { jq -er "$1" "$settings"; }

# Map appsettings.user.json -> the env vars the live e2e test reads.
export ACMEPROXY_E2E_STAGING=1
export E2E_DOMAIN="$(get '.Proxy.AllowedDomains[0]')"
export E2E_ACCOUNT_EMAIL="$(get '.Proxy.LetsEncrypt.AccountEmail')"
export E2E_HESTIA_BASEURL="$(get '.Proxy.HestiaCP.BaseUrl')"
export E2E_HESTIA_ACCESSKEY="$(get '.Proxy.HestiaCP.AccessKey')"
export E2E_HESTIA_SECRETKEY="$(get '.Proxy.HestiaCP.SecretKey')"
export E2E_HESTIA_USERNAME="$(get '.Proxy.HestiaCP.Username')"

echo "Running live staging e2e for domain '$E2E_DOMAIN' (Hestia user '$E2E_HESTIA_USERNAME')..."
echo "Note: real DNS propagation + LE staging validation can take a few minutes."

dotnet test "$script_dir/AcmeProxy.E2ETests.csproj" \
	--filter "FullyQualifiedName~LiveStagingEndToEndTests" \
	--logger "console;verbosity=detailed" \
	"$@"
