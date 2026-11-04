#!/bin/sh
set -e

# Fail fast on missing required values rather than serve a broken bundle.
: "${FSH_API_URL:?FSH_API_URL is required (e.g. https://api.example.com)}"
: "${FSH_DASHBOARD_URL:?FSH_DASHBOARD_URL is required (e.g. https://app.example.com)}"

# Defaults for non-required values.
: "${FSH_DEFAULT_TENANT:=root}"

export FSH_API_URL FSH_DASHBOARD_URL FSH_DEFAULT_TENANT

# FSH_API_URL lands verbatim in the CSP: a value with a path would become a path source that blocks /api/v1
# once the policy is enforced, and a ';' would inject directives. Accept a bare origin only, else refuse to start.
fsh_api_url_ok=no
case "$FSH_API_URL" in
  *[!A-Za-z0-9.:/-]*) ;;
  *) if printf '%s' "$FSH_API_URL" | grep -Eq '^https?://[A-Za-z0-9.-]+(:[0-9]+)?/?$'; then fsh_api_url_ok=yes; fi ;;
esac
if [ "$fsh_api_url_ok" != yes ]; then
  echo "FSH_API_URL must be a bare origin like https://api.example.com (no path, query, spaces or line breaks); got: $FSH_API_URL" >&2
  exit 1
fi

# Render the runtime config into nginx's web root on every start. The template
# lives outside the web root, so it is never served and survives a restart.
envsubst < /etc/fsh/config.json.template > /usr/share/nginx/html/config.json

# The CSP names the environment's API: its origin for fetch and its ws(s) form for the SignalR hubs. Only these
# two variables are substituted, so nothing else in the template is touched.
FSH_API_ORIGIN="${FSH_API_URL%/}"
FSH_API_WS_URL="$(printf '%s' "$FSH_API_ORIGIN" | sed -e 's#^https://#wss://#' -e 's#^http://#ws://#')"
FSH_API_URL="$FSH_API_ORIGIN" FSH_API_WS_URL="$FSH_API_WS_URL" \
  envsubst '${FSH_API_URL} ${FSH_API_WS_URL}' < /etc/fsh/csp.conf.template > /etc/nginx/snippets/csp.conf

exec nginx -g 'daemon off;'
